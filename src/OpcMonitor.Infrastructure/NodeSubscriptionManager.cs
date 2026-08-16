using Microsoft.Extensions.Logging;
using Opc.Ua;
using OpcMonitor.Domain;

namespace OpcMonitor.Infrastructure;

/// <summary>Why a subscribe or unsubscribe request did not do what was asked.</summary>
public enum NodeChangeOutcome
{
    /// <summary>The node set changed.</summary>
    Changed,

    /// <summary>Already subscribed, or already not subscribed. Nothing to do.</summary>
    NoChange,

    /// <summary>There is no live session right now, so the request cannot be honoured.</summary>
    NotConnected,

    /// <summary>The address does not resolve to a node on this server.</summary>
    NotFound,

    /// <summary>The server refused the monitored item.</summary>
    Rejected
}

public sealed record NodeChangeResult(NodeChangeOutcome Outcome, MonitoredNode? Node, string? Detail)
{
    public bool Succeeded => Outcome is NodeChangeOutcome.Changed;
}

/// <summary>
/// The one place that changes what is being monitored, keeping the registry, the
/// live OPC subscription, the snapshot store and connected dashboards in step.
/// </summary>
/// <remarks>
/// <para>
/// These four have to move together and each of them is owned by something else,
/// so without a single coordinator the update ends up spread across a request
/// handler — which is how you get a node that appears on the dashboard but is not
/// subscribed, or is subscribed but has no card, or both until the next restart.
/// </para>
/// <para>
/// Requests are serialised behind one gate. Subscribe and unsubscribe are rare,
/// operator-driven and involve a server round trip; letting two of them interleave
/// on the same subscription buys nothing and makes the partial-failure cases
/// considerably harder to reason about.
/// </para>
/// </remarks>
public sealed class NodeSubscriptionManager
{
    private readonly ResilientOpcClient _client;
    private readonly NodeResolver _resolver;
    private readonly OpcSubscriptionService _subscriptions;
    private readonly MonitoredNodeRegistry _registry;
    private readonly NodeSnapshotStore _store;
    private readonly OpcEventChannel _events;
    private readonly ILogger<NodeSubscriptionManager> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public NodeSubscriptionManager(
        ResilientOpcClient client,
        NodeResolver resolver,
        OpcSubscriptionService subscriptions,
        MonitoredNodeRegistry registry,
        NodeSnapshotStore store,
        OpcEventChannel events,
        ILogger<NodeSubscriptionManager> logger)
    {
        _client = client;
        _resolver = resolver;
        _subscriptions = subscriptions;
        _registry = registry;
        _store = store;
        _events = events;
        _logger = logger;
    }

    public async Task<NodeChangeResult> SubscribeAsync(
        MonitoredNodeOptions option,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(option);

        if (string.IsNullOrWhiteSpace(option.Address))
        {
            return new NodeChangeResult(NodeChangeOutcome.NotFound, null, "Address is required.");
        }

        var session = _client.CurrentSession;
        if (session is null)
        {
            return new NodeChangeResult(
                NodeChangeOutcome.NotConnected, null,
                "No live OPC UA session. The node was not added; try again once the connection recovers.");
        }

        var id = MonitoredNodeRegistry.IdOf(option);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_registry.Contains(id))
            {
                return new NodeChangeResult(NodeChangeOutcome.NoChange, _store.Get(id)?.Node, "Already subscribed.");
            }

            // Resolved before anything is mutated, so an address that does not
            // exist on this server leaves no trace behind.
            var resolved = (await _resolver
                    .ResolveAsync(session, [option], cancellationToken)
                    .ConfigureAwait(false))
                .FirstOrDefault();

            if (resolved is null)
            {
                return new NodeChangeResult(
                    NodeChangeOutcome.NotFound, null,
                    $"'{option.Address}' does not resolve to a node on this server.");
            }

            // Store first: a reading can arrive between AddNodeAsync attaching the
            // item and this line, and the store drops readings for nodes it does
            // not know about.
            _store.Add(resolved.Node);

            bool attached;
            try
            {
                attached = await _subscriptions.AddNodeAsync(resolved, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _store.Remove(id);
                _logger.LogWarning(ex, "Server rejected a monitored item for {Address}.", option.Address);
                return new NodeChangeResult(NodeChangeOutcome.Rejected, null, Describe(ex));
            }

            if (!attached)
            {
                _store.Remove(id);
                return new NodeChangeResult(
                    NodeChangeOutcome.NotConnected, null,
                    "The subscription went away while the node was being added.");
            }

            _registry.Add(option);
            PublishNodeSet();

            _logger.LogInformation("Subscribed to {Address} ({DisplayName}) from the dashboard.",
                option.Address, resolved.Node.DisplayName);

            return new NodeChangeResult(NodeChangeOutcome.Changed, resolved.Node, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NodeChangeResult> UnsubscribeAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registry.Contains(id) && _store.Get(id) is null)
            {
                return new NodeChangeResult(NodeChangeOutcome.NoChange, null, "Not subscribed.");
            }

            // The registry is updated first and unconditionally. Even if the
            // server round trip below fails, the operator's intent is recorded,
            // and the next reconnect will not bring the node back.
            _registry.Remove(id);

            try
            {
                await _subscriptions.RemoveNodeAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A monitored item left behind on a subscription that is about to
                // be torn down anyway is not worth failing the request over. The
                // store no longer knows the node, so its readings are discarded.
                _logger.LogDebug(ex, "Ignoring error while detaching monitored item {NodeId}.", id);
            }

            _store.Remove(id);
            PublishNodeSet();

            _logger.LogInformation("Unsubscribed from {NodeId} from the dashboard.", id);
            return new NodeChangeResult(NodeChangeOutcome.Changed, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Broadcasts the node set through the same channel the reconnect path uses,
    /// so every dashboard converges regardless of which one made the change.
    /// </summary>
    private void PublishNodeSet() =>
        _events.Publish(new NodeSetChanged(_store.Snapshot().Select(s => s.Node).ToList()));

    private static string Describe(Exception ex) =>
        ex is ServiceResultException sre
            ? $"{StatusCodes.GetBrowseName(sre.StatusCode)}: {sre.Message}"
            : ex.Message;
}
