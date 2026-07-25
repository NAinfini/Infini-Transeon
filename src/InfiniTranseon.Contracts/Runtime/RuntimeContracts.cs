namespace InfiniTranseon.Contracts.Runtime;

public sealed record CaptureTargetId
{
    public CaptureTargetId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier cannot be empty.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
}

public sealed record TargetInstanceId
{
    public TargetInstanceId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier cannot be empty.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
}

public sealed record RegionId
{
    public RegionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier cannot be empty.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
}

public sealed record TextTrackId
{
    public TextTrackId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier cannot be empty.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
}

public sealed record SourceEventId
{
    public SourceEventId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier cannot be empty.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
}

public sealed record TranslationChannelId
{
    public TranslationChannelId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Identifier cannot be empty.", nameof(value));
        Value = value;
    }
    public Guid Value { get; }
}

public sealed record NormalizedRect
{
    public NormalizedRect(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || x < 0 || y < 0 || width <= 0 || height <= 0 ||
            x + width > 1 || y + height > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Rectangle must fit in normalized content coordinates.");
        }
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
}

public enum CaptureAreaKind
{
    UserRegion,
    FullTarget,
    RemainingArea,
}

public sealed record CaptureAreaKey
{
    public CaptureAreaKey(CaptureAreaKind kind, RegionId? userRegionId)
    {
        bool valid = kind == CaptureAreaKind.UserRegion
            ? userRegionId is not null
            : userRegionId is null;
        if (!valid) throw new ArgumentException("Region identity must match the capture area kind.", nameof(userRegionId));
        Kind = kind;
        UserRegionId = userRegionId;
    }

    public static CaptureAreaKey UserRegion(RegionId regionId) => new(CaptureAreaKind.UserRegion, regionId);
    public static CaptureAreaKey FullTarget { get; } = new(CaptureAreaKind.FullTarget, null);
    public static CaptureAreaKey RemainingArea { get; } = new(CaptureAreaKind.RemainingArea, null);
    public CaptureAreaKind Kind { get; }
    public RegionId? UserRegionId { get; }
}

public sealed record SourceGenerationToken
{
    public SourceGenerationToken(
        Guid runtimeEpoch,
        TargetInstanceId targetInstanceId,
        CaptureAreaKey area,
        TextTrackId textTrackId,
        long sourceGeneration,
        long profileRevision)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(textTrackId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        RuntimeEpoch = runtimeEpoch;
        TargetInstanceId = targetInstanceId;
        Area = area;
        TextTrackId = textTrackId;
        SourceGeneration = sourceGeneration;
        ProfileRevision = profileRevision;
    }

    public Guid RuntimeEpoch { get; }
    public TargetInstanceId TargetInstanceId { get; }
    public CaptureAreaKey Area { get; }
    public TextTrackId TextTrackId { get; }
    public long SourceGeneration { get; }
    public long ProfileRevision { get; }
}

public sealed record OcrExecutionToken
{
    public OcrExecutionToken(
        SourceGenerationToken source,
        Guid ocrRunId,
        int attempt,
        long resultSequence,
        bool isManual = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ocrRunId == Guid.Empty) throw new ArgumentException("OCR run cannot be empty.", nameof(ocrRunId));
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(resultSequence, 1);
        Source = source;
        OcrRunId = ocrRunId;
        Attempt = attempt;
        ResultSequence = resultSequence;
        IsManual = isManual;
    }

    public SourceGenerationToken Source { get; }
    public Guid OcrRunId { get; }
    public int Attempt { get; }
    public long ResultSequence { get; }
    public bool IsManual { get; }
}

public sealed record ChannelExecutionToken
{
    public ChannelExecutionToken(
        SourceGenerationToken source,
        TranslationChannelId channelId,
        Guid channelRunId,
        Guid immutableSlotId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(channelId);
        if (channelRunId == Guid.Empty) throw new ArgumentException("Channel run cannot be empty.", nameof(channelRunId));
        if (immutableSlotId == Guid.Empty) throw new ArgumentException("Slot cannot be empty.", nameof(immutableSlotId));
        Source = source;
        ChannelId = channelId;
        ChannelRunId = channelRunId;
        ImmutableSlotId = immutableSlotId;
    }

    public SourceGenerationToken Source { get; }
    public TranslationChannelId ChannelId { get; }
    public Guid ChannelRunId { get; }
    public Guid ImmutableSlotId { get; }
}

public sealed record StageExecutionToken
{
    public StageExecutionToken(
        ChannelExecutionToken channel,
        Guid stageId,
        int stageSequence,
        int attempt,
        long streamSequence)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (stageId == Guid.Empty) throw new ArgumentException("Stage cannot be empty.", nameof(stageId));
        ArgumentOutOfRangeException.ThrowIfLessThan(stageSequence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(streamSequence, 1);
        Channel = channel;
        StageId = stageId;
        StageSequence = stageSequence;
        Attempt = attempt;
        StreamSequence = streamSequence;
    }

    public ChannelExecutionToken Channel { get; }
    public Guid StageId { get; }
    public int StageSequence { get; }
    public int Attempt { get; }
    public long StreamSequence { get; }
}

public sealed record TextLine(string Text, NormalizedRect Bounds, double Confidence)
{
    public int OrientationDegrees { get; init; }
    public bool IsVertical { get; init; }
}

public sealed record TextGeneration(
    CaptureTargetId TargetId,
    SourceGenerationToken SourceToken,
    SourceEventId SourceEventId,
    NormalizedRect SourceBounds,
    string SourceText,
    IReadOnlyList<TextLine> Lines,
    DateTimeOffset CapturedAtUtc,
    long CapturedAtQpc,
    long FrameSequence);

public enum TranslationStage
{
    Initial,
    Fallback,
    Refinement,
}

public sealed record ContextPolicy(bool IncludeGame, bool IncludeScene, bool IncludeRecentHistory);
public sealed record CachePolicy(bool MemoryEnabled, bool PersistentEnabled, bool FuzzyEnabled);
public sealed record DisplaySlotDefinition(Guid SlotId, int Order, string Label);
public sealed record RefinementStepDefinition(Guid StageId, string ProviderId, string PromptTemplateId);

public sealed record TranslationChannelDefinition(
    TranslationChannelId Id,
    string InitialProviderId,
    IReadOnlyList<string> FallbackProviderIds,
    IReadOnlyList<RefinementStepDefinition> RefinementSteps,
    ContextPolicy Context,
    CachePolicy Cache,
    DisplaySlotDefinition DisplaySlot)
{
    public int RetryCount { get; init; } = 1;
}

public sealed record TranslationOutput(
    TranslationChannelId ChannelId,
    StageExecutionToken ExecutionToken,
    Guid ImmutableSlotId,
    Guid StageId,
    int StageIndex,
    int Attempt,
    TranslationStage Stage,
    string Text,
    string ProviderId,
    TimeSpan Latency,
    decimal? EstimatedCost,
    string? CostCurrency,
    bool CacheHit,
    bool StreamCompleted,
    string? FallbackFromProviderId,
    string? TerminalErrorCode,
    string? SupersededReason)
{
    public bool EstimateOnly { get; init; }
}
