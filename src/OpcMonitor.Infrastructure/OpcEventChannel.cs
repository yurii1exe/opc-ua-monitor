using System.Threading.Channels;
using OpcMonitor.Domain;

namespace OpcMonitor.Infrastructure;

/// <summary>Something the OPC client wants the rest of the process to know.</summary>
public abstract record OpcEvent;

/// <summary>A node reported a new value.</summary>
public sealed record ReadingReceived(NodeReading Reading) : OpcEvent;

/// <summary>The session's connection state changed.</summary>
public sealed record ConnectionStateChanged(ConnectionStatus Status) : OpcEvent;

/// <summary>The set of successfully resolved nodes changed (startup or reconnect).</summary>
public sealed record NodeSetChanged(IReadOnlyList<MonitoredNode> Nodes) : OpcEvent;

/// <summary>
/// Bounded, single-reader channel between the OPC SDK's callback threads and the
/// hosted service that fans events out to SignalR.
/// </summary>
/// <remarks>
/// Bounded on purpose. The SDK raises notifications on its own threads and will
/// not slow down for a slow consumer, so an unbounded queue turns a stalled
/// SignalR fan-out into unbounded memory growth. <see cref="BoundedChannelFullMode.DropOldest"/>
/// is the right overflow policy for a live monitor: if the process cannot keep
/// up, the newest value is the one an operator needs, and stale readings have no
/// value once a fresher one exists.
/// </remarks>
public sealed class OpcEventChannel
{
    private readonly Channel<OpcEvent> _channel;
    private long _dropped;

    public OpcEventChannel(int capacity = 4096)
    {
        _channel = Channel.CreateBounded<OpcEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public ChannelReader<OpcEvent> Reader => _channel.Reader;

    /// <summary>Number of events discarded because the consumer fell behind.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Never blocks and never throws on a full channel; DropOldest guarantees
    /// the write succeeds. Returns false only after the channel is completed.
    /// </summary>
    public bool Publish(OpcEvent opcEvent) => _channel.Writer.TryWrite(opcEvent);

    public void Complete() => _channel.Writer.TryComplete();
}
