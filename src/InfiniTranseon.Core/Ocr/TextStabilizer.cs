namespace InfiniTranseon.Core.Ocr;

public sealed record TextStabilizerOptions(
    int StableFrameCount,
    TimeSpan MinimumDelay,
    TimeSpan MaximumWait)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(StableFrameCount, 1);
        if (MinimumDelay < TimeSpan.Zero || MaximumWait <= TimeSpan.Zero || MaximumWait < MinimumDelay)
            throw new ArgumentOutOfRangeException(nameof(MaximumWait));
    }
}

public sealed record StabilizedText(string Text, bool IsStable, bool ForcedProgress, long Generation);

public sealed class TextStabilizer
{
    private readonly object _gate = new();
    private readonly TextStabilizerOptions _options;
    private string _pending = string.Empty;
    private string _stable = string.Empty;
    private int _consecutiveFrames;
    private DateTimeOffset _pendingSince;
    private DateTimeOffset _sequenceStarted;
    private long _generation;

    public TextStabilizer(TextStabilizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public StabilizedText Observe(string text, DateTimeOffset observedAt, long generation)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        string normalized = TextNormalizer.Normalize(text);
        lock (_gate)
        {
            if (_generation != generation)
            {
                _generation = generation;
                _pending = normalized;
                _stable = string.Empty;
                _consecutiveFrames = 1;
                _pendingSince = observedAt;
                _sequenceStarted = observedAt;
                return new StabilizedText(normalized, false, false, generation);
            }

            if (_pending == normalized)
            {
                _consecutiveFrames++;
            }
            else
            {
                bool hadStableText = _stable.Length > 0;
                _pending = normalized;
                _consecutiveFrames = 1;
                _pendingSince = observedAt;
                if (hadStableText) _sequenceStarted = observedAt;
            }

            if (_stable == _pending && _stable.Length > 0)
                return new StabilizedText(_stable, true, false, generation);

            bool normallyStable = _consecutiveFrames >= _options.StableFrameCount &&
                observedAt - _pendingSince >= _options.MinimumDelay;
            bool forced = observedAt - _sequenceStarted >= _options.MaximumWait;
            if (normallyStable || forced)
            {
                _stable = _pending;
                return new StabilizedText(_stable, true, forced && !normallyStable, generation);
            }
            return new StabilizedText(_pending, false, false, generation);
        }
    }
}
