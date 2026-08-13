namespace EConomic.Http;

/// <summary>
/// Controls how failed requests are retried.
/// </summary>
public sealed class EconomicRetryOptions
{
    /// <summary>
    /// Total attempts, including the first. Defaults to 3, so two retries.
    /// </summary>
    /// <remarks>
    /// e-conomic charges tokens per call, so retries spend budget. Raising this makes a throttled
    /// client throttle itself harder.
    /// </remarks>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry, doubled for each subsequent one. Defaults to one second.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on any single delay, before jitter. Defaults to thirty seconds.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to honour a <c>Retry-After</c> header in preference to the computed backoff.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool RespectRetryAfter { get; set; } = true;

    /// <summary>Throws if the options are not usable.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new InvalidOperationException($"{nameof(MaxAttempts)} must be at least 1.");
        }

        if (BaseDelay < TimeSpan.Zero || MaxDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Retry delays cannot be negative.");
        }

        if (MaxDelay < BaseDelay)
        {
            throw new InvalidOperationException($"{nameof(MaxDelay)} cannot be shorter than {nameof(BaseDelay)}.");
        }
    }
}
