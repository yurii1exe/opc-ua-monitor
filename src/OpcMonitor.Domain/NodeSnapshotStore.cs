using System.Collections.Concurrent;

namespace OpcMonitor.Domain;

/// <summary>
/// In-memory current-value store with a bounded rolling window per node.
/// </summary>
/// <remarks>
/// Thread-safe: the OPC subscription callback writes from an SDK thread while
/// SignalR and REST callers read from request threads. Each node keeps its own
/// small lock rather than one global lock, so a burst on one node does not stall
/// reads of another.
/// </remarks>
public sealed class NodeSnapshotStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _windowSize;

    private volatile ConnectionStatus _connection;

    public NodeSnapshotStore(IEnumerable<MonitoredNode> nodes, int windowSize, string endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (windowSize < 1) throw new ArgumentOutOfRangeException(nameof(windowSize));

        _windowSize = windowSize;
        _connection = ConnectionStatus.Initial(endpointUrl);

        foreach (var node in nodes)
        {
            _entries[node.Id] = new Entry(node, windowSize);
        }
    }

    public int WindowSize => _windowSize;

    public ConnectionStatus Connection => _connection;

    public ConnectionStatus SetConnection(ConnectionStatus status)
    {
        _connection = status;
        return status;
    }

    /// <summary>Nodes in configuration order is not guaranteed; sort by id for stable output.</summary>
    public IReadOnlyList<NodeStatus> Snapshot() =>
        _entries.Values
            .Select(e => e.Read())
            .OrderBy(s => s.Node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public NodeStatus? Get(string nodeId) =>
        _entries.TryGetValue(nodeId, out var entry) ? entry.Read() : null;

    /// <summary>
    /// Starts tracking a node, so the dashboard gets a card for it before the
    /// first value arrives. Returns false when the node is already tracked, which
    /// is the ordinary result of subscribing to something twice and not an error.
    /// </summary>
    public bool Add(MonitoredNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _entries.TryAdd(node.Id, new Entry(node, _windowSize));
    }

    /// <summary>
    /// Stops tracking a node and discards its window. Returns false when there
    /// was nothing to remove.
    /// </summary>
    /// <remarks>
    /// The window is dropped rather than retained. Re-subscribing to a node after
    /// unsubscribing gives a fresh chart, which is the honest picture: the gap
    /// while it was unsubscribed contains no data, and a chart that stitches
    /// across it would imply continuity that was never observed.
    /// </remarks>
    public bool Remove(string nodeId) => _entries.TryRemove(nodeId, out _);

    public IReadOnlyList<NodeReading> History(string nodeId) =>
        _entries.TryGetValue(nodeId, out var entry) ? entry.Read().Window : Array.Empty<NodeReading>();

    /// <summary>
    /// Records a reading. Readings for unknown nodes are ignored rather than
    /// creating an entry: the configured node set is the contract, and a
    /// surprise node id means a bug worth noticing, not data worth keeping.
    /// </summary>
    public bool Record(NodeReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (!_entries.TryGetValue(reading.NodeId, out var entry)) return false;

        entry.Append(reading);
        return true;
    }

    private sealed class Entry(MonitoredNode node, int windowSize)
    {
        private readonly Queue<NodeReading> _window = new(windowSize);
        private readonly object _gate = new();
        private NodeReading? _current;

        public void Append(NodeReading reading)
        {
            lock (_gate)
            {
                _current = reading;
                _window.Enqueue(reading);
                while (_window.Count > windowSize) _window.Dequeue();
            }
        }

        public NodeStatus Read()
        {
            lock (_gate)
            {
                return new NodeStatus(node, _current, _window.ToArray());
            }
        }
    }
}
