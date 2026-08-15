using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using OpcMonitor.Domain;

namespace OpcMonitor.Infrastructure;

/// <summary>A configured node paired with the node id it resolved to.</summary>
/// <param name="Node">Domain-facing description of the node.</param>
/// <param name="NodeId">Server-specific node id to monitor.</param>
public sealed record ResolvedNode(MonitoredNode Node, NodeId NodeId);

/// <summary>
/// Turns configured node addresses into node ids against a live session.
/// </summary>
public sealed class NodeResolver
{
    private readonly ILogger<NodeResolver> _logger;

    public NodeResolver(ILogger<NodeResolver> logger) => _logger = logger;

    /// <summary>
    /// Resolves every configured node. Nodes that cannot be resolved are skipped
    /// with a warning unless they are marked required, so one config file can
    /// list nodes for several server profiles and still start against any of
    /// them.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedNode>> ResolveAsync(
        ISession session,
        IEnumerable<MonitoredNodeOptions> configured,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedNode>();

        foreach (var option in configured)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(option.Address))
            {
                _logger.LogWarning("Skipping a configured node with an empty Address.");
                continue;
            }

            NodeId? nodeId;
            try
            {
                nodeId = await ResolveOneAsync(session, option.Address, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (option.Required) throw;
                _logger.LogWarning(ex, "Could not resolve node {Address}; skipping.", option.Address);
                continue;
            }

            if (nodeId is null || NodeId.IsNull(nodeId))
            {
                if (option.Required)
                {
                    throw new InvalidOperationException(
                        $"Required node '{option.Address}' does not exist on this server.");
                }

                _logger.LogWarning(
                    "Node {Address} does not exist on this server; skipping. Set Required=true to fail startup instead.",
                    option.Address);
                continue;
            }

            var node = new MonitoredNode(
                Id: option.Id ?? option.Address,
                DisplayName: option.DisplayName ?? NodeAddress.DefaultDisplayName(option.Address),
                Unit: option.Unit,
                Minimum: option.Minimum,
                Maximum: option.Maximum);

            _logger.LogInformation("Resolved {Address} -> {NodeId} ({DisplayName})",
                option.Address, nodeId, node.DisplayName);

            resolved.Add(new ResolvedNode(node, nodeId));
        }

        return resolved;
    }

    private async Task<NodeId?> ResolveOneAsync(ISession session, string address, CancellationToken cancellationToken)
    {
        if (NodeAddress.LooksLikeNodeId(address))
        {
            // Parsed against the session's namespace table so an nsu= form maps
            // onto this server's namespace index instead of being taken
            // literally.
            return NodeId.Parse(session.MessageContext, address, new NodeIdParsingOptions
            {
                // The session's namespace table is authoritative and already
                // populated; parsing must map an nsu= form onto it, never
                // append to it.
                UpdateTables = false
            });
        }

        var segments = NodeAddress.SplitBrowsePath(address);
        if (segments.Count == 0) return null;

        return await WalkBrowsePathAsync(session, ObjectIds.ObjectsFolder, segments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Walks a browse path one hierarchical level at a time, matching on the
    /// browse name and falling back to the display name.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>TranslateBrowsePathsToNodeIds</c>. That service needs
    /// each path element qualified with the target namespace index, which is
    /// exactly the server-specific detail this addressing scheme exists to
    /// avoid; a caller who already knows the namespace index can just write the
    /// node id. Walking costs one request per level at startup only.
    /// </remarks>
    private async Task<NodeId?> WalkBrowsePathAsync(
        ISession session,
        NodeId start,
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken)
    {
        var current = start;

        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var children = await BrowseChildrenAsync(session, current, cancellationToken).ConfigureAwait(false);

            var match =
                children.FirstOrDefault(r => string.Equals(r.BrowseName?.Name, segment, StringComparison.Ordinal))
                ?? children.FirstOrDefault(r => string.Equals(r.BrowseName?.Name, segment, StringComparison.OrdinalIgnoreCase))
                ?? children.FirstOrDefault(r => string.Equals(r.DisplayName?.Text, segment, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _logger.LogDebug("Browse path segment '{Segment}' not found under {Parent}.", segment, current);
                return null;
            }

            current = ExpandedNodeId.ToNodeId(match.NodeId, session.NamespaceUris);
            if (NodeId.IsNull(current)) return null;
        }

        return current;
    }

    private static async Task<List<ReferenceDescription>> BrowseChildrenAsync(
        ISession session,
        NodeId parent,
        CancellationToken cancellationToken)
    {
        var results = new List<ReferenceDescription>();

        var (_, continuationPoint, references) = await session.BrowseAsync(
            requestHeader: null,
            view: null,
            nodeToBrowse: parent,
            maxResultsToReturn: 0,
            browseDirection: BrowseDirection.Forward,
            referenceTypeId: ReferenceTypeIds.HierarchicalReferences,
            includeSubtypes: true,
            nodeClassMask: (uint)(NodeClass.Object | NodeClass.Variable),
            ct: cancellationToken).ConfigureAwait(false);

        results.AddRange(references);

        // Servers cap the number of references per response; without following
        // the continuation point a node late in a large folder simply appears
        // not to exist, which is a maddening way to fail.
        while (continuationPoint is { Length: > 0 })
        {
            cancellationToken.ThrowIfCancellationRequested();

            (_, continuationPoint, references) = await session.BrowseNextAsync(
                requestHeader: null,
                releaseContinuationPoint: false,
                continuationPoint: continuationPoint,
                ct: cancellationToken).ConfigureAwait(false);

            results.AddRange(references);
        }

        return results;
    }
}
