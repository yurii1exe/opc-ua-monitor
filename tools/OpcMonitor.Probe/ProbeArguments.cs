namespace OpcMonitor.Probe;

/// <summary>Command-line options for <c>opcprobe</c>.</summary>
public sealed class ProbeArguments
{
    public string? EndpointUrl { get; private init; }

    /// <summary>How many levels of the address space to print. 0 disables the browse.</summary>
    public int BrowseDepth { get; private init; } = 2;

    /// <summary>Seconds to stay subscribed and print live changes. 0 reads once and exits.</summary>
    public int WatchSeconds { get; private init; }

    public bool NoSecurity { get; private init; }

    public bool Verbose { get; private init; }

    public bool ShowHelp { get; private init; }

    public static ProbeArguments Parse(string[] args)
    {
        string? endpoint = null;
        var depth = 2;
        var watch = 0;
        var noSecurity = false;
        var verbose = false;
        var help = args.Length == 0 && false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-e" or "--endpoint" when i + 1 < args.Length:
                    endpoint = args[++i];
                    break;
                case "-d" or "--depth" when i + 1 < args.Length:
                    depth = int.TryParse(args[++i], out var d) ? d : depth;
                    break;
                case "-w" or "--watch" when i + 1 < args.Length:
                    watch = int.TryParse(args[++i], out var w) ? w : watch;
                    break;
                case "--no-security":
                    noSecurity = true;
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                case "-h" or "--help":
                    help = true;
                    break;
            }
        }

        return new ProbeArguments
        {
            EndpointUrl = endpoint,
            BrowseDepth = depth,
            WatchSeconds = watch,
            NoSecurity = noSecurity,
            Verbose = verbose,
            ShowHelp = help
        };
    }

    /// <summary>
    /// Command-line values expressed as configuration overrides, so the probe
    /// and the service read the identical option shape and a flag here proves
    /// something about the service too.
    /// </summary>
    public Dictionary<string, string?> ToOverrides()
    {
        var overrides = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(EndpointUrl))
        {
            overrides["Opc:EndpointUrl"] = EndpointUrl;
        }

        if (NoSecurity)
        {
            overrides["Opc:UseSecurity"] = "false";
        }

        return overrides;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            opcprobe — connect to an OPC UA server, browse its address space and read values.

            Usage:
              opcprobe [options]

            Options:
              -e, --endpoint <url>   Endpoint to connect to (default: from appsettings.json)
              -d, --depth <n>        Address-space levels to print (default 2, 0 to skip)
              -w, --watch <seconds>  Subscribe and print live changes for n seconds
                  --no-security      Prefer an unsecured endpoint
              -v, --verbose          Include SDK debug logging
              -h, --help             Show this help

            Examples:
              opcprobe -e opc.tcp://localhost:62541 -d 3
              opcprobe -e opc.tcp://simulator:62541 -w 15
            """);
    }
}
