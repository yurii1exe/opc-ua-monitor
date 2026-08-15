using OpcMonitor.Infrastructure;

namespace OpcMonitor.Tests;

/// <summary>
/// The subscription lifetime is one of the classic ways an OPC UA client appears
/// to work and then silently stops delivering, so the arithmetic is pinned down.
/// </summary>
public class SubscriptionLifetimeTests
{
    [Fact]
    public void RaisesLifetimeToCoverTheSessionTimeout()
    {
        var options = new OpcSubscriptionOptions
        {
            PublishingIntervalMs = 1000,
            KeepAliveCount = 10,
            LifetimeCount = 30
        };

        // 30 cycles x 1000ms = 30s, shorter than the 60s session. A hiccup would
        // drop the subscription while the session it belongs to is still alive.
        var effective = OpcSubscriptionService.ResolveLifetimeCount(options, sessionTimeoutMs: 60_000);

        Assert.Equal(60u, effective);
    }

    [Fact]
    public void EnforcesTheSpecMinimumOfThreeKeepAliveIntervals()
    {
        var options = new OpcSubscriptionOptions
        {
            PublishingIntervalMs = 5000,
            KeepAliveCount = 20,
            LifetimeCount = 25
        };

        // Part 4 §5.13.2: lifetime must be at least 3 x keep-alive count.
        var effective = OpcSubscriptionService.ResolveLifetimeCount(options, sessionTimeoutMs: 10_000);

        Assert.Equal(60u, effective);
    }

    [Fact]
    public void LeavesAGenerousConfiguredValueAlone()
    {
        var options = new OpcSubscriptionOptions
        {
            PublishingIntervalMs = 1000,
            KeepAliveCount = 10,
            LifetimeCount = 500
        };

        Assert.Equal(500u, OpcSubscriptionService.ResolveLifetimeCount(options, sessionTimeoutMs: 60_000));
    }

    [Fact]
    public void HandlesAZeroPublishingIntervalWithoutDividingByZero()
    {
        var options = new OpcSubscriptionOptions
        {
            PublishingIntervalMs = 0,
            KeepAliveCount = 10,
            LifetimeCount = 30
        };

        var effective = OpcSubscriptionService.ResolveLifetimeCount(options, sessionTimeoutMs: 60_000);

        Assert.True(effective >= 30);
    }
}
