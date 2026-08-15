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
