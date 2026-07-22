namespace InfiniTranseon.Core.Translation;

public sealed record ProviderRetryOptions(
    int MaximumAttempts = 2,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaximumDelay = null,
    double JitterRatio = 0.20)
{
    public TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromMilliseconds(250);
    public TimeSpan EffectiveMaximumDelay => MaximumDelay ?? TimeSpan.FromSeconds(4);
}

public interface IProviderRetryPolicy
{
    int MaximumAttempts { get; }
    ValueTask WaitBeforeRetryAsync(int failedAttempt, CancellationToken cancellationToken);
}

public sealed class ExponentialJitterRetryPolicy : IProviderRetryPolicy
{
    private readonly ProviderRetryOptions _options;

    public ExponentialJitterRetryPolicy(ProviderRetryOptions? options = null)
    {
        _options = options ?? new ProviderRetryOptions();
        Validate(_options);
    }

    public int MaximumAttempts => _options.MaximumAttempts;

    public ValueTask WaitBeforeRetryAsync(int failedAttempt, CancellationToken cancellationToken)
    {
        TimeSpan delay = ComputeDelay(_options, failedAttempt, Random.Shared.NextDouble());
        return new ValueTask(Task.Delay(delay, cancellationToken));
    }

    public static TimeSpan ComputeDelay(
        ProviderRetryOptions options,
        int failedAttempt,
        double jitterSample)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);
        if (jitterSample is < 0 or > 1 || double.IsNaN(jitterSample))
            throw new ArgumentOutOfRangeException(nameof(jitterSample));

        double exponent = Math.Min(failedAttempt - 1, 30);
        double unboundedTicks = options.EffectiveBaseDelay.Ticks * Math.Pow(2, exponent);
        double boundedTicks = Math.Min(unboundedTicks, options.EffectiveMaximumDelay.Ticks);
        double jitterMultiplier = 1 + ((jitterSample * 2) - 1) * options.JitterRatio;
        long ticks = checked((long)Math.Min(
            boundedTicks * jitterMultiplier,
            options.EffectiveMaximumDelay.Ticks));
        return TimeSpan.FromTicks(ticks);
    }

    private static void Validate(ProviderRetryOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumAttempts, 8);
        if (options.EffectiveBaseDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Base delay must be positive.");
        if (options.EffectiveMaximumDelay < options.EffectiveBaseDelay)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum delay cannot be below base delay.");
        if (options.JitterRatio is < 0 or > 1 || double.IsNaN(options.JitterRatio))
            throw new ArgumentOutOfRangeException(nameof(options), "Jitter ratio must be between zero and one.");
    }
}
