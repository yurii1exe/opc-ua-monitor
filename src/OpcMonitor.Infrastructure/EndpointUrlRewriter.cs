namespace OpcMonitor.Infrastructure;

/// <summary>
/// Rewrites a server-advertised endpoint URL so it keeps the host and port the
/// client actually used to reach the server.
/// </summary>
/// <remarks>
/// <para>
/// This is the fix for the single most common way a containerised OPC UA client
/// fails. Per OPC UA Part 4 §5.4.2, <c>GetEndpoints</c> returns
/// <c>EndpointDescription.EndpointUrl</c> values that the <i>server</i>
/// constructs, using the hostname the server believes it has. A server running
/// as a Compose service advertises its container hostname; a server behind a
/// published port advertises the container's internal name; a server behind NAT
/// advertises its private address. In every one of those cases the client
/// connected successfully to the URL it was given, then throws the good URL away
/// and reconnects to an unroutable one.
/// </para>
/// <para>
/// The correct client-side response is to keep the authority component that
/// demonstrably works and preserve everything else the server said — the scheme
/// stays the server's, and the path matters because many servers expose
/// multiple endpoints under distinct paths.
/// </para>
/// <para>
/// Pure and static so it can be tested without a server, which is the point:
/// the failure it prevents is otherwise only reproducible inside Docker.
/// </para>
/// </remarks>
public static class EndpointUrlRewriter
{
    /// <summary>
    /// Returns <paramref name="advertisedUrl"/> with its host and port replaced
    /// by those of <paramref name="requestedUrl"/>.
    /// </summary>
    /// <returns>
    /// The rewritten URL, or <paramref name="advertisedUrl"/> unchanged when
    /// either input cannot be parsed or no rewrite is needed. Never throws: a
    /// malformed advertised URL is the server's problem and should surface as a
    /// connection error with the original text, not as an exception here.
    /// </returns>
    public static string PreserveRequestedHost(string advertisedUrl, string requestedUrl)
    {
        if (string.IsNullOrWhiteSpace(advertisedUrl)) return advertisedUrl;
        if (string.IsNullOrWhiteSpace(requestedUrl)) return advertisedUrl;

        if (!Uri.TryCreate(advertisedUrl, UriKind.Absolute, out var advertised)) return advertisedUrl;
        if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out var requested)) return advertisedUrl;

        // Same authority already: nothing to do, and returning the original
        // preserves the server's exact formatting.
        if (string.Equals(advertised.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
            && advertised.Port == requested.Port)
        {
            return advertisedUrl;
        }

        var builder = new UriBuilder(advertised)
        {
            Host = requested.Host,
            Port = requested.Port
        };

        // UriBuilder.ToString() re-encodes and can append a trailing slash for
        // an empty path; opc.tcp endpoints are compared as strings by some
        // servers, so reassemble by hand to keep the form the server used.
        var path = advertised.PathAndQuery;
        if (path == "/" && !advertisedUrl.TrimEnd().EndsWith('/')) path = string.Empty;

        return $"{builder.Scheme}://{FormatAuthority(requested.Host, requested.Port)}{path}";
    }

    /// <summary>
    /// Returns true when the two URLs differ in host or port, i.e. when the
    /// server advertised something other than what the client asked for. Used
    /// to decide whether the rewrite is worth logging.
    /// </summary>
    public static bool AuthorityDiffers(string advertisedUrl, string requestedUrl)
    {
        if (!Uri.TryCreate(advertisedUrl, UriKind.Absolute, out var advertised)) return false;
        if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out var requested)) return false;

        return !string.Equals(advertised.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
               || advertised.Port != requested.Port;
    }

    private static string FormatAuthority(string host, int port)
    {
        // IPv6 literals must stay bracketed.
        var formattedHost = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return port < 0 ? formattedHost : $"{formattedHost}:{port}";
    }
}
