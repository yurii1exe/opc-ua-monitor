using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpcMonitor.Api;
using OpcMonitor.Domain;
using OpcMonitor.Infrastructure;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Structured logging to stdout, which is where a container's logs belong.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    // The SDK logs routine channel traffic at Information; its warnings are the
    // part worth surfacing by default.
    .MinimumLevel.Override("Opc.Ua", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.AddOpcMonitoring(builder.Configuration);
builder.Services.AddHostedService<OpcMonitorWorker>();

builder.Services
    .AddSignalR()
    .AddJsonProtocol(o => ConfigureJson(o.PayloadSerializerOptions));

builder.Services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));

builder.Services
    .AddHealthChecks()
    .AddCheck<OpcConnectionHealthCheck>("opc-connection", tags: ["ready"]);

// The Angular dev server runs on a different origin than the API. Origins come
// from configuration so the container image does not need rebuilding to add one,
// and the default list contains only localhost.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:4200"];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    // Required for the SignalR browser client's negotiate request.
    .AllowCredentials()));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCors();

app.MapHub<MonitoringHub>("/hubs/monitoring");

var api = app.MapGroup("/api");

api.MapGet("/nodes", (NodeSnapshotStore store) =>
        store.Snapshot().Select(NodeStatusDto.From).ToList())
    .WithName("GetNodes")
    .WithSummary("Current value and rolling window for every monitored node.");

// Catch-all route parameter: a node's configured id may be a browse path such as
// "Server/ServerStatus/CurrentTime", and slashes are the norm rather than an
// edge case here.
api.MapGet("/nodes/{*id}", (string id, NodeSnapshotStore store) =>
    {
        var status = store.Get(Uri.UnescapeDataString(id));
        return status is null ? Results.NotFound() : Results.Ok(NodeStatusDto.From(status));
    })
    .WithName("GetNode");

api.MapGet("/history/{*id}", (string id, NodeSnapshotStore store) =>
    {
        var nodeId = Uri.UnescapeDataString(id);
        if (store.Get(nodeId) is null) return Results.NotFound();

        return Results.Ok(store.History(nodeId).Select(NodeReadingDto.From).ToList());
    })
    .WithName("GetNodeHistory")
    .WithSummary("Rolling in-memory window of recent readings. Not a historian.");

api.MapGet("/connection", (ResilientOpcClient client) =>
        ConnectionStatusDto.From(client.Status))
    .WithName("GetConnectionStatus");

// Health reports the OPC session state, not just process liveness, so a
// container healthcheck on this endpoint means something.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = WriteHealthResponse
});

app.Run();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
}

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";

    var payload = new
    {
        status = report.Status.ToString().ToLowerInvariant(),
        totalDurationMs = report.TotalDuration.TotalMilliseconds,
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString().ToLowerInvariant(),
            description = entry.Value.Description,
            data = entry.Value.Data
        })
    };

    return context.Response.WriteAsJsonAsync(payload);
}

/// <summary>Exposed so integration tests can reference the entry point assembly.</summary>
public partial class Program;
