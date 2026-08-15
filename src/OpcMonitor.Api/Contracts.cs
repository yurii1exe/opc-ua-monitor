using OpcMonitor.Domain;

namespace OpcMonitor.Api;

/// <summary>
/// Wire contracts for the REST endpoints and the SignalR hub.
/// </summary>
/// <remarks>
/// Separate from the domain records on purpose. The domain carries a
/// <see cref="QualityCode"/> whose raw 32-bit value is meaningful to a
/// diagnostic tool and noise to a dashboard, and a browser has no use for the
/// distinction between a null value and an absent one. Flattening here means the
/// UI contract can stay stable while the domain evolves, and it is the one place
/// to look to answer "what exactly does the client receive".
/// </remarks>
public sealed record NodeReadingDto(
    string NodeId,
    object? Value,
    string DisplayValue,
    string Quality,
    string QualitySeverity,
    DateTimeOffset Timestamp,
    DateTimeOffset ReceivedAt)
{
    public static NodeReadingDto From(NodeReading reading) => new(
        reading.NodeId,
        reading.Value,
        reading.DisplayValue,
        reading.Quality.Symbol,
        reading.Quality.Severity.ToString().ToLowerInvariant(),
        reading.EffectiveTimestamp,
        reading.ReceivedAt);
}

public sealed record MonitoredNodeDto(
    string Id,
    string DisplayName,
    string? Unit,
    double? Minimum,
    double? Maximum)
{
    public static MonitoredNodeDto From(MonitoredNode node) =>
        new(node.Id, node.DisplayName, node.Unit, node.Minimum, node.Maximum);
}

public sealed record NodeStatusDto(
    MonitoredNodeDto Node,
    NodeReadingDto? Current,
    bool? WithinBand,
    IReadOnlyList<NodeReadingDto> Window)
{
    public static NodeStatusDto From(NodeStatus status) => new(
        MonitoredNodeDto.From(status.Node),
        status.Current is null ? null : NodeReadingDto.From(status.Current),
        status.IsWithinBand,
        status.Window.Select(NodeReadingDto.From).ToList());
}

public sealed record ConnectionStatusDto(
    string State,
    string EndpointUrl,
    DateTimeOffset ChangedAt,
    int Attempt,
    string? Detail)
{
    public static ConnectionStatusDto From(ConnectionStatus status) => new(
        status.State.ToString().ToLowerInvariant(),
        status.EndpointUrl,
        status.ChangedAt,
        status.Attempt,
        status.Detail);
}

/// <summary>
/// Everything a freshly connected client needs for its first paint, in one
/// message: the node list, their current values, their recent history and the
/// connection state. Sending this on connect is what stops the dashboard opening
/// empty and waiting for a value to happen to change.
/// </summary>
public sealed record SnapshotDto(
    ConnectionStatusDto Connection,
    IReadOnlyList<NodeStatusDto> Nodes,
    DateTimeOffset ServerTime);
