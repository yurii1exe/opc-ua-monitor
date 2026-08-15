using Microsoft.AspNetCore.SignalR;
using OpcMonitor.Domain;

namespace OpcMonitor.Api;

/// <summary>
/// Strongly-typed contract for what the server pushes to connected dashboards.
/// </summary>
/// <remarks>
/// A typed hub client rather than <c>SendAsync("SomeString", ...)</c>: renaming
/// a message then becomes a compile error on the server instead of a dashboard
/// that silently stops updating.
/// </remarks>
public interface IMonitoringClient
{
    /// <summary>A batch of value changes. Batched rather than sent per reading.</summary>
    Task ReadingsUpdated(IReadOnlyList<NodeReadingDto> readings);

    /// <summary>The OPC session's connection state changed.</summary>
    Task ConnectionStateChanged(ConnectionStatusDto status);

    /// <summary>The set of monitored nodes changed, e.g. after a reconnect resolved more of them.</summary>
    Task NodesChanged(IReadOnlyList<MonitoredNodeDto> nodes);

    /// <summary>Full current state, sent on connect and on request.</summary>
    Task SnapshotReceived(SnapshotDto snapshot);
}

/// <summary>
/// The dashboard's socket. Deliberately thin: it owns no state and starts no
/// work, it only relays what the hosted service has already put in the store.
/// </summary>
public sealed class MonitoringHub : Hub<IMonitoringClient>
{
    private readonly NodeSnapshotStore _store;
    private readonly ILogger<MonitoringHub> _logger;

    public MonitoringHub(NodeSnapshotStore store, ILogger<MonitoringHub> logger)
    {
        _store = store;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Dashboard connected: {ConnectionId}", Context.ConnectionId);

        // Push before returning, so the client is never briefly connected and
        // blank while it decides to ask.
        await Clients.Caller.SnapshotReceived(BuildSnapshot()).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug(exception, "Dashboard disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Lets a client re-sync explicitly — used by the dashboard after its own
    /// socket reconnects, when it has no idea what it missed.
    /// </summary>
    public SnapshotDto GetSnapshot() => BuildSnapshot();

    private SnapshotDto BuildSnapshot() => new(
        ConnectionStatusDto.From(_store.Connection),
        _store.Snapshot().Select(NodeStatusDto.From).ToList(),
        DateTimeOffset.UtcNow);
}
