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

// Browsing needs the live session rather than the snapshot store, because the
// point is to find nodes that are *not* being monitored yet.
api.MapGet("/browse", async (
        string? nodeId,
        ResilientOpcClient client,
        OpcBrowseService browser,
        NodeSnapshotStore store,
        CancellationToken cancellationToken) =>
    {
        var session = client.CurrentSession;
        if (session is null)
        {
            return Results.Problem(
                title: "Not connected",
                detail: "There is no live OPC UA session to browse. Retry once the connection recovers.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // A node id is the natural key here, but the store is keyed by the
        // configured address — which for a browse-path node is not the node id.
        // Both forms are checked so an already-monitored node is marked as such
        // however it was originally configured.
        var monitored = store.Snapshot().Select(s => s.Node.Id).ToHashSet(StringComparer.Ordinal);

        try
        {
            var result = await browser.BrowseAsync(session, nodeId, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new BrowseResultDto(
                result.NodeId,
                result.Children.Select(c => new BrowsedNodeDto(
                    c.NodeId, c.BrowseName, c.DisplayName, c.NodeClass, c.IsVariable, c.HasChildren,
                    monitored.Contains(c.NodeId), c.DataType, c.DisplayValue, c.Quality)).ToList()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Problem(
                title: "Browse failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    })
    .WithName("BrowseAddressSpace")
    .WithSummary("Hierarchical children of a node, with current values. Omit nodeId for the Objects folder.");

api.MapPost("/nodes", async (
        SubscribeRequest request,
        NodeSubscriptionManager manager,
        CancellationToken cancellationToken) =>
    {
        var result = await manager.SubscribeAsync(new MonitoredNodeOptions
        {
            Address = request.Address,
            DisplayName = request.DisplayName,
            Unit = request.Unit,
            Minimum = request.Minimum,
            Maximum = request.Maximum
        }, cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            NodeChangeOutcome.Changed => Results.Created(
                $"/api/nodes/{Uri.EscapeDataString(result.Node!.Id)}",
                MonitoredNodeDto.From(result.Node)),

            NodeChangeOutcome.NoChange => result.Node is null
                ? Results.NoContent()
                : Results.Ok(MonitoredNodeDto.From(result.Node)),

            NodeChangeOutcome.NotFound => Results.Problem(
                title: "Node not found", detail: result.Detail, statusCode: StatusCodes.Status404NotFound),

            NodeChangeOutcome.NotConnected => Results.Problem(
                title: "Not connected", detail: result.Detail, statusCode: StatusCodes.Status503ServiceUnavailable),

            _ => Results.Problem(
                title: "Server rejected the node", detail: result.Detail, statusCode: StatusCodes.Status502BadGateway)
        };
    })
    .WithName("SubscribeToNode")
    .WithSummary("Starts monitoring a node. Survives reconnects for the lifetime of the process.");

api.MapDelete("/nodes/{*id}", async (
        string id,
        NodeSubscriptionManager manager,
        CancellationToken cancellationToken) =>
    {
        var result = await manager
            .UnsubscribeAsync(Uri.UnescapeDataString(id), cancellationToken)
            .ConfigureAwait(false);

        // Unsubscribing from something that is not subscribed has already
        // achieved what the caller wanted, so it is not an error.
        return result.Outcome is NodeChangeOutcome.Changed or NodeChangeOutcome.NoChange
            ? Results.NoContent()
            : Results.Problem(title: "Could not unsubscribe", detail: result.Detail, statusCode: 500);
    })
    .WithName("UnsubscribeFromNode");

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
