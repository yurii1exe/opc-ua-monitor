namespace OpcMonitor.Domain;

/// <summary>
/// Current value of a node plus a short rolling window of recent values.
/// </summary>
/// <remarks>
/// The window exists so a browser that connects thirty seconds after the
/// service started still paints a populated sparkline instead of waiting for
/// the next data change. It is intentionally bounded and in-memory: this is a
/// live monitor, not a historian, and adding a database would buy nothing that
/// the OPC UA server's own historical access does not already do better.
/// </remarks>
public sealed record NodeStatus(
    MonitoredNode Node,
    NodeReading? Current,
    IReadOnlyList<NodeReading> Window)
{
    public static NodeStatus Empty(MonitoredNode node) =>
        new(node, null, Array.Empty<NodeReading>());

    /// <summary>True when the node has never reported, so the UI can show "—".</summary>
    public bool HasValue => Current is not null;

    /// <summary>
    /// Band classification of the current value, or null when there is no band
    /// configured, no value yet, or the value is not numeric.
    /// </summary>
    public bool? IsWithinBand => Current is null ? null : Node.IsWithinBand(Current.Value);
}
