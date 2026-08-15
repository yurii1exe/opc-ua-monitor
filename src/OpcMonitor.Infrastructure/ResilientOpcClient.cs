using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OpcMonitor.Domain;

namespace OpcMonitor.Infrastructure;

/// <summary>
/// Keeps a session and its subscription alive for the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// On connection loss the client rebuilds the session and the subscription from
/// scratch rather than attempting to transfer the existing subscription to a new
/// session. Subscription transfer saves a round trip and preserves queued
/// notifications, but it succeeds only if the server still holds the
/// subscription and supports transfer, so a client that relies on it needs the
/// full rebuild path anyway as a fallback — and that fallback is then the least
/// exercised code in the system, which is exactly the wrong place to keep the
/// rarely-tested branch.
/// </para>
/// <para>
/// Rebuilding unconditionally means one path, always exercised, with no partial
/// state to reason about. The cost is the notifications queued during the
/// outage; the initial read on resubscribe recovers the current value of every
/// node immediately, and for a live monitor the current value is the whole
/// point.
/// </para>
/// </remarks>
public sealed class ResilientOpcClient
{
    private readonly OpcSessionFactory _sessionFactory;
    private readonly NodeResolver _nodeResolver;
    private readonly OpcSubscriptionService _subscriptions;
    private readonly OpcEventChannel _events;
    private readonly OpcClientOptions _options;
    private readonly ILogger<ResilientOpcClient> _logger;

    private volatile ConnectionStatus _status;

    public ResilientOpcClient(
        OpcSessionFactory sessionFactory,
        NodeResolver nodeResolver,
        OpcSubscriptionService subscriptions,
        OpcEventChannel events,
        IOptions<OpcClientOptions> options,
        ILogger<ResilientOpcClient> logger)
    {
        _sessionFactory = sessionFactory;
        _nodeResolver = nodeResolver;
        _subscriptions = subscriptions;
        _events = events;
        _options = options.Value;
        _logger = logger;
        _status = ConnectionStatus.Initial(_options.EndpointUrl);
    }

    public ConnectionStatus Status => _status;

    /// <summary>
    /// Runs until cancelled. Never throws for a connection problem — a server
    /// that is down is an expected condition for this class, not an error.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        string? lastError = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            ISession? session = null;

            try
            {
                Publish(
                    attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting,
                    attempt,
                    attempt == 0 ? null : $"attempt {attempt}");

                session = await _sessionFactory.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

                var nodes = await _nodeResolver
                    .ResolveAsync(session, _options.Nodes, cancellationToken)
                    .ConfigureAwait(false);

                _events.Publish(new NodeSetChanged(nodes.Select(n => n.Node).ToList()));

                await _subscriptions.StartAsync(session, nodes, cancellationToken).ConfigureAwait(false);

                attempt = 0;
                lastError = null;
                Publish(ConnectionState.Connected, 0);
                _logger.LogInformation("Monitoring {Count} node(s) on {Endpoint}.", nodes.Count, _options.EndpointUrl);

                await WaitForSessionLossAsync(session, cancellationToken).ConfigureAwait(false);

                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Session to {Endpoint} was lost.", _options.EndpointUrl);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // No state is published here. The backoff block below publishes
                // once, with the delay included, so clients see one transition
                // per cycle rather than two that immediately overwrite each
                // other.
                lastError = Describe(ex);
                _logger.LogError(ex, "OPC UA connection attempt failed: {Message}", ex.Message);
            }
            finally
            {
                await SafeTeardownAsync(session).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested) break;

            attempt++;
            var delay = ReconnectPolicy.DelayFor(_options.Reconnect, attempt, Random.Shared);

            var detail = lastError is null
                ? $"retrying in {delay.TotalSeconds:F1}s"
                : $"{lastError} — retrying in {delay.TotalSeconds:F1}s";

            Publish(ConnectionState.Reconnecting, attempt, detail);

            _logger.LogInformation("Reconnect attempt {Attempt} in {Delay:F1}s.", attempt, delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Publish(ConnectionState.Disconnected, 0, "shutting down");
        _events.Complete();
    }

    /// <summary>
    /// Completes when the session stops being usable.
    /// </summary>
    /// <remarks>
    /// Driven by the SDK's keep-alive, which is the only mechanism that detects
    /// a silently dead TCP connection on an idle subscription. A slow polling
    /// fallback runs alongside it because a keep-alive callback that itself
    /// faults would otherwise leave this method waiting forever, and a monitor
    /// that hangs looks exactly like a monitor whose values simply stopped
    /// changing.
    /// </remarks>
    private static async Task WaitForSessionLossAsync(ISession session, CancellationToken cancellationToken)
    {
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnKeepAlive(ISession sender, KeepAliveEventArgs e)
        {
            if (ServiceResult.IsBad(e.Status) || !sender.Connected)
            {
                lost.TrySetResult();
            }
        }

        session.KeepAlive += OnKeepAlive;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var cancelled = Task.Delay(Timeout.Infinite, linked.Token);

            while (true)
            {
                var completed = await Task.WhenAny(
                    lost.Task,
                    cancelled,
                    Task.Delay(TimeSpan.FromSeconds(5), linked.Token)).ConfigureAwait(false);

                if (completed == lost.Task || cancellationToken.IsCancellationRequested) break;
                if (!session.Connected) break;
            }

            linked.Cancel();
        }
        finally
        {
            session.KeepAlive -= OnKeepAlive;
        }
    }

    private async Task SafeTeardownAsync(ISession? session)
    {
        try
        {
            await _subscriptions.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring subscription teardown error.");
        }

        if (session is null) return;

        try
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring session close error.");
        }
        finally
        {
            session.Dispose();
        }
    }

    private void Publish(ConnectionState state, int attempt, string? detail = null)
    {
        var status = new ConnectionStatus(state, _options.EndpointUrl, DateTimeOffset.UtcNow, attempt, detail);
        _status = status;
        _events.Publish(new ConnectionStateChanged(status));
    }

    private static string Describe(Exception ex) =>
        ex is ServiceResultException sre ? $"{StatusCodes.GetBrowseName(sre.StatusCode)}: {sre.Message}" : ex.Message;
}
