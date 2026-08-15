using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using OpcMonitor.Infrastructure;

namespace OpcMonitor.Probe;

/// <summary>
/// Runs one diagnostic pass: connect, describe the endpoint, browse the address
/// space, read the configured nodes, and optionally watch them change.
/// </summary>
public sealed class ProbeRunner
{
    private readonly OpcSessionFactory _sessionFactory;
    private readonly NodeResolver _nodeResolver;
    private readonly OpcSubscriptionService _subscriptions;
    private readonly OpcEventChannel _events;
    private readonly OpcClientOptions _options;
    private readonly ILogger _logger;

    public ProbeRunner(
        OpcSessionFactory sessionFactory,
        NodeResolver nodeResolver,
        OpcSubscriptionService subscriptions,
        OpcEventChannel events,
        OpcClientOptions options,
        ILogger logger)
    {
        _sessionFactory = sessionFactory;
        _nodeResolver = nodeResolver;
        _subscriptions = subscriptions;
        _events = events;
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(ProbeArguments arguments, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"  endpoint      {_options.EndpointUrl}");
        Console.WriteLine($"  applicationUri {_options.ApplicationUri}");
        Console.WriteLine();

        ISession? session = null;

        try
        {
            session = await _sessionFactory.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            PrintSessionSummary(session);

            if (arguments.BrowseDepth > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Address space:");
                await PrintAddressSpaceAsync(session, ObjectIds.ObjectsFolder, arguments.BrowseDepth, "  ", cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolved = await _nodeResolver
                .ResolveAsync(session, _options.Nodes, cancellationToken)
                .ConfigureAwait(false);

            if (resolved.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No configured nodes resolved on this server. Connection itself is fine.");
                return 0;
            }

            if (arguments.WatchSeconds > 0)
            {
                await WatchAsync(session, resolved, arguments.WatchSeconds, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Configured nodes:");
                await ReadAndPrintAsync(session, resolved, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            await _subscriptions.StopAsync().ConfigureAwait(false);

            if (session is not null)
            {
                try
                {
                    await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ignoring session close error.");
                }

                session.Dispose();
            }
        }
    }

    private static void PrintSessionSummary(ISession session)
    {
        var description = session.ConfiguredEndpoint?.Description;

        Console.WriteLine("Connected.");
        Console.WriteLine($"  session       {session.SessionName}");
        Console.WriteLine($"  server        {description?.Server?.ApplicationUri ?? "(unknown)"}");
        Console.WriteLine($"  endpoint used {description?.EndpointUrl ?? "(unknown)"}");
        Console.WriteLine($"  security      {description?.SecurityMode} / {ShortPolicy(description?.SecurityPolicyUri)}");
        Console.WriteLine($"  namespaces    {session.NamespaceUris?.Count ?? 0}");

        if (session.NamespaceUris is { Count: > 0 })
        {
            for (var i = 0; i < session.NamespaceUris.Count; i++)
            {
                Console.WriteLine($"                ns={i} {session.NamespaceUris.GetString((uint)i)}");
            }
        }
    }

    private static string ShortPolicy(string? policyUri) =>
        string.IsNullOrEmpty(policyUri) ? "(none)" : policyUri[(policyUri.LastIndexOf('#') + 1)..];

    /// <summary>
    /// Prints the hierarchy under a node, reading the value of every variable it
    /// finds at each level in a single batched Read.
    /// </summary>
    private async Task PrintAddressSpaceAsync(
        ISession session,
        NodeId parent,
        int remainingDepth,
        string indent,
        CancellationToken cancellationToken)
    {
        if (remainingDepth <= 0) return;

        var references = await BrowseAllAsync(session, parent, cancellationToken).ConfigureAwait(false);
        if (references.Count == 0) return;

        // A node can be reachable through more than one hierarchical reference
        // (Organizes and HasComponent, typically). For a listing that is a
        // duplicate, not information.
        references = references
            .GroupBy(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris))
            .Select(g => g.First())
            .ToList();

        var variables = references
            .Where(r => r.NodeClass == NodeClass.Variable)
            .ToList();

        var values = await ReadValuesAsync(session, variables, cancellationToken).ConfigureAwait(false);

        foreach (var reference in references)
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            var name = reference.BrowseName?.Name ?? reference.DisplayName?.Text ?? "(unnamed)";

            if (reference.NodeClass == NodeClass.Variable && values.TryGetValue(nodeId, out var dataValue))
            {
                Console.WriteLine($"{indent}{name,-28} {nodeId,-34} = {Format(dataValue)}");
            }
            else
            {
                Console.WriteLine($"{indent}{name,-28} {nodeId}");
            }

            if (reference.NodeClass == NodeClass.Object)
            {
                await PrintAddressSpaceAsync(session, nodeId, remainingDepth - 1, indent + "  ", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<List<ReferenceDescription>> BrowseAllAsync(
        ISession session,
        NodeId parent,
        CancellationToken cancellationToken)
    {
        var results = new List<ReferenceDescription>();

        var (_, continuationPoint, references) = await session.BrowseAsync(
            null, null, parent, 0, BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences, true,
            (uint)(NodeClass.Object | NodeClass.Variable),
            cancellationToken).ConfigureAwait(false);

        results.AddRange(references);

        while (continuationPoint is { Length: > 0 })
        {
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

        var nodeIds = variables
            .Select(v => ExpandedNodeId.ToNodeId(v.NodeId, session.NamespaceUris))
            .ToList();

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

    private async Task ReadAndPrintAsync(
        ISession session,
        IReadOnlyList<ResolvedNode> nodes,
        CancellationToken cancellationToken)
    {
        var readValueIds = new ReadValueIdCollection(
            nodes.Select(n => new ReadValueId { NodeId = n.NodeId, AttributeId = Attributes.Value }));

        var response = await session
            .ReadAsync(null, 0, TimestampsToReturn.Both, readValueIds, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < nodes.Count && i < response.Results.Count; i++)
        {
            Console.WriteLine($"  {nodes[i].Node.DisplayName,-28} = {Format(response.Results[i])}");
        }
    }

    /// <summary>
    /// Subscribes through the same service the API uses and prints every event
    /// that comes out of it, so a live tail here is evidence about the real
    /// pipeline rather than about a parallel implementation written for the tool.
    /// </summary>
    private async Task WatchAsync(
        ISession session,
        IReadOnlyList<ResolvedNode> nodes,
        int seconds,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"Watching {nodes.Count} node(s) for {seconds}s — Ctrl+C to stop.");
        Console.WriteLine();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TimeSpan.FromSeconds(seconds));

        await _subscriptions.StartAsync(session, nodes, cancellationToken).ConfigureAwait(false);

        var received = 0;

        try
        {
            await foreach (var opcEvent in _events.Reader.ReadAllAsync(window.Token).ConfigureAwait(false))
            {
                if (opcEvent is not ReadingReceived(var reading)) continue;

                received++;
                Console.WriteLine(
                    $"  {reading.EffectiveTimestamp:HH:mm:ss.fff}  {reading.NodeId,-40} {reading.DisplayValue,-24} {reading.Quality.Symbol}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Watch window elapsed; that is the normal way out.
        }

        Console.WriteLine();
        Console.WriteLine($"{received} update(s) received.");
    }

    private static string Format(DataValue dataValue)
    {
        var reading = DataValueMapper.ToReading("probe", dataValue, DateTimeOffset.UtcNow);
        return reading.Quality.IsGood
            ? reading.DisplayValue
            : $"{reading.DisplayValue} [{reading.Quality.Symbol}]";
    }
}
