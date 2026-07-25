using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Ocr;

public sealed class OcrTextGenerationException : Exception
{
    public OcrTextGenerationException(string code)
        : base($"OCR text generation failed: {code}.") => Code = code;

    public string Code { get; }
}

public sealed class OcrTextGenerationGate
{
    private sealed class TrackState
    {
        public string PendingText { get; set; } = string.Empty;
        public string EmittedText { get; set; } = string.Empty;
        public int ConsecutiveFrames { get; set; }
        public DateTimeOffset PendingSince { get; set; }
        public DateTimeOffset SequenceStarted { get; set; }
    }

    private readonly record struct TrackKey(Guid TargetInstanceId, Guid TextTrackId);

    private readonly object _gate = new();
    private readonly TextStabilizerOptions _options;
    private readonly LineLayoutProcessor _layout = new();
    private readonly Dictionary<TrackKey, TrackState> _tracks = [];

    public OcrTextGenerationGate(TextStabilizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public bool TryCreate(
        OcrResultSnapshot result,
        CaptureTargetId targetId,
        ProfileRegion region,
        DateTimeOffset observedAtUtc,
        long capturedAtQpc,
        long frameSequence,
        out TextGeneration? generation) =>
        TryCreate(
            result,
            targetId,
            region,
            observedAtUtc,
            capturedAtQpc,
            frameSequence,
            out generation,
            out _);

    public bool TryCreate(
        OcrResultSnapshot result,
        CaptureTargetId targetId,
        ProfileRegion region,
        DateTimeOffset observedAtUtc,
        long capturedAtQpc,
        long frameSequence,
        out TextGeneration? generation,
        out bool cleared)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(region);
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Observation time must use UTC.", nameof(observedAtUtc));
        if (capturedAtQpc < 0) throw new ArgumentOutOfRangeException(nameof(capturedAtQpc));
        ArgumentOutOfRangeException.ThrowIfLessThan(frameSequence, 1);
        if (result.TerminalErrorCode is not null)
            throw new OcrTextGenerationException(result.TerminalErrorCode);

        SourceGenerationToken source = result.ExecutionToken.Source;
        ValidateArea(source.Area, region);
        var key = new TrackKey(
            source.TargetInstanceId.Value,
            source.TextTrackId.Value);
        ProcessedOcrText processed = _layout.Process(
            result.Lines,
            LineBreakPolicy.FromProfile(region));
        if (processed.NormalizedText.Length == 0 || processed.Lines.Count == 0)
        {
            lock (_gate) _tracks.Remove(key);
            generation = null;
            cleared = true;
            return false;
        }

        cleared = false;
        lock (_gate)
        {
            if (!_tracks.TryGetValue(key, out TrackState? state))
            {
                state = new TrackState
                {
                    PendingText = processed.NormalizedText,
                    ConsecutiveFrames = 1,
                    PendingSince = observedAtUtc,
                    SequenceStarted = observedAtUtc,
                };
                _tracks.Add(key, state);
            }
            else if (string.Equals(
                state.PendingText, processed.NormalizedText, StringComparison.Ordinal))
            {
                state.ConsecutiveFrames = checked(state.ConsecutiveFrames + 1);
            }
            else
            {
                if (state.EmittedText.Length > 0) state.SequenceStarted = observedAtUtc;
                state.PendingText = processed.NormalizedText;
                state.ConsecutiveFrames = 1;
                state.PendingSince = observedAtUtc;
            }

            bool stable = state.ConsecutiveFrames >= _options.StableFrameCount &&
                observedAtUtc - state.PendingSince >= _options.MinimumDelay;
            bool forced = observedAtUtc - state.SequenceStarted >= _options.MaximumWait;
            bool explicitlyRequested = result.ExecutionToken.IsManual;
            if ((!stable && !forced && !explicitlyRequested) ||
                (!explicitlyRequested && string.Equals(
                    state.EmittedText, processed.NormalizedText, StringComparison.Ordinal)))
            {
                generation = null;
                return false;
            }

            TextLine[] mappedLines = processed.Lines
                .Select(line => new TextLine(
                    line.NormalizedText,
                    MapToTarget(line.Bounds, region.Bounds),
                    line.Confidence))
                .ToArray();
            NormalizedRect bounds = Union(mappedLines.Select(line => line.Bounds));
            state.EmittedText = processed.NormalizedText;
            generation = new TextGeneration(
                targetId,
                source,
                new SourceEventId(Guid.NewGuid()),
                bounds,
                processed.NormalizedText,
                mappedLines,
                observedAtUtc,
                capturedAtQpc,
                frameSequence);
            return true;
        }
    }

    public void Clear(TargetInstanceId target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            foreach (TrackKey key in _tracks.Keys
                         .Where(key => key.TargetInstanceId == target.Value)
                         .ToArray())
                _tracks.Remove(key);
        }
    }

    private static void ValidateArea(CaptureAreaKey area, ProfileRegion region)
    {
        bool valid = area.Kind == region.AreaMode &&
            (area.Kind != CaptureAreaKind.UserRegion ||
                area.UserRegionId?.Value == region.RegionId);
        if (!valid)
            throw new ArgumentException("OCR source area does not match the profile region.", nameof(region));
    }

    private static NormalizedRect MapToTarget(NormalizedRect crop, NormalizedRect region) => new(
        region.X + crop.X * region.Width,
        region.Y + crop.Y * region.Height,
        crop.Width * region.Width,
        crop.Height * region.Height);

    private static NormalizedRect Union(IEnumerable<NormalizedRect> values)
    {
        NormalizedRect[] rectangles = values.ToArray();
        if (rectangles.Length == 0)
            throw new ArgumentException("At least one OCR line is required.", nameof(values));
        double left = rectangles.Min(item => item.X);
        double top = rectangles.Min(item => item.Y);
        double right = rectangles.Max(item => item.X + item.Width);
        double bottom = rectangles.Max(item => item.Y + item.Height);
        return new NormalizedRect(left, top, right - left, bottom - top);
    }
}
