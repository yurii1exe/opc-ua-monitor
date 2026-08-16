using Opc.Ua;
using OpcMonitor.Infrastructure;

namespace OpcMonitor.Tests;

/// <summary>
/// Covers the order in which the client tries the endpoints a server offers.
/// </summary>
/// <remarks>
/// The order matters twice over. It decides how securely the client connects
/// when everything works, and — because a handshake can fail on a policy the
/// server advertised — it decides what the client drops to when one does not.
/// Both are decisions about a list of <see cref="EndpointDescription"/>, so both
/// are testable without a server.
/// </remarks>
public class EndpointRankingTests
{
    private static EndpointDescription Endpoint(
        string url,
        MessageSecurityMode mode,
        byte securityLevel,
        string policy = "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256") =>
        new()
        {
            EndpointUrl = url,
            SecurityMode = mode,
            SecurityLevel = securityLevel,
            SecurityPolicyUri = mode == MessageSecurityMode.None
                ? "http://opcfoundation.org/UA/SecurityPolicy#None"
                : policy
        };

    private static readonly EndpointDescription Unsecured =
        Endpoint("opc.tcp://server:4840", MessageSecurityMode.None, 0);

    private static readonly EndpointDescription SignOnly =
        Endpoint("opc.tcp://server:4840", MessageSecurityMode.Sign, 20);

    private static readonly EndpointDescription SignAndEncrypt =
        Endpoint("opc.tcp://server:4840", MessageSecurityMode.SignAndEncrypt, 60);

    [Fact]
    public void OrdersSecureEndpointsByTheServersOwnSecurityLevel()
    {
        var ranked = OpcSessionFactory.RankEndpoints(
            [Unsecured, SignOnly, SignAndEncrypt],
            useSecurity: true,
            allowNoSecurityFallback: true);

        Assert.Equal(
            [MessageSecurityMode.SignAndEncrypt, MessageSecurityMode.Sign, MessageSecurityMode.None],
            ranked.Select(e => e.SecurityMode));
    }

    [Fact]
    public void PutsTheUnsecuredEndpointLastSoItIsOnlyReachedWhenEveryPolicyFailed()
    {
        var ranked = OpcSessionFactory.RankEndpoints(
            [Unsecured, SignAndEncrypt],
            useSecurity: true,
            allowNoSecurityFallback: true);

        Assert.Equal(MessageSecurityMode.None, ranked[^1].SecurityMode);
    }

    [Fact]
    public void OmitsTheUnsecuredEndpointEntirelyWhenTheFallbackIsDisabled()
    {
        var ranked = OpcSessionFactory.RankEndpoints(
            [Unsecured, SignAndEncrypt],
            useSecurity: true,
            allowNoSecurityFallback: false);

        Assert.DoesNotContain(ranked, e => e.SecurityMode == MessageSecurityMode.None);
    }

    [Fact]
    public void ReturnsNothingWhenSecurityIsRequiredAndTheServerOffersNone()
    {
        var ranked = OpcSessionFactory.RankEndpoints(
            [Unsecured],
            useSecurity: true,
            allowNoSecurityFallback: false);

        Assert.Empty(ranked);
    }

    [Fact]
    public void PrefersTheUnsecuredEndpointWhenSecurityIsTurnedOff()
    {
        var ranked = OpcSessionFactory.RankEndpoints(
            [SignAndEncrypt, Unsecured],
            useSecurity: false,
            allowNoSecurityFallback: true);

        Assert.Equal(MessageSecurityMode.None, ranked[0].SecurityMode);
    }

    [Fact]
    public void StillOffersSecureEndpointsWhenSecurityIsOffAndTheServerHasNoUnsecuredOne()
    {
        var ranked = OpcSessionFactory.RankEndpoints(
            [SignAndEncrypt],
            useSecurity: false,
            allowNoSecurityFallback: true);

        Assert.Equal(MessageSecurityMode.SignAndEncrypt, Assert.Single(ranked).SecurityMode);
    }

    [Fact]
    public void KeepsOnlyBinaryEndpointsWhenTheServerAlsoOffersHttps()
    {
        var https = Endpoint("https://server:443/UA", MessageSecurityMode.SignAndEncrypt, 80);

        var ranked = OpcSessionFactory.RankEndpoints(
            [https, SignAndEncrypt],
            useSecurity: true,
            allowNoSecurityFallback: true);

        Assert.All(ranked, e => Assert.StartsWith("opc.tcp://", e.EndpointUrl));
    }

    [Fact]
    public void FallsBackToWhateverTransportIsOfferedWhenNoneIsBinary()
    {
        var https = Endpoint("https://server:443/UA", MessageSecurityMode.SignAndEncrypt, 80);

        var ranked = OpcSessionFactory.RankEndpoints(
            [https],
            useSecurity: true,
            allowNoSecurityFallback: true);

        Assert.Same(https, Assert.Single(ranked));
    }

    [Fact]
    public void SkipsEndpointsWithNoUrlToConnectTo()
    {
        var blank = Endpoint(string.Empty, MessageSecurityMode.SignAndEncrypt, 90);

        var ranked = OpcSessionFactory.RankEndpoints(
            [blank, SignAndEncrypt],
            useSecurity: true,
            allowNoSecurityFallback: true);

        Assert.Same(SignAndEncrypt, Assert.Single(ranked));
    }
}
