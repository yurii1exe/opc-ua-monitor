using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using OpcMonitor.Domain;

namespace OpcMonitor.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OPC UA client stack. The hosted service that drives it and
    /// the transport that fans events out live in the API project — this layer
    /// deliberately has no opinion about either.
    /// </summary>
    public static IServiceCollection AddOpcMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpcClientOptions>()
            .Bind(configuration.GetSection(OpcClientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Routes the SDK's own diagnostics into the host's logger rather than
        // letting it stand up a private logging pipeline.
        services.AddSingleton<ITelemetryContext>(sp =>
            new OpcTelemetryContext(sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<OpcEventChannel>();
        services.AddSingleton<OpcSessionFactory>();
        services.AddSingleton<NodeResolver>();
        services.AddSingleton<OpcSubscriptionService>();
        services.AddSingleton<ResilientOpcClient>();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcClientOptions>>().Value;

            // Seeded from configuration rather than from the server, so the
            // dashboard can render placeholder cards for every configured node
            // before the first connection succeeds. Nodes that fail to resolve
            // are removed once the client reports its resolved set.
            var nodes = options.Nodes.Select(n => new MonitoredNode(
                n.Id ?? n.Address,
                n.DisplayName ?? NodeAddress.DefaultDisplayName(n.Address),
                n.Unit,
                n.Minimum,
                n.Maximum));

            return new NodeSnapshotStore(nodes, options.HistoryWindowSize, options.EndpointUrl);
        });

        return services;
    }
}
