using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace OpcMonitor.Infrastructure;

/// <summary>One entry in a browse result.</summary>
/// <param name="NodeId">Protocol node id, in the textual form the API accepts back as an address.</param>
/// <param name="BrowseName">Server-assigned browse name — the stable one.</param>
/// <param name="DisplayName">Localised label, which is what an operator recognises.</param>
/// <param name="NodeClass">"object" or "variable".</param>
/// <param name="IsVariable">True when the node has a value and can be monitored.</param>
/// <param name="HasChildren">True when browsing this node would return something.</param>
/// <param name="DataType">Resolved data type name for variables, e.g. "Double". Null when unknown.</param>
/// <param name="DisplayValue">Current value, read as part of the browse. Null for non-variables.</param>
/// <param name="Quality">Quality symbol of that read, so a bad node is visible before subscribing to it.</param>
public sealed record BrowsedNode(
    string NodeId,
    string BrowseName,
    string DisplayName,
    string NodeClass,
    bool IsVariable,
    bool HasChildren,
    string? DataType,
    string? DisplayValue,
    string? Quality);

/// <summary>Children of one node, plus the path taken to reach it.</summary>
public sealed record BrowseResultSet(
    string NodeId,
    IReadOnlyList<BrowsedNode> Children);

/// <summary>
/// Read-only exploration of a server's address space, one level at a time.
/// </summary>
/// <remarks>
/// <para>
/// One level per request rather than a recursive crawl. A real server's address
/// space is large enough that a full walk is measured in minutes, and a
/// dashboard that expands a folder needs an answer in milliseconds. Lazy
/// expansion also matches what the operator is actually doing — looking for one
/// tag, not enumerating the plant.
/// </para>
/// <para>
/// Values are read for the variables at the level being browsed, in one batched
/// Read. Browsing is how you find a tag; seeing its current value is how you
/// know it is the tag you wanted, and paying one extra round trip for that is a
/// good trade.
/// </para>
/// </remarks>
public sealed class OpcBrowseService
{
    /// <summary>
    /// Cap on children returned for one node. A folder with tens of thousands of
    /// children exists in the wild, and neither the browser nor the person using
    /// it benefits from receiving all of them.
    /// </summary>
    private const int MaxChildren = 500;

    private readonly ILogger<OpcBrowseService> _logger;

    public OpcBrowseService(ILogger<OpcBrowseService> logger) => _logger = logger;

    /// <summary>
    /// Browses the hierarchical children of <paramref name="address"/>, which may
    /// be a node id, a browse path, or null/empty for the Objects folder.
    /// </summary>
    public async Task<BrowseResultSet> BrowseAsync(
        ISession session,
        string? address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var parent = ResolveParent(session, address);

        var references = Deduplicate(
            await BrowseChildrenAsync(session, parent, cancellationToken).ConfigureAwait(false),
            session);

        var truncated = references.Count > MaxChildren;
        if (truncated)
        {
            _logger.LogInformation(
                "Node {Parent} has {Count} children; returning the first {Max}.",
                parent, references.Count, MaxChildren);
            references = references.Take(MaxChildren).ToList();
        }

        var variables = references.Where(r => r.NodeClass == NodeClass.Variable).ToList();

        var values = await ReadValuesAsync(session, variables, cancellationToken).ConfigureAwait(false);
        var expandable = await FindExpandableAsync(session, references, cancellationToken).ConfigureAwait(false);

        var children = new List<BrowsedNode>(references.Count);

        foreach (var reference in references)
        {
            var nodeId = ToNodeId(session, reference);
            var isVariable = reference.NodeClass == NodeClass.Variable;

            string? displayValue = null;
            string? quality = null;
            string? dataType = null;

            if (isVariable && values.TryGetValue(nodeId, out var dataValue))
            {
                var reading = DataValueMapper.ToReading(nodeId.ToString(), dataValue, DateTimeOffset.UtcNow);
                displayValue = reading.DisplayValue;
                quality = reading.Quality.Symbol;
                dataType = dataValue.Value?.GetType().Name;
            }

            children.Add(new BrowsedNode(
                NodeId: nodeId.ToString(),
                BrowseName: reference.BrowseName?.Name ?? string.Empty,
                DisplayName: reference.DisplayName?.Text
                             ?? reference.BrowseName?.Name
                             ?? nodeId.ToString(),
                NodeClass: isVariable ? "variable" : "object",
                IsVariable: isVariable,
                HasChildren: expandable.Contains(nodeId),
                DataType: dataType,
                DisplayValue: displayValue,
                Quality: quality));
        }

        return new BrowseResultSet(parent.ToString(), children);
    }

