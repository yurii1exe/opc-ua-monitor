namespace OpcMonitor.Domain;

/// <summary>
/// Lifecycle of the client's session with the OPC UA server.
/// </summary>
/// <remarks>
/// <see cref="Reconnecting"/> is deliberately distinct from
/// <see cref="Connecting"/>: the dashboard shows amber for a session that was
/// established and is being recovered, and grey for one that has never been up.
/// Collapsing the two loses the only signal an operator has that something
/// broke rather than never started.
/// </remarks>
public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Faulted = 4
}

/// <summary>
/// Connection state plus the context needed to render it, published to clients
/// whenever it changes and included in the snapshot sent on connect.
/// </summary>
/// <param name="State">Current state.</param>
/// <param name="EndpointUrl">Endpoint the client is using, for display.</param>
/// <param name="ChangedAt">When the state last changed.</param>
/// <param name="Attempt">Consecutive reconnect attempts; 0 while healthy.</param>
/// <param name="Detail">Short human-readable reason, e.g. the last error.</param>
public sealed record ConnectionStatus(
    ConnectionState State,
    string EndpointUrl,
    DateTimeOffset ChangedAt,
    int Attempt = 0,
    string? Detail = null)
{
    public static ConnectionStatus Initial(string endpointUrl) =>
        new(ConnectionState.Disconnected, endpointUrl, DateTimeOffset.UtcNow);

    public bool IsHealthy => State == ConnectionState.Connected;
}
