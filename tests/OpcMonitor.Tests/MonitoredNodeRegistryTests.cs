using Microsoft.Extensions.Options;
using OpcMonitor.Infrastructure;

namespace OpcMonitor.Tests;

/// <summary>
/// The registry is what makes a node subscribed from the dashboard survive a
/// reconnect, so its behaviour around identity and duplicates is worth pinning
/// down: the reconnect path resolves whatever is in here, and anything it gets
/// wrong shows up minutes later as a node that silently disappeared.
/// </summary>
public class MonitoredNodeRegistryTests
{
    private static MonitoredNodeRegistry CreateRegistry(params MonitoredNodeOptions[] configured) =>
        new(Options.Create(new OpcClientOptions { Nodes = configured.ToList() }));

    private static MonitoredNodeOptions Node(string address, string? id = null) =>
        new() { Address = address, Id = id };

    [Fact]
    public void StartsFromConfiguration()
    {
        var registry = CreateRegistry(Node("Tank/TankLevel"), Node("i=2258"));

        Assert.Equal(2, registry.Snapshot().Count);
        Assert.True(registry.Contains("Tank/TankLevel"));
        Assert.True(registry.Contains("i=2258"));
    }

    [Fact]
    public void IdentifiesANodeByItsExplicitIdWhenGivenOne()
    {
        var registry = CreateRegistry(Node("ns=2;s=Some.Tag", id: "level"));

        Assert.True(registry.Contains("level"));
        Assert.False(registry.Contains("ns=2;s=Some.Tag"));
    }

    [Fact]
    public void RefusesADuplicateWithoutDisturbingTheOriginal()
    {
        var registry = CreateRegistry(Node("Tank/TankLevel"));

        Assert.False(registry.Add(Node("Tank/TankLevel")));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void AddsAndRemovesAtRuntime()
    {
        var registry = CreateRegistry(Node("Tank/TankLevel"));

        Assert.True(registry.Add(Node("ns=1;i=1756")));
        Assert.Equal(2, registry.Snapshot().Count);

        Assert.True(registry.Remove("ns=1;i=1756"));
        Assert.False(registry.Remove("ns=1;i=1756"));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void HandsOutACopySoACallerCannotMutateItByAccident()
    {
        // The reconnect path iterates this list while a request thread may be
        // adding to it. Returning the live list would be a collection-modified
        // exception in the middle of a reconnect, which is the worst possible
        // moment for one.
        var registry = CreateRegistry(Node("Tank/TankLevel"));

        var snapshot = registry.Snapshot();
        registry.Add(Node("i=2258"));

        Assert.Single(snapshot);
        Assert.Equal(2, registry.Snapshot().Count);
    }

    [Fact]
    public void DoesNotWriteBackToConfiguration()
    {
        // A dashboard experiment must not silently become permanent state; a
        // restart returns to the configured node set.
        var configured = new List<MonitoredNodeOptions> { Node("Tank/TankLevel") };
        var options = new OpcClientOptions { Nodes = configured };
        var registry = new MonitoredNodeRegistry(Options.Create(options));

        registry.Add(Node("i=2258"));

        Assert.Single(options.Nodes);
    }
}
