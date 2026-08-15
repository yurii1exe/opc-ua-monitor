using OpcMonitor.Infrastructure;

namespace OpcMonitor.Tests;

public class ReconnectPolicyTests
{
    private static ReconnectOptions Options(double jitterFraction = 0.0) => new()
    {
        InitialDelayMs = 1000,
        MaxDelayMs = 30_000,
        BackoffFactor = 2.0,
        JitterFraction = jitterFraction
    };

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 16000)]
    public void BacksOffExponentiallyFromTheFirstAttempt(int attempt, double expectedMs)
    {
        var delay = ReconnectPolicy.DelayFor(Options(), attempt, unitRandom: 0.5);
        Assert.Equal(expectedMs, delay.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void StopsGrowingAtTheCap()
    {
        // A server that is down for an hour must not push the retry interval
        // into next week; the cap is what makes recovery prompt once it returns.
        var delay = ReconnectPolicy.DelayFor(Options(), attempt: 40, unitRandom: 0.5);
        Assert.Equal(30_000, delay.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void DoesNotOverflowOnAnAbsurdAttemptCount()
    {
        var delay = ReconnectPolicy.DelayFor(Options(), attempt: int.MaxValue, unitRandom: 0.99);
        Assert.InRange(delay.TotalMilliseconds, 0, 45_000);
    }

    [Fact]
    public void TreatsAttemptZeroAsTheFirstAttempt()
    {
        Assert.Equal(
            ReconnectPolicy.DelayFor(Options(), 1, 0.5),
            ReconnectPolicy.DelayFor(Options(), 0, 0.5));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999)]
    public void KeepsJitterWithinTheConfiguredFractionOfNominal(double unitRandom)
    {
        var delay = ReconnectPolicy.DelayFor(Options(jitterFraction: 0.2), attempt: 3, unitRandom).TotalMilliseconds;

        // Nominal at attempt 3 is 4000ms, so 20% jitter spans 3200..4800.
        Assert.InRange(delay, 3200, 4800);
    }

    [Fact]
    public void SpreadsRetriesSoClientsDoNotReturnInLockstep()
    {
        var options = Options(jitterFraction: 0.5);

        var delays = Enumerable.Range(0, 200)
            .Select(i => ReconnectPolicy.DelayFor(options, attempt: 4, unitRandom: i / 200.0).TotalMilliseconds)
            .ToList();

        Assert.True(delays.Distinct().Count() > 100,
            "Jitter should produce a spread of delays, not a handful of buckets.");
        Assert.True(delays.Max() - delays.Min() > 1000,
            "The spread should be wide enough to actually de-synchronise clients.");
    }

    [Fact]
    public void NeverReturnsANegativeDelay()
    {
        var options = Options(jitterFraction: 1.0);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            Assert.True(ReconnectPolicy.DelayFor(options, attempt, unitRandom: 0.0) >= TimeSpan.Zero);
        }
    }
}
