using Microsoft.Extensions.Options;

namespace OpcMonitor.Infrastructure;

/// <summary>
/// The set of nodes the service is <i>meant</i> to be watching, as opposed to the
/// set it currently has monitored items for.
/// </summary>
/// <remarks>
/// <para>
/// Configuration seeds this once at startup, and after that it is the authority.
/// Without it, a node subscribed from the dashboard would vanish at the next
/// reconnect, because the reconnect path resolves its node list from somewhere —
/// and if that somewhere is <see cref="OpcClientOptions.Nodes"/>, it is the list
/// from the config file, not the list the operator is actually looking at.
/// </para>
/// <para>
/// Held in memory only. A restart returns to the configured set, which is the
/// behaviour you want from a monitor whose node list is configuration: a
/// dashboard experiment should not silently become permanent state.
/// </para>
/// </remarks>
public sealed class MonitoredNodeRegistry
{
    private readonly object _gate = new();
    private readonly List<MonitoredNodeOptions> _nodes;

    public MonitoredNodeRegistry(IOptions<OpcClientOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _nodes = options.Value.Nodes.ToList();
    }

    /// <summary>Stable id a configured node is known by. Mirrors <see cref="NodeResolver"/>.</summary>
    public static string IdOf(MonitoredNodeOptions option) => option.Id ?? option.Address;

    public IReadOnlyList<MonitoredNodeOptions> Snapshot()
    {
        lock (_gate) return _nodes.ToList();
    }

    public bool Contains(string id)
    {
        lock (_gate) return _nodes.Any(n => string.Equals(IdOf(n), id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Adds a node. Returns false when one with the same id is already present —
    /// subscribing twice to the same tag is a normal double-click, not an error.
    /// </summary>
    public bool Add(MonitoredNodeOptions option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var id = IdOf(option);

        lock (_gate)
        {
            if (_nodes.Any(n => string.Equals(IdOf(n), id, StringComparison.Ordinal))) return false;
            _nodes.Add(option);
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var index = _nodes.FindIndex(n => string.Equals(IdOf(n), id, StringComparison.Ordinal));
            if (index < 0) return false;

            _nodes.RemoveAt(index);
            return true;
        }
    }
}
