using System.ComponentModel.DataAnnotations;

namespace OpcMonitor.Infrastructure;

/// <summary>
/// Everything about which server to talk to and how. Bound from the "Opc"
/// section of configuration; nothing here is hardcoded anywhere else.
/// </summary>
public sealed class OpcClientOptions
{
    public const string SectionName = "Opc";

    /// <summary>
    /// Endpoint to connect to, e.g. <c>opc.tcp://simulator:62541</c>. Discovery
    /// is performed against this URL.
    /// </summary>
    [Required]
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:62541";

    /// <summary>
    /// Application URI this client presents to the server, and the URI embedded
    /// in the generated application instance certificate.
    /// </summary>
    /// <remarks>
    /// Set explicitly and never left to the SDK default, which derives the URI
    /// from the local machine's hostname and would publish it to every server
    /// the client touches and into the certificate's subject alternative names.
    /// </remarks>
    [Required]
    public string ApplicationUri { get; set; } = "urn:localhost:OpcMonitor:Client";

    /// <summary>Application name presented during session activation.</summary>
    [Required]
    public string ApplicationName { get; set; } = "OpcMonitor";

    /// <summary>Product URI reported in the application description.</summary>
    public string ProductUri { get; set; } = "https://github.com/opc-ua-monitor";

    /// <summary>
    /// Subject name of the application instance certificate. The SDK creates a
    /// self-signed certificate with this subject on first run if none exists.
    /// </summary>
    public string CertificateSubjectName { get; set; } = "CN=OpcMonitor, O=OpcMonitor, C=US";

    /// <summary>
    /// Subject alternative names placed in the generated certificate.
    /// </summary>
    /// <remarks>
    /// Set explicitly for the same reason as <see cref="ApplicationUri"/>. Left
    /// to the SDK, this list is filled from the local machine's hostname, and
    /// the certificate then carries that name to every server it connects to.
    /// Override it when the client needs to be reachable under a specific name,
    /// which for a pure client is almost never.
    /// </remarks>
    public List<string> CertificateDomainNames { get; set; } = ["localhost"];

    /// <summary>Validity period of the generated certificate.</summary>
    [Range(1, 240)]
    public ushort CertificateLifetimeMonths { get; set; } = 24;

    /// <summary>
    /// Root of the certificate stores. Relative paths resolve against the
    /// application base directory. This directory is gitignored.
    /// </summary>
    public string PkiRoot { get; set; } = "pki";

    /// <summary>
    /// Prefer a signed-and-encrypted endpoint. Falls back to an unsecured
    /// endpoint only when <see cref="AllowNoSecurityFallback"/> is set.
    /// </summary>
    public bool UseSecurity { get; set; } = true;

    /// <summary>
    /// Connect over an unsecured endpoint when the server offers no secure one.
    /// Convenient against simulators; a warning is logged every time it is used.
    /// </summary>
    public bool AllowNoSecurityFallback { get; set; } = true;

    /// <summary>
    /// Trust the server's certificate without it being in the trusted store.
    /// <b>Development only.</b> Off here and turned on by the appsettings.json
    /// files in this repository, whose endpoints are a simulator and a public
    /// demo server, so a fresh clone connects without a manual certificate
    /// exchange; the accepted certificate's subject and thumbprint are logged
    /// every time so the trust decision is never silent.
    /// </summary>
    public bool AcceptUntrustedServerCertificate { get; set; } = false;

    /// <summary>
    /// Add this client's own certificate to its trusted store on creation, so a
    /// restart does not produce a fresh untrusted identity.
    /// </summary>
    public bool AddAppCertToTrustedStore { get; set; } = true;

    /// <summary>
    /// Keep the host and port that were configured, discarding the host and port
    /// the server advertises in its EndpointDescription.
    /// </summary>
    /// <remarks>
    /// An OPC UA server returns endpoint URLs built from its own idea of its
    /// hostname (Part 4, §5.4.2 GetEndpoints). Inside Docker Compose that is the
    /// container hostname, which a client on another network — or a client that
    /// reached the container through a published port — cannot resolve. Keeping
    /// the requested host is the standard client-side answer, and it is
    /// deliberately a flag rather than unconditional behaviour so the default
    /// stays spec-faithful for a real deployment behind a gateway.
    /// </remarks>
    public bool PreserveRequestedEndpointUrl { get; set; } = true;

