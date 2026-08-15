using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using OpcMonitor.Domain;
using OpcMonitor.Infrastructure;

namespace OpcMonitor.Api;

/// <summary>
/// Owns the OPC session for the lifetime of the process and pumps everything it
/// produces into the snapshot store and the SignalR hub.
/// </summary>
/// <remarks>
/// Two concerns, one service, on purpose: the client loop and the pump share a
/// lifetime and a cancellation token, and splitting them into two hosted
/// services would only create an ordering problem at shutdown.
/// </remarks>
public sealed class OpcMonitorWorker : BackgroundService
{
    /// <summary>Longest a reading waits before being flushed to clients.</summary>
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>Most readings sent in one hub message.</summary>
    private const int MaxBatchSize = 200;

    private readonly ResilientOpcClient _client;
    private readonly OpcEventChannel _events;
    private readonly NodeSnapshotStore _store;
    private readonly IHubContext<MonitoringHub, IMonitoringClient> _hub;
    private readonly ILogger<OpcMonitorWorker> _logger;

    public OpcMonitorWorker(
        ResilientOpcClient client,
        OpcEventChannel events,
        NodeSnapshotStore store,
        IHubContext<MonitoringHub, IMonitoringClient> hub,
        ILogger<OpcMonitorWorker> logger)
    {
        _client = client;
        _events = events;
        _store = store;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = _client.RunAsync(stoppingToken);
        var pump = PumpAsync(stoppingToken);

        await Task.WhenAll(connection, pump).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains the event channel, updates the store and forwards to clients.
    /// </summary>
    /// <remarks>
    /// Readings are coalesced into batches. At one node this is pointless; at a
    /// few hundred nodes changing every 100ms it is the difference between one
    /// hub message per tick and several thousand, and SignalR's per-message
    /// overhead is what would otherwise dominate. The store is updated
    /// immediately regardless of batching, so a REST caller or a newly connected
    /// dashboard always sees the freshest value even mid-window.
    /// </remarks>
    private async Task PumpAsync(CancellationToken stoppingToken)
    {
        var batch = new List<NodeReadingDto>(MaxBatchSize);
        var clock = Stopwatch.StartNew();
        var nextFlush = clock.Elapsed + BatchWindow;

        try
        {
            while (await WaitForEventAsync(stoppingToken).ConfigureAwait(false))
            {
                while (_events.Reader.TryRead(out var opcEvent))
                {
                    switch (opcEvent)
                    {
                        case ReadingReceived(var reading):
                            _store.Record(reading);
                            batch.Add(NodeReadingDto.From(reading));
                            break;

                        case ConnectionStateChanged(var status):
                            _store.SetConnection(status);
                            await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                            await _hub.Clients.All
                                .ConnectionStateChanged(ConnectionStatusDto.From(status))
                                .ConfigureAwait(false);
                            LogConnection(status);
                            break;

                        case NodeSetChanged(var nodes):
                            await _hub.Clients.All
                                .NodesChanged(nodes.Select(MonitoredNodeDto.From).ToList())
                                .ConfigureAwait(false);
                            break;
                    }

                    if (batch.Count >= MaxBatchSize) break;
                }

                if (batch.Count >= MaxBatchSize || clock.Elapsed >= nextFlush)
                {
                    await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                    nextFlush = clock.Elapsed + BatchWindow;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);

        if (_events.DroppedCount > 0)
        {
            _logger.LogWarning(
                "{Dropped} event(s) were dropped because the fan-out could not keep up.",
                _events.DroppedCount);
        }
    }

    /// <summary>
    /// Waits for either an event or the end of the batch window, so a partially
    /// filled batch is still flushed promptly when the data rate drops.
    /// </summary>
    private async Task<bool> WaitForEventAsync(CancellationToken stoppingToken)
    {
        var wait = _events.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var timeout = Task.Delay(BatchWindow, stoppingToken);

        var completed = await Task.WhenAny(wait, timeout).ConfigureAwait(false);

        // Channel completed and drained: nothing more will ever arrive.
        return completed != wait || await wait.ConfigureAwait(false);
    }

    private async Task FlushAsync(List<NodeReadingDto> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        var payload = batch.ToArray();
        batch.Clear();

        try
        {
            await _hub.Clients.All.ReadingsUpdated(payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A hub failure must not take down the OPC session; the store is
            // already updated and the next snapshot will carry the values.
            _logger.LogWarning(ex, "Failed to broadcast {Count} reading(s).", payload.Length);
        }
    }

    private void LogConnection(ConnectionStatus status)
    {
        if (status.State is ConnectionState.Connected)
        {
            _logger.LogInformation("OPC connection state: {State} ({Endpoint})", status.State, status.EndpointUrl);
        }
        else
        {
            _logger.LogWarning(
                "OPC connection state: {State} attempt={Attempt} {Detail}",
                status.State, status.Attempt, status.Detail);
        }
    }
}