    /// <summary>
    /// Turns the requested address into a node id, defaulting to the Objects
    /// folder — the conventional entry point for a client that does not know
    /// what it is looking at yet.
    /// </summary>
    private static NodeId ResolveParent(ISession session, string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return ObjectIds.ObjectsFolder;

        return NodeId.Parse(session.MessageContext, address, new NodeIdParsingOptions
        {
            UpdateTables = false
        });
    }

    /// <summary>
    /// Establishes which of the browsed nodes are worth showing an expander for,
    /// in a single batched Browse.
    /// </summary>
    /// <remarks>
    /// Guessing from the node class instead would be wrong in both directions:
    /// an empty folder would offer an expander that reveals nothing, and a
    /// variable with properties or child components — which is normal for a
    /// structured tag — would offer none and hide them.
    /// </remarks>
    private async Task<HashSet<NodeId>> FindExpandableAsync(
        ISession session,
        IReadOnlyList<ReferenceDescription> references,
        CancellationToken cancellationToken)
    {
        var expandable = new HashSet<NodeId>();
        if (references.Count == 0) return expandable;

        var nodeIds = references.Select(r => ToNodeId(session, r)).ToList();

        var toBrowse = new BrowseDescriptionCollection(nodeIds.Select(id => new BrowseDescription
        {
            NodeId = id,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
            ResultMask = (uint)BrowseResultMask.None
        }));

        try
        {
            // One reference is all it takes to know the answer, so the server is
            // asked for exactly one per node.
            var response = await session
                .BrowseAsync(null, null, 1, toBrowse, cancellationToken)
                .ConfigureAwait(false);

            var results = response.Results;

            for (var i = 0; i < nodeIds.Count && i < results.Count; i++)
            {
                if (results[i].References is { Count: > 0 }) expandable.Add(nodeIds[i]);

                // Asking for one reference guarantees a continuation point on any
                // node with more; releasing it immediately keeps the server from
                // holding state for a question already answered.
                if (results[i].ContinuationPoint is { Length: > 0 })
                {
                    expandable.Add(nodeIds[i]);
                }
            }

            await ReleaseContinuationPointsAsync(session, results, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cosmetic information only. A server that refuses the batched browse
            // should not stop the level that was successfully browsed from being
            // shown, so every node is simply reported as expandable.
            _logger.LogDebug(ex, "Batched child-count browse failed; showing every node as expandable.");
            foreach (var id in nodeIds) expandable.Add(id);
        }

        return expandable;
    }

    private static async Task ReleaseContinuationPointsAsync(
        ISession session,
        BrowseResultCollection results,
        CancellationToken cancellationToken)
    {
        var points = new ByteStringCollection(
            results.Where(r => r.ContinuationPoint is { Length: > 0 }).Select(r => r.ContinuationPoint));

        if (points.Count == 0) return;

        try
        {
            await session.BrowseNextAsync(null, true, points, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort. The server expires these on its own schedule.
        }
    }

    /// <summary>
    /// A node reachable through more than one hierarchical reference — Organizes
    /// and HasComponent together, typically — would otherwise be listed twice.
    /// </summary>
    private static List<ReferenceDescription> Deduplicate(
        IEnumerable<ReferenceDescription> references,
        ISession session) =>
        references
            .GroupBy(r => ToNodeId(session, r))
            .Select(g => g.First())
            .ToList();

    private static NodeId ToNodeId(ISession session, ReferenceDescription reference) =>
        ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);

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

        // Servers cap references per response. Without following the
        // continuation point, a node late in a large folder simply appears not to
        // exist — which is a maddening way to fail.
        while (continuationPoint is { Length: > 0 } && results.Count <= MaxChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (_, continuationPoint, references) = await session
                .BrowseNextAsync(null, false, continuationPoint, cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(references);
        }

        return results;
    }

    private static async Task<Dictionary<NodeId, DataValue>> ReadValuesAsync(
        ISession session,
        IReadOnlyList<ReferenceDescription> variables,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<NodeId, DataValue>();
        if (variables.Count == 0) return values;

        var nodeIds = variables.Select(v => ToNodeId(session, v)).ToList();

        var readValueIds = new ReadValueIdCollection(
            nodeIds.Select(id => new ReadValueId { NodeId = id, AttributeId = Attributes.Value }));

        var response = await session
            .ReadAsync(null, 0, TimestampsToReturn.Both, readValueIds, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < nodeIds.Count && i < response.Results.Count; i++)
        {
            values[nodeIds[i]] = response.Results[i];
        }

        return values;
    }
}
