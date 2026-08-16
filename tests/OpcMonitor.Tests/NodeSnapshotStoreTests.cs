using OpcMonitor.Domain;

namespace OpcMonitor.Tests;

public class NodeSnapshotStoreTests
{
    private static readonly MonitoredNode Temperature = new("temp", "Temperature", "degC", 10, 40);
    private static readonly MonitoredNode Pressure = new("press", "Pressure", "bar");

    private static NodeSnapshotStore CreateStore(int windowSize = 3) =>
        new([Temperature, Pressure], windowSize, "opc.tcp://localhost:62541");

    private static NodeReading Reading(string nodeId, object? value, int secondsOffset = 0) => new(
        nodeId,
        value,
        QualityCode.Good,
        DateTimeOffset.UnixEpoch.AddSeconds(secondsOffset),
        DateTimeOffset.UnixEpoch.AddSeconds(secondsOffset),
        DateTimeOffset.UnixEpoch.AddSeconds(secondsOffset));

    [Fact]
    public void StartsWithEveryConfiguredNodePresentButValueless()
    {
        var snapshot = CreateStore().Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.All(snapshot, s => Assert.False(s.HasValue));
        // Placeholder cards let the dashboard render its full layout before the
        // first value arrives, instead of growing as readings trickle in.
        Assert.All(snapshot, s => Assert.Null(s.Current));
    }

    [Fact]
    public void KeepsTheLatestValueAndABoundedWindow()
    {
        var store = CreateStore(windowSize: 3);

        for (var i = 1; i <= 5; i++)
        {
            store.Record(Reading("temp", i * 1.0, i));
        }

        var status = store.Get("temp");

        Assert.NotNull(status);
        Assert.Equal(5.0, status!.Current!.Value);
        Assert.Equal(3, status.Window.Count);
        Assert.Equal(new object?[] { 3.0, 4.0, 5.0 }, status.Window.Select(r => r.Value));
    }

    [Fact]
    public void IgnoresReadingsForNodesItWasNotAskedToWatch()
    {
        var store = CreateStore();

        Assert.False(store.Record(Reading("unexpected", 1.0)));
        Assert.Null(store.Get("unexpected"));
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public void ClassifiesTheCurrentValueAgainstTheConfiguredBand()
    {
        var store = CreateStore();

        store.Record(Reading("temp", 25.0));
        Assert.True(store.Get("temp")!.IsWithinBand);

        store.Record(Reading("temp", 55.0));
        Assert.False(store.Get("temp")!.IsWithinBand);
    }

    [Fact]
    public void HasNoOpinionAboutBandsItWasNotGiven()
    {
        var store = CreateStore();
        store.Record(Reading("press", 3.2));

        Assert.Null(store.Get("press")!.IsWithinBand);
    }

    [Fact]
    public void TracksConnectionStateSeparatelyFromReadings()
    {
        var store = CreateStore();
        Assert.Equal(ConnectionState.Disconnected, store.Connection.State);

        store.SetConnection(new ConnectionStatus(
            ConnectionState.Connected, "opc.tcp://localhost:62541", DateTimeOffset.UnixEpoch));

        Assert.True(store.Connection.IsHealthy);
    }

    [Fact]
    public void AcceptsNodesAddedAfterConstruction()
    {
        // Subscribing from the dashboard has to make the store accept readings
        // for a node that was not in the configuration file, otherwise the card
        // appears and then never updates.
        var store = CreateStore();
        var flow = new MonitoredNode("flow", "Flow", "L/min");

        Assert.False(store.Record(Reading("flow", 12.5)));

        Assert.True(store.Add(flow));
        Assert.True(store.Record(Reading("flow", 12.5)));
        Assert.Equal(12.5, store.Get("flow")!.Current!.Value);
        Assert.Equal(3, store.Snapshot().Count);
    }

    [Fact]
    public void TreatsAddingAKnownNodeAsANoOpRatherThanResettingIt()
    {
        // Double-clicking subscribe must not wipe the history of a node that is
        // already on screen.
        var store = CreateStore();
        store.Record(Reading("temp", 21.0));

        Assert.False(store.Add(new MonitoredNode("temp", "Temperature renamed")));

        Assert.Equal(21.0, store.Get("temp")!.Current!.Value);
        Assert.Equal("Temperature", store.Get("temp")!.Node.DisplayName);
    }

    [Fact]
    public void ForgetsRemovedNodesEntirely()
    {
        var store = CreateStore();
        store.Record(Reading("temp", 21.0));

        Assert.True(store.Remove("temp"));
        Assert.Null(store.Get("temp"));
        Assert.Single(store.Snapshot());

        // Readings still in flight when the unsubscribe landed are dropped, not
        // resurrected as a node nobody asked for.
        Assert.False(store.Record(Reading("temp", 22.0)));
        Assert.Null(store.Get("temp"));

        Assert.False(store.Remove("temp"));
    }

    [Fact]
    public void GivesAReSubscribedNodeAFreshWindow()
    {
        // The gap while a node was unsubscribed contains no data. Carrying the
        // old window across would draw a line through it and imply continuity
        // that was never observed.
        var store = CreateStore();
        store.Record(Reading("temp", 21.0));
        store.Remove("temp");
        store.Add(Temperature);

        Assert.Empty(store.Get("temp")!.Window);
        Assert.Null(store.Get("temp")!.Current);
    }

    [Fact]
    public async Task SurvivesConcurrentWritesAndReads()
    {
        // The SDK delivers notifications on its own threads while SignalR and
        // REST read the same store, so the concurrency is real, not theoretical.
        var store = CreateStore(windowSize: 10);

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) store.Record(Reading("temp", i + w * 1000.0, i));
        }));

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var snapshot = store.Snapshot();
                Assert.Equal(2, snapshot.Count);
                Assert.All(snapshot, s => Assert.True(s.Window.Count <= 10));
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(10, store.Get("temp")!.Window.Count);
    }
}
