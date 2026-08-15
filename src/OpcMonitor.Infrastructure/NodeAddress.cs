namespace OpcMonitor.Infrastructure;

/// <summary>
/// Classifies a configured node address as either a protocol node id or a
/// browse path.
/// </summary>
/// <remarks>
/// <para>
/// Configuration accepts both because they solve different problems. A node id
/// such as <c>i=2258</c> is exact and cheap, and the well-known ids from OPC UA
/// Part 6 Annex A are identical on every compliant server. A browse path such as
/// <c>Server/ServerStatus/CurrentTime</c> survives the thing that breaks
/// portable configuration: namespace indices are assigned per server and can
/// change between restarts, so <c>ns=2;s=Something</c> copied from one server is
/// not guaranteed to mean the same thing on another.
/// </para>
/// <para>
/// The distinction is made by prefix rather than by attempting to parse, because
/// <c>NodeId.Parse</c> happily accepts an unprefixed string as a namespace-zero
/// string identifier and would silently swallow every browse path.
/// </para>
/// </remarks>
public static class NodeAddress
{
    private static readonly string[] NodeIdPrefixes = ["i=", "s=", "g=", "b=", "ns=", "nsu="];

    public static bool LooksLikeNodeId(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var trimmed = address.TrimStart();
        return NodeIdPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Splits a browse path into its segments. Leading and trailing separators
    /// and empty segments are ignored so both <c>/Server/Status</c> and
    /// <c>Server/Status</c> mean the same thing.
    /// </summary>
    public static IReadOnlyList<string> SplitBrowsePath(string address) =>
        address.Split(['/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Default display name for an address: the last browse-path segment, or the
    /// address itself when it is a node id.
    /// </summary>
    public static string DefaultDisplayName(string address)
    {
        if (LooksLikeNodeId(address)) return address;
        var segments = SplitBrowsePath(address);
        return segments.Count > 0 ? segments[^1] : address;
    }
}
