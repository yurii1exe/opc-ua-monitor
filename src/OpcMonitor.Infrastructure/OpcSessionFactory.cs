using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace OpcMonitor.Infrastructure;

/// <summary>
/// Builds the OPC UA application configuration, ensures an application instance
/// certificate exists, selects an endpoint and opens a session.
/// </summary>
/// <remarks>
/// The configuration is built in code rather than loaded from the SDK's XML
/// config file. That keeps every security-relevant decision — the application
/// URI, the certificate store locations, whether untrusted server certificates
/// are accepted — visible in one reviewable place instead of spread across an
/// XML document that tends to get copied between projects unread.
/// </remarks>
public sealed class OpcSessionFactory
{
    private readonly OpcClientOptions _options;
    private readonly ITelemetryContext _telemetry;
    private readonly ILogger<OpcSessionFactory> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private ApplicationConfiguration? _configuration;

    public OpcSessionFactory(
        IOptions<OpcClientOptions> options,
        ITelemetryContext telemetry,
        ILogger<OpcSessionFactory> logger)
    {
        _options = options.Value;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>Endpoint URL as configured, before any server round-trip.</summary>
    public string RequestedEndpointUrl => _options.EndpointUrl;

    /// <summary>
    /// Creates (once) the application configuration and the application instance
    /// certificate. Safe to call repeatedly; subsequent calls return the cached
    /// configuration, which matters because reconnects must not regenerate the
    /// client's identity.
    /// </summary>
    public async Task<ApplicationConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null) return _configuration;

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _configuration ??= await BuildConfigurationAsync(cancellationToken).ConfigureAwait(false);
            return _configuration;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<ApplicationConfiguration> BuildConfigurationAsync(CancellationToken cancellationToken)
    {
        var pkiRoot = ResolvePkiRoot(_options.PkiRoot);
        Directory.CreateDirectory(pkiRoot);

        var application = new ApplicationInstance(_telemetry)
        {
            ApplicationName = _options.ApplicationName,
            ApplicationType = ApplicationType.Client
        };

        _logger.LogInformation(
            "Building OPC UA client configuration. ApplicationUri={ApplicationUri} PkiRoot={PkiRoot}",
            _options.ApplicationUri, pkiRoot);

        // A single RSA/SHA-256 application instance certificate, stored as a
        // directory store under the gitignored pki root. Directory rather than
        // an OS certificate store so the identity travels with the container and
        // never lands in the developer's personal store.
        var certificates = new CertificateIdentifierCollection
        {
            new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pkiRoot, "own"),
                SubjectName = _options.CertificateSubjectName,
                CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
            }
        };

        var configuration = await application
            .Build(_options.ApplicationUri, _options.ProductUri)
            .AsClient()
            .AddSecurityConfiguration(certificates, pkiRoot)
            .SetAutoAcceptUntrustedCertificates(_options.AcceptUntrustedServerCertificate)
            .SetAddAppCertToTrustedStore(_options.AddAppCertToTrustedStore)
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        configuration.ClientConfiguration.DefaultSessionTimeout = (int)_options.SessionTimeoutMs;
        configuration.TransportQuotas.OperationTimeout = _options.OperationTimeoutMs;

        application.ApplicationConfiguration = configuration;

        await EnsureApplicationCertificateAsync(configuration, cancellationToken).ConfigureAwait(false);

        // Verifies the certificate is present and usable. Because the step above
        // has already created one when needed, this does not fall through to the
        // SDK's own generation path.
        var certificateOk = await application
            .CheckApplicationInstanceCertificatesAsync(silent: true, lifeTimeInMonths: null, ct: cancellationToken)
            .ConfigureAwait(false);

        if (!certificateOk)
        {
            throw new InvalidOperationException(
                $"No usable OPC UA application instance certificate. Delete '{pkiRoot}' and restart to regenerate one.");
        }

        var ownCertificate = configuration.SecurityConfiguration.ApplicationCertificate?.Certificate;
        if (ownCertificate is not null)
        {
            _logger.LogInformation(
                "Application instance certificate ready. Subject={Subject} Thumbprint={Thumbprint} NotAfter={NotAfter:u}",
                ownCertificate.Subject, ownCertificate.Thumbprint, ownCertificate.NotAfter);
        }

        configuration.CertificateValidator.CertificateValidation += OnServerCertificateValidation;

        return configuration;
    }

    /// <summary>
    /// Creates the application instance certificate on first run, with the
    /// subject alternative names stated explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK will happily generate this certificate itself, and if left to do
    /// so it fills the subject alternative names from
    /// <c>Dns.GetHostName()</c> — so the certificate the client presents to
    /// every server it ever connects to carries the name of the machine that
    /// first ran it. That is fine on a build agent and unhelpful on a laptop.
    /// </para>
    /// <para>
    /// Generating it here with an explicit domain-name list keeps the
    /// certificate's contents a deliberate decision. It also makes the
    /// certificate reproducible: two developers running this get certificates
    /// that differ only in their key material.
    /// </para>
    /// </remarks>
    private async Task EnsureApplicationCertificateAsync(
        ApplicationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var identifier = configuration.SecurityConfiguration.ApplicationCertificate;
        if (identifier is null) return;

        var existing = await identifier
            .FindAsync(needPrivateKey: true, configuration.ApplicationUri, _telemetry, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null) return;

        var domainNames = _options.CertificateDomainNames.Count > 0
            ? _options.CertificateDomainNames
            : ["localhost"];

        _logger.LogInformation(
            "Creating application instance certificate. Subject={Subject} DomainNames={DomainNames}",
            _options.CertificateSubjectName, string.Join(", ", domainNames));

        using var certificate = CertificateFactory
            .CreateCertificate(
                configuration.ApplicationUri,
                configuration.ApplicationName,
                _options.CertificateSubjectName,
                domainNames)
            .SetLifeTime(_options.CertificateLifetimeMonths)
            .CreateForRSA();

        var store = identifier.OpenStore(_telemetry);
        try
        {
            await store.AddAsync(certificate, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            store.Close();
        }

        if (_options.AddAppCertToTrustedStore)
        {
            var trusted = configuration.SecurityConfiguration.TrustedPeerCertificates?.OpenStore(_telemetry);
            if (trusted is not null)
            {
                try
                {
                    // Strip the private key: a trust list holds public
                    // certificates, and writing a key into it would be a
                    // needless second copy of the client's identity on disk.
                    using var publicOnly = CertificateFactory.Create(certificate.RawData);
                    await trusted.AddAsync(publicOnly, null, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    trusted.Close();
                }
            }
        }
    }

    /// <summary>
    /// Decides whether to accept the server's certificate.
    /// </summary>
    /// <remarks>
    /// Three failures are treated as acceptable when
    /// <see cref="OpcClientOptions.AcceptUntrustedServerCertificate"/> is on, and
    /// only then. <c>BadCertificateUntrusted</c> is the first-run case: the
    /// simulator's self-signed certificate is not in the client's trust list yet.
    /// <c>BadCertificateHostNameInvalid</c> is the container case: the
    /// certificate was issued for the server's own hostname and the client
    /// reached it under a different one. Both are expected against a development
    /// simulator and neither is acceptable in production, which is why the
    /// accepted certificate is logged as a warning with its thumbprint every
    /// single time rather than silently waved through.
    /// </remarks>
    private void OnServerCertificateValidation(CertificateValidator sender, CertificateValidationEventArgs e)
    {
        var statusCode = e.Error?.StatusCode.Code ?? StatusCodes.Good;

        var acceptable =
            statusCode == StatusCodes.BadCertificateUntrusted ||
            statusCode == StatusCodes.BadCertificateHostNameInvalid ||
            statusCode == StatusCodes.BadCertificateChainIncomplete;

        if (_options.AcceptUntrustedServerCertificate && acceptable)
        {
            e.Accept = true;
            _logger.LogWarning(
                "Accepting server certificate that failed validation ({Status}) because AcceptUntrustedServerCertificate is enabled. " +
                "This is a development-only setting. Subject={Subject} Thumbprint={Thumbprint}",
                StatusCodes.GetBrowseName(statusCode), e.Certificate?.Subject, e.Certificate?.Thumbprint);
            return;
        }

        _logger.LogError(
            "Rejecting server certificate. Status={Status} Subject={Subject} Thumbprint={Thumbprint}. " +
            "Copy the certificate into {TrustedStore} to trust it, or set Opc:AcceptUntrustedServerCertificate for local development.",
            StatusCodes.GetBrowseName(statusCode), e.Certificate?.Subject, e.Certificate?.Thumbprint,
            Path.Combine(ResolvePkiRoot(_options.PkiRoot), "trusted"));
    }

    /// <summary>
    /// Opens a session, applying the endpoint-URL policy described on
    /// <see cref="EndpointUrlRewriter"/>.
    /// </summary>
    public async Task<ISession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var requestedUrl = _options.EndpointUrl;

        var endpointConfiguration = EndpointConfiguration.Create(configuration);
        endpointConfiguration.OperationTimeout = _options.OperationTimeoutMs;

        var candidates = await SelectEndpointsAsync(configuration, endpointConfiguration, requestedUrl, cancellationToken)
            .ConfigureAwait(false);

        var sessionFactory = new DefaultSessionFactory(_telemetry);

        for (var index = 0; index < candidates.Count; index++)
        {
            var description = candidates[index];

            if (description.SecurityMode == MessageSecurityMode.None)
            {
                _logger.LogWarning(
                    "Connecting to {Url} over an UNSECURED endpoint (SecurityMode=None). Acceptable against a simulator, never in production.",
                    requestedUrl);
            }

            _logger.LogInformation(
                "Opening session to {EndpointUrl} with SecurityMode={SecurityMode} Policy={SecurityPolicy}",
                description.EndpointUrl, description.SecurityMode, description.SecurityPolicyUri);

            var endpoint = new ConfiguredEndpoint(null, description, endpointConfiguration);

            ISession session;

            try
            {
                session = await sessionFactory.CreateAsync(
                    configuration: configuration,
                    endpoint: endpoint,
                    // false: do not re-fetch endpoints from the server on connect. Doing
                    // so would discard the endpoint URL selected above and replace it
                    // with the server-advertised one, undoing the container fix.
                    updateBeforeConnect: false,
                    // false: the server's certificate is issued for its own hostname,
                    // which will not match the name the client used inside Compose. The
                    // certificate is still validated; only the domain check is relaxed.
                    checkDomain: false,
                    sessionName: $"{_options.ApplicationName}:{Environment.ProcessId}",
                    sessionTimeout: _options.SessionTimeoutMs,
                    identity: new UserIdentity(),
                    preferredLocales: null,
                    ct: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && index + 1 < candidates.Count)
            {
                // Whether a policy works is only known at channel open: a server
                // can advertise one its deployment then refuses, because the
                // client certificate is not registered with it or because one
                // side does not implement the algorithm. That arrives after
                // endpoint selection, so the only way to act on it is to try the
                // next endpoint the server offered. The list is ordered
                // strongest-first and contains only endpoints the configuration
                // already permits.
                _logger.LogWarning(
                    ex,
                    "Endpoint {EndpointUrl} (SecurityMode={SecurityMode} Policy={SecurityPolicy}) did not complete a handshake: {Message} "
                        + "Trying the next endpoint this server offers.",
                    description.EndpointUrl, description.SecurityMode, description.SecurityPolicyUri, ex.Message);
                continue;
            }

            session.KeepAliveInterval = _options.KeepAliveIntervalMs;

            _logger.LogInformation(
                "Session established. SessionId={SessionId} Server={ServerUri}",
                session.SessionId, session.ConfiguredEndpoint?.Description?.Server?.ApplicationUri);

            return session;
        }

        throw new InvalidOperationException($"Server at {requestedUrl} offers no usable endpoint.");
    }

    /// <summary>
    /// Discovers the server's endpoints and returns those the configuration
    /// permits, in the order they should be tried: strongest security first.
    /// </summary>
    public async Task<IReadOnlyList<EndpointDescription>> SelectEndpointsAsync(
        ApplicationConfiguration configuration,
        EndpointConfiguration endpointConfiguration,
        string requestedUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EndpointDescriptionCollection endpoints;

        var discovery = await DiscoveryClient
            .CreateAsync(new Uri(requestedUrl), endpointConfiguration, _telemetry, DiagnosticsMasks.None, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            endpoints = await discovery.GetEndpointsAsync(null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            discovery.Dispose();
        }

        if (endpoints is null || endpoints.Count == 0)
        {
            throw new InvalidOperationException($"Server at {requestedUrl} returned no endpoints.");
        }

        _logger.LogDebug("Discovery returned {Count} endpoint(s) from {Url}", endpoints.Count, requestedUrl);

        var ranked = RankEndpoints(endpoints, _options.UseSecurity, _options.AllowNoSecurityFallback);

        if (ranked.Count == 0)
        {
            throw new InvalidOperationException(
                _options.UseSecurity && !_options.AllowNoSecurityFallback
                    ? $"Server at {requestedUrl} offers no secure endpoint and Opc:AllowNoSecurityFallback is disabled."
                    : $"Server at {requestedUrl} offers no usable endpoint.");
        }

        foreach (var candidate in ranked)
        {
            ApplyEndpointUrlPolicy(candidate, requestedUrl);
        }

        return ranked;
    }

    /// <summary>
    /// Replaces the advertised host and port with the requested ones when
    /// <see cref="OpcClientOptions.PreserveRequestedEndpointUrl"/> is set. The
    /// rewrite is logged loudly because when it does fire, it is almost always
    /// the difference between a working and a hanging client, and a future
    /// reader debugging a connection needs to see that it happened.
    /// </summary>
    private void ApplyEndpointUrlPolicy(EndpointDescription selected, string requestedUrl)
    {
        var advertised = selected.EndpointUrl;

        if (!EndpointUrlRewriter.AuthorityDiffers(advertised, requestedUrl))
        {
            return;
        }

        if (!_options.PreserveRequestedEndpointUrl)
        {
            _logger.LogWarning(
                "Server advertises endpoint {Advertised} but was reached at {Requested}. " +
                "PreserveRequestedEndpointUrl is disabled, so the advertised host will be used and must be resolvable from this process.",
                advertised, requestedUrl);
            return;
        }

        var rewritten = EndpointUrlRewriter.PreserveRequestedHost(advertised, requestedUrl);
        selected.EndpointUrl = rewritten;

        _logger.LogInformation(
            "Server advertised endpoint {Advertised}; using {Rewritten} instead so the host stays reachable from this process.",
            advertised, rewritten);
    }

    /// <summary>
    /// Endpoint preference, strongest first: secure endpoints ordered by the
    /// server's own SecurityLevel (Part 4, §7.10), which is a better signal than
    /// hardcoding a policy URI preference list that goes stale as policies are
    /// deprecated, followed by the unsecured endpoint where that is permitted.
    /// </summary>
    /// <remarks>
    /// An ordered list rather than a single choice, because whether a policy
    /// works is only known at channel open. Every entry is one the configuration
    /// permits, so falling through the list never connects less securely than
    /// <see cref="OpcClientOptions.UseSecurity"/> and
    /// <see cref="OpcClientOptions.AllowNoSecurityFallback"/> already allow.
    /// </remarks>
    internal static IReadOnlyList<EndpointDescription> RankEndpoints(
        IReadOnlyCollection<EndpointDescription> endpoints,
        bool useSecurity,
        bool allowNoSecurityFallback)
    {
        var candidates = endpoints
            .Where(e => !string.IsNullOrWhiteSpace(e.EndpointUrl))
            .Where(e => e.EndpointUrl.StartsWith(Utils.UriSchemeOpcTcp, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = endpoints.Where(e => !string.IsNullOrWhiteSpace(e.EndpointUrl)).ToList();
        }

        var secure = candidates
            .Where(e => e.SecurityMode != MessageSecurityMode.None)
            .OrderByDescending(e => e.SecurityLevel)
            .ToList();

        var insecure = candidates
            .Where(e => e.SecurityMode == MessageSecurityMode.None)
            .OrderByDescending(e => e.SecurityLevel)
            .ToList();

        if (useSecurity)
        {
            return allowNoSecurityFallback ? [.. secure, .. insecure] : secure;
        }

        return [.. insecure, .. secure];
    }

    private static string ResolvePkiRoot(string pkiRoot) =>
        Path.IsPathRooted(pkiRoot) ? pkiRoot : Path.Combine(AppContext.BaseDirectory, pkiRoot);
}