    /// <summary>Session timeout advertised to the server.</summary>
    [Range(10_000, 3_600_000)]
    public uint SessionTimeoutMs { get; set; } = 60_000;

    /// <summary>How long discovery and other operations may take.</summary>
    [Range(1_000, 120_000)]
    public int OperationTimeoutMs { get; set; } = 15_000;

    /// <summary>Session keep-alive probe interval.</summary>
    [Range(1_000, 60_000)]
    public int KeepAliveIntervalMs { get; set; } = 5_000;

    public OpcSubscriptionOptions Subscription { get; set; } = new();

    public ReconnectOptions Reconnect { get; set; } = new();

    /// <summary>Nodes to monitor. Empty is valid but pointless.</summary>
    public List<MonitoredNodeOptions> Nodes { get; set; } = new();

    /// <summary>Readings retained per node for the snapshot-on-connect window.</summary>
    [Range(2, 10_000)]
    public int HistoryWindowSize { get; set; } = 120;
}

public sealed class OpcSubscriptionOptions
{
    /// <summary>How often the server sends notification messages.</summary>
    [Range(50, 600_000)]
    public int PublishingIntervalMs { get; set; } = 1_000;

    /// <summary>How often the server samples each monitored item.</summary>
    [Range(0, 600_000)]
    public int SamplingIntervalMs { get; set; } = 500;

    /// <summary>Server-side queue depth per monitored item.</summary>
    [Range(0, 1000)]
    public uint QueueSize { get; set; } = 10;

    /// <summary>Drop the oldest queued value when the queue overflows.</summary>
    public bool DiscardOldest { get; set; } = true;

    /// <summary>
    /// Absolute deadband: suppress changes smaller than this. Zero disables the
    /// filter and reports every change. Applies to numeric nodes only.
    /// </summary>
    [Range(0d, double.MaxValue)]
    public double DeadbandAbsolute { get; set; } = 0d;

    /// <summary>Publish cycles with no data before a keep-alive is sent.</summary>
    [Range(1, 1000)]
    public uint KeepAliveCount { get; set; } = 10;

    /// <summary>Publish cycles with no publish request before the server drops the subscription.</summary>
    [Range(3, 10_000)]
    public uint LifetimeCount { get; set; } = 30;
}

public sealed class ReconnectOptions
{
    /// <summary>First backoff delay after a connection is lost.</summary>
    [Range(100, 60_000)]
    public int InitialDelayMs { get; set; } = 1_000;

    /// <summary>Ceiling for the exponential backoff.</summary>
    [Range(1_000, 600_000)]
    public int MaxDelayMs { get; set; } = 30_000;

    /// <summary>Multiplier applied to the delay after each failed attempt.</summary>
    [Range(1.0, 10.0)]
    public double BackoffFactor { get; set; } = 2.0;

    /// <summary>
    /// Random fraction of the delay added or subtracted, to stop a fleet of
    /// clients reconnecting to a recovering server in lockstep.
    /// </summary>
    [Range(0d, 1d)]
    public double JitterFraction { get; set; } = 0.2;
}

/// <summary>
/// One configured node. <see cref="Address"/> accepts either a protocol node id
/// or a slash-separated browse path.
/// </summary>
public sealed class MonitoredNodeOptions
{
    /// <summary>
    /// Either a node id in the standard textual form (<c>i=2258</c>,
    /// <c>ns=2;s=Some.Tag</c>) or a browse path relative to the Objects folder
    /// (<c>Server/ServerStatus/CurrentTime</c>).
    /// </summary>
    [Required]
    public string Address { get; set; } = string.Empty;

    /// <summary>Stable id used by the API and the dashboard. Defaults to <see cref="Address"/>.</summary>
    public string? Id { get; set; }

    /// <summary>Label for the dashboard. Defaults to the last browse-path segment.</summary>
    public string? DisplayName { get; set; }

    public string? Unit { get; set; }

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    /// <summary>
    /// When true, failure to resolve this node aborts startup. Default false:
    /// a config that lists nodes from several server profiles should still start
    /// against a server that only has some of them.
    /// </summary>
    public bool Required { get; set; }
}
