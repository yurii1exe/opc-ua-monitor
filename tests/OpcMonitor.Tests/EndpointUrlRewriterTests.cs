using OpcMonitor.Infrastructure;

namespace OpcMonitor.Tests;

/// <summary>
/// Covers the endpoint-URL policy that makes the client work inside containers.
/// </summary>
/// <remarks>
/// These cases are the reason the rewrite is a pure function. The bug it
/// prevents only reproduces with a real server that advertises a hostname the
/// client cannot resolve, which is not something a unit test can stand up — but
/// the decision the client makes about that hostname is exactly what needs to be
/// pinned down, and that part is testable in microseconds.
/// </remarks>
public class EndpointUrlRewriterTests
{
    [Theory]
    // The Compose case: the server advertises its container hostname, the client
    // reached it on a published port from the host.
    [InlineData("opc.tcp://4f3c1a2b9d7e:62541", "opc.tcp://localhost:62541", "opc.tcp://localhost:62541")]
    // The reverse: reached by service name inside the network, advertising the
    // container id.
    [InlineData("opc.tcp://4f3c1a2b9d7e:62541", "opc.tcp://simulator:62541", "opc.tcp://simulator:62541")]
    // NAT: the server advertises a private address unreachable from the client.
    [InlineData("opc.tcp://192.0.2.10:4840", "opc.tcp://plc.example.test:4840", "opc.tcp://plc.example.test:4840")]
    // Port mapping: published on a different host port than the server believes.
    [InlineData("opc.tcp://simulator:62541", "opc.tcp://localhost:52541", "opc.tcp://localhost:52541")]
    public void ReplacesAdvertisedAuthorityWithRequestedOne(string advertised, string requested, string expected)
    {
        Assert.Equal(expected, EndpointUrlRewriter.PreserveRequestedHost(advertised, requested));
    }

    [Fact]
    public void PreservesThePathBecauseServersExposeSeveralEndpointsUnderOne()
    {
        var result = EndpointUrlRewriter.PreserveRequestedHost(
            "opc.tcp://server-container:62541/UA/SampleServer",
            "opc.tcp://localhost:62541");

        Assert.Equal("opc.tcp://localhost:62541/UA/SampleServer", result);
    }

    [Fact]
    public void LeavesTheUrlAloneWhenTheServerAdvertisesWhatWasAsked()
    {
        const string url = "opc.tcp://localhost:62541";

        Assert.Equal(url, EndpointUrlRewriter.PreserveRequestedHost(url, url));
        Assert.False(EndpointUrlRewriter.AuthorityDiffers(url, url));
    }

    [Fact]
    public void TreatsHostCaseAsInsignificant()
    {
        Assert.False(EndpointUrlRewriter.AuthorityDiffers(
            "opc.tcp://Simulator:62541", "opc.tcp://simulator:62541"));
    }

    [Fact]
    public void KeepsIpv6LiteralsBracketed()
    {
        var result = EndpointUrlRewriter.PreserveRequestedHost(
            "opc.tcp://simulator:62541",
            "opc.tcp://[2001:db8::1]:62541");

        Assert.Equal("opc.tcp://[2001:db8::1]:62541", result);
    }

    [Theory]
    [InlineData("not a url", "opc.tcp://localhost:62541")]
    [InlineData("opc.tcp://localhost:62541", "")]
    [InlineData("", "opc.tcp://localhost:62541")]
    public void ReturnsTheAdvertisedUrlUnchangedRatherThanThrowing(string advertised, string requested)
    {
        // A malformed URL from the server is the server's problem. Surfacing it
        // as a connection error keeps the original text visible in the log;
        // throwing here would replace it with a stack trace from string parsing.
        Assert.Equal(advertised, EndpointUrlRewriter.PreserveRequestedHost(advertised, requested));
    }

    [Fact]
    public void DetectsAPortOnlyDifference()
    {
        Assert.True(EndpointUrlRewriter.AuthorityDiffers(
            "opc.tcp://localhost:62541", "opc.tcp://localhost:52541"));
    }
}
