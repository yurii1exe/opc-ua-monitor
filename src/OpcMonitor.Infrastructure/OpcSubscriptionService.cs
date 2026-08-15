using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OpcMonitor.Domain;

namespace OpcMonitor.Infrastructure;

/// <summary>
/// Owns one OPC UA subscription and the monitored items on it, and turns
/// data-change notifications into <see cref="ReadingReceived"/> events.
/// </summary>
/// <remarks>
/// Subscriptions rather than polling. The server samples each item at the
/// configured rate and pushes only changes (Part 4, §5.12), so the client sees
/// every transition without generating read traffic proportional to the number
/// of tags — which is the difference between monitoring ten nodes and monitoring
/// ten thousand.
/// </remarks>
public sealed class OpcSubscriptionService : IAsyncDisposable
{
    private readonly OpcClientOptions _options;
    private readonly OpcEventChannel _events;
    private readonly ITelemetryContext _telemetry;
    private readonly ILogger<OpcSubscriptionService> _logger;

    private Subscription? _subscription;
    private ISession? _session;

    public OpcSubscriptionService(
        IOptions<OpcClientOptions> options,
        OpcEventChannel events,
        ITelemetryContext telemetry,
        ILogger<OpcSubscriptionService> logger)
    {
        _options = options.Value;
        _events = events;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Creates the subscription, adds a monitored item per node, then reads all
    /// current values once.
    /// </summary>
    /// <remarks>
    /// The initial read matters more than it looks. A data-change subscription
    /// only reports changes, so a node that is stable when the client attaches
    /// produces nothing until it happens to move — which on a healthy production
    /// line can be hours. Priming from an explicit read means the dashboard is
    /// fully populated within a second of connecting, on first start and on
    /// every reconnect.
    /// </remarks>
    public async Task StartAsync(ISession session, IReadOnlyList<ResolvedNode> nodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(nodes);

        await StopAsync().ConfigureAwait(false);
        _session = session;

        if (nodes.Count == 0)
        {
            _logger.LogWarning("No nodes resolved; the subscription would be empty, so none was created.");
            return;
        }

        var subscriptionOptions = _options.Subscription;
        var lifetimeCount = ResolveLifetimeCount(subscriptionOptions, _options.SessionTimeoutMs);

        var subscription = new Subscription(_telemetry, new Opc.Ua.Client.SubscriptionOptions
        {
            DisplayName = $"{_options.ApplicationName}:nodes",
            PublishingEnabled = true,
            PublishingInterval = subscriptionOptions.PublishingIntervalMs,
            KeepAliveCount = subscriptionOptions.KeepAliveCount,
            LifetimeCount = lifetimeCount,
            // 0 means "no client-imposed limit"; the server still bounds a
            // publish response by its own capabilities.
            MaxNotificationsPerPublish = 0,
            Priority = 100,
            // Both timestamps, so a reading can distinguish when the device
            // produced a value from when the server saw it.
            TimestampsToReturn = TimestampsToReturn.Both
        });

        foreach (var node in nodes)
        {
            subscription.AddItem(CreateMonitoredItem(node, subscriptionOptions));
        }

        session.AddSubscription(subscription);

        await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
        await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

        _subscription = subscription;

        var failed = subscription.MonitoredItems.Where(i => i.Status?.Error is { StatusCode.Code: not 0 }).ToList();
        foreach (var item in failed)
        {
            _logger.LogWarning("Monitored item {Item} was rejected by the server: {Error}",
                item.DisplayName, item.Status!.Error);
        }

        if (lifetimeCount != subscriptionOptions.LifetimeCount)
        {
            _logger.LogInformation(
                "Raised subscription LifetimeCount from {Configured} to {Effective} to satisfy the keep-alive and session-timeout minimums.",
                subscriptionOptions.LifetimeCount, lifetimeCount);
        }

        _logger.LogInformation(
            "Subscription created. Id={SubscriptionId} Items={ItemCount} PublishingInterval={PublishingInterval}ms (server granted {Granted}ms)",
            subscription.Id, subscription.MonitoredItemCount,
            subscriptionOptions.PublishingIntervalMs, subscription.CurrentPublishingInterval);

        await PrimeCurrentValuesAsync(session, nodes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Derives a lifetime count that satisfies both constraints the spec and the
    /// SDK impose, raising the configured value when it is too low.
    /// </summary>
    /// <remarks>
    /// OPC UA Part 4 §5.13.2 requires the subscription lifetime to be at least
    /// three keep-alive intervals — otherwise the server may delete a
    /// subscription that is merely quiet. Separately, a lifetime shorter than
    /// the session timeout means a network hiccup destroys the subscription
    /// while the session it belongs to is still alive, which produces the
    /// confusing state of a healthy session delivering nothing. Both are cheap
    /// to get right and expensive to diagnose, so they are computed rather than
    /// left to the operator to notice.
    /// </remarks>
    internal static uint ResolveLifetimeCount(OpcSubscriptionOptions options, uint sessionTimeoutMs)
    {
        var interval = Math.Max(options.PublishingIntervalMs, 1);

        var minimumForKeepAlive = options.KeepAliveCount * 3;
        var minimumForSession = (uint)Math.Ceiling(sessionTimeoutMs / (double)interval);

        return Math.Max(options.LifetimeCount, Math.Max(minimumForKeepAlive, minimumForSession));
    }

    private MonitoredItem CreateMonitoredItem(ResolvedNode node, OpcSubscriptionOptions subscriptionOptions)
    {
        // Absolute deadband: the server suppresses changes smaller than the
        // configured amount, so a noisy analogue signal does not saturate the
        // link with meaningless movement. Null disables filtering entirely.
        MonitoringFilter? filter = subscriptionOptions.DeadbandAbsolute > 0
            ? new DataChangeFilter
            {
                Trigger = DataChangeTrigger.StatusValue,
                DeadbandType = (uint)DeadbandType.Absolute,
                DeadbandValue = subscriptionOptions.DeadbandAbsolute
            }
            : null;

        var itemOptions = new MonitoredItemOptions
        {
            DisplayName = node.Node.DisplayName,
            StartNodeId = node.NodeId,
            AttributeId = Attributes.Value,
            MonitoringMode = MonitoringMode.Reporting,
            SamplingInterval = subscriptionOptions.SamplingIntervalMs,
            QueueSize = subscriptionOptions.QueueSize,
            DiscardOldest = subscriptionOptions.DiscardOldest,
            Filter = filter
        };

        var item = new MonitoredItem(_telemetry, itemOptions)
        {
            // Carries the configured id through the SDK so the notification
            // handler does not need a side table to know which node reported.
            Handle = node.Node.Id
        };

        item.Notification += OnNotification;
        return item;
    }

    private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        if (item.Handle is not string nodeId) return;

        var receivedAt = DateTimeOffset.UtcNow;

        // DequeueValues drains the queued notifications for this item, so a
        // burst that arrived in one publish response is not collapsed to its
        // last value.
        foreach (var dataValue in item.DequeueValues())
        {
            if (dataValue is null) continue;
            _events.Publish(new ReadingReceived(DataValueMapper.ToReading(nodeId, dataValue, receivedAt)));
        }
    }

    private async Task PrimeCurrentValuesAsync(
        ISession session,
        IReadOnlyList<ResolvedNode> nodes,
        CancellationToken cancellationToken)
    {
        var readValueIds = new ReadValueIdCollection(nodes.Select(n => new ReadValueId
        {
            NodeId = n.NodeId,
            AttributeId = Attributes.Value
        }));

        try
        {
            var response = await session.ReadAsync(
                null,
                0,
                TimestampsToReturn.Both,
                readValueIds,
                cancellationToken).ConfigureAwait(false);

            var receivedAt = DateTimeOffset.UtcNow;
            var values = response.Results;

            for (var i = 0; i < nodes.Count && i < values.Count; i++)
            {
                _events.Publish(new ReadingReceived(
                    DataValueMapper.ToReading(nodes[i].Node.Id, values[i], receivedAt)));
            }

            _logger.LogInformation("Primed {Count} node value(s) with an initial read.", Math.Min(nodes.Count, values.Count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: the subscription is already live and will deliver values
            // as they change. Only the immediate first paint is lost.
            _logger.LogWarning(ex, "Initial read failed; values will appear as they change.");
        }
    }

    /// <summary>Tears the subscription down. Safe to call when nothing is running.</summary>
    public async Task StopAsync()
    {
        var subscription = Interlocked.Exchange(ref _subscription, null);
        var session = Interlocked.Exchange(ref _session, null);

        if (subscription is null) return;

        foreach (var item in subscription.MonitoredItems)
        {
            item.Notification -= OnNotification;
        }

        try
        {
            if (session is not null && session.Connected)
            {
                await session.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false);
            }

            subscription.Dispose();
        }
        catch (Exception ex)
        {
            // A subscription on an already-dead session cannot be deleted
            // cleanly, and that is fine — the server drops it on session close.
            _logger.LogDebug(ex, "Ignoring error while tearing down subscription.");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
