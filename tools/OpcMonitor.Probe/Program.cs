using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpcMonitor.Infrastructure;
using OpcMonitor.Probe;

// opcprobe — the diagnostic tool for "is the connection itself working?".
//
// It exists because every other layer of this system is downstream of one
// question: can this process open a session to that server and read a value.
// When the dashboard is blank, running this first tells you in ten seconds
// whether the problem is OPC UA or everything after it.

var options = ProbeArguments.Parse(args);

if (options.ShowHelp)
{
    ProbeArguments.PrintUsage();
    return 0;
}

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddInMemoryCollection(options.ToOverrides())
    .Build();

var services = new ServiceCollection();

services.AddLogging(logging =>
{
    logging.AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    });
    logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);
    // The SDK is chatty at Information about routine channel activity; its
    // warnings and errors are the parts worth seeing here.
    logging.AddFilter("Opc.Ua", options.Verbose ? LogLevel.Debug : LogLevel.Warning);
});

services.AddOpcMonitoring(configuration);

await using var provider = services.BuildServiceProvider();

var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("probe");
var clientOptions = provider.GetRequiredService<IOptions<OpcClientOptions>>().Value;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var runner = new ProbeRunner(
    provider.GetRequiredService<OpcSessionFactory>(),
    provider.GetRequiredService<NodeResolver>(),
    provider.GetRequiredService<OpcSubscriptionService>(),
    provider.GetRequiredService<OpcEventChannel>(),
    clientOptions,
    logger);

try
{
    return await runner.RunAsync(options, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    logger.LogInformation("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    logger.LogError(ex, "Probe failed: {Message}", ex.Message);
    return 1;
}
