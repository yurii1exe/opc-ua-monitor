namespace OpcMonitor.Infrastructure;

/// <summary>
/// Exponential backoff with symmetric jitter, expressed as a pure function so
/// it can be tested without waiting for real time to pass.
/// </summary>
public static class ReconnectPolicy
{
    /// <summary>
    /// Delay before reconnect attempt number <paramref name="attempt"/>
    /// (1-based).
    /// </summary>
    /// <param name="options">Backoff configuration.</param>
    /// <param name="attempt">1 for the first retry after a loss.</param>
    /// <param name="unitRandom">
    /// A value in [0,1). Passed in rather than sampled internally so the policy
    /// stays deterministic under test.
    /// </param>
    /// <remarks>
    /// The jitter is symmetric around the nominal delay — plus or minus
    /// <see cref="ReconnectOptions.JitterFraction"/> of it — rather than the
    /// "sleep a random amount up to the cap" variant. Symmetric jitter keeps the
    /// expected delay equal to the nominal backoff, so the schedule stays
    /// predictable while still preventing a set of clients that lost the same
    /// server from retrying in lockstep and re-flooding it the moment it comes
    /// back.
    /// </remarks>
    public static TimeSpan DelayFor(ReconnectOptions options, int attempt, double unitRandom)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (attempt < 1) attempt = 1;

        var exponent = Math.Min(attempt - 1, 32);
        var nominal = options.InitialDelayMs * Math.Pow(options.BackoffFactor, exponent);

        // Clamp before jitter so the cap bounds the schedule and jitter only
        // spreads clients around it.
        nominal = Math.Min(nominal, options.MaxDelayMs);

        var jitter = nominal * options.JitterFraction * (2.0 * unitRandom - 1.0);
        var delay = Math.Max(0, nominal + jitter);

        return TimeSpan.FromMilliseconds(delay);
    }

    /// <summary>Convenience overload sampling from a supplied RNG.</summary>
    public static TimeSpan DelayFor(ReconnectOptions options, int attempt, Random random) =>
        DelayFor(options, attempt, (random ?? Random.Shared).NextDouble());
}
