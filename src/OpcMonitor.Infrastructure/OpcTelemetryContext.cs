using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace OpcMonitor.Infrastructure;

/// <summary>
/// Bridges the OPC UA SDK's telemetry abstraction onto the host's own logging,
/// tracing and metrics.
/// </summary>
/// <remarks>
/// The SDK asks for an <see cref="ITelemetryContext"/> and will happily build
/// itself a private one. Handing it the application's
/// <see cref="ILoggerFactory"/> instead means protocol-level diagnostics —
/// certificate rejections, channel faults, publish errors — land in the same
/// structured log as everything else, which is the difference between debugging
/// a failed connection from the application log and having to enable a separate
/// SDK trace file to find out what happened.
/// </remarks>
public sealed class OpcTelemetryContext : ITelemetryContext, IDisposable
{
    public const string SourceName = "OpcMonitor.Opc";

    private readonly Meter _meter;

    public OpcTelemetryContext(ILoggerFactory loggerFactory, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var resolvedVersion = version ?? typeof(OpcTelemetryContext).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        LoggerFactory = loggerFactory;
        ActivitySource = new ActivitySource(SourceName, resolvedVersion);
        _meter = new Meter(SourceName, resolvedVersion);
    }

    public ILoggerFactory LoggerFactory { get; }

    public ActivitySource ActivitySource { get; }

    public Meter CreateMeter() => _meter;

    public void Dispose()
    {
        ActivitySource.Dispose();
        _meter.Dispose();
    }
}
