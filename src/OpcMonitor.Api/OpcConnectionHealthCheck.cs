using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpcMonitor.Domain;
using OpcMonitor.Infrastructure;

namespace OpcMonitor.Api;

/// <summary>
/// Reports the OPC session state as the service's health.
/// </summary>
/// <remarks>
/// <para>
/// A monitoring service that is up but not connected to anything is not healthy
/// in any sense a user cares about, so the health endpoint reflects the OPC
/// session rather than merely the fact that ASP.NET Core is answering. That is
/// what makes a Compose or Kubernetes healthcheck on this endpoint meaningful.
/// </para>
/// <para>
/// Reconnecting is <see cref="HealthStatus.Degraded"/> rather than Unhealthy:
/// the service is doing exactly what it should during a server restart, still
/// serving the last known values, and an orchestrator that restarts the
/// container mid-backoff makes recovery slower, not faster.
/// </para>
/// </remarks>
public sealed class OpcConnectionHealthCheck : IHealthCheck
{
    private readonly ResilientOpcClient _client;

    public OpcConnectionHealthCheck(ResilientOpcClient client) => _client = client;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = _client.Status;

        var data = new Dictionary<string, object>
        {
            ["state"] = status.State.ToString(),
            ["endpoint"] = status.EndpointUrl,
            ["changedAt"] = status.ChangedAt,
            ["attempt"] = status.Attempt
        };

        var result = status.State switch
        {
            ConnectionState.Connected =>
                HealthCheckResult.Healthy($"Connected to {status.EndpointUrl}.", data),
            ConnectionState.Connecting or ConnectionState.Reconnecting =>
                HealthCheckResult.Degraded($"{status.State} to {status.EndpointUrl}. {status.Detail}", data: data),
            _ =>
                HealthCheckResult.Unhealthy($"{status.State}. {status.Detail}", data: data)
        };

        return Task.FromResult(result);
    }
}
