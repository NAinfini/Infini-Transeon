namespace InfiniTranseon.Contracts.Runtime;

public enum RuntimeContractError
{
    StreamIdentityChanged,
    StreamSequenceOutOfOrder,
    PolicyAcknowledgementOutOfOrder,
    PolicyAcknowledgementNotSent,
    BudgetSnapshotOutOfOrder,
}

public sealed class RuntimeContractException : Exception
{
    public RuntimeContractException(RuntimeContractError error)
        : base($"Runtime contract validation failed: {error}.") => Error = error;

    public RuntimeContractError Error { get; }
}

public enum TargetLifecycleState
{
    Available,
    Running,
    Minimized,
    OccludedOrUnsupported,
    Resized,
    DpiChanged,
    AdapterChanged,
    DeviceLost,
    Closed,
    WaitingForMatch,
    RunningWithCaptureBorder,
}

public sealed record TargetSnapshot
{
    public TargetSnapshot(
        TargetInstanceId targetInstanceId,
        CaptureTargetId targetId,
        TargetLifecycleState state,
        int pixelWidth,
        int pixelHeight,
        int dpi)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        TargetInstanceId = targetInstanceId;
        TargetId = targetId;
        State = state;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Dpi = dpi;
    }

    public TargetInstanceId TargetInstanceId { get; }
    public CaptureTargetId TargetId { get; }
    public TargetLifecycleState State { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int Dpi { get; }
}

public sealed record TargetLifecycleEvent(
    TargetSnapshot Target,
    long LifecycleSequence,
    DateTimeOffset OccurredAtUtc,
    string? ErrorCode)
{
    public int? NativeErrorCode { get; init; }
}

public sealed record OcrResultSnapshot(
    OcrExecutionToken ExecutionToken,
    IReadOnlyList<TextLine> Lines,
    string ModelId,
    string ModelVersion,
    bool IsStable,
    string? TerminalErrorCode);

public sealed record CloudOcrCropRequest : IDisposable
{
    private readonly byte[] _encodedCrop;
    private bool _disposed;

    public CloudOcrCropRequest(
        OcrExecutionToken executionToken,
        string mimeType,
        ReadOnlySpan<byte> encodedCrop,
        int pixelWidth,
        int pixelHeight,
        bool explicitCloudConsent,
        long consentPolicyRevision = 1,
        int? encodedByteCeiling = null,
        DateTimeOffset? deadlineUtc = null,
        string providerId = "auto")
    {
        ArgumentNullException.ThrowIfNull(executionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(consentPolicyRevision, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (providerId.Length > 128 || providerId.Any(char.IsControl))
            throw new ArgumentException("Cloud OCR provider identifier is invalid.", nameof(providerId));
        if (!explicitCloudConsent)
        {
            throw new ArgumentException("Cloud OCR requires explicit profile consent.", nameof(explicitCloudConsent));
        }
        int byteCeiling = encodedByteCeiling ?? RuntimeProtocol.MaxPayloadBytes;
        if (byteCeiling is < 1 or > RuntimeProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(encodedByteCeiling));
        if (encodedCrop.IsEmpty || encodedCrop.Length > byteCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(encodedCrop));
        }

        ExecutionToken = executionToken;
        MimeType = mimeType;
        _encodedCrop = encodedCrop.ToArray();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        ExplicitCloudConsent = true;
        ConsentPolicyRevision = consentPolicyRevision;
        EncodedByteCeiling = byteCeiling;
        DeadlineUtc = deadlineUtc ?? DateTimeOffset.UtcNow.AddSeconds(30);
        if (DeadlineUtc.Offset != TimeSpan.Zero || DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(deadlineUtc));
        ProviderId = providerId;
    }

    public OcrExecutionToken ExecutionToken { get; }
    public string MimeType { get; }
    public ReadOnlyMemory<byte> EncodedCrop
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _encodedCrop;
        }
    }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public bool ExplicitCloudConsent { get; }
    public long ConsentPolicyRevision { get; }
    public int EncodedByteCeiling { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public string ProviderId { get; }

    public void Dispose()
    {
        if (_disposed) return;
        Array.Clear(_encodedCrop);
        _disposed = true;
    }
}

public enum TranslationStreamState
{
    Waiting,
    Streaming,
    Succeeded,
    TimedOut,
    Failed,
    Cancelled,
    Superseded,
}

public sealed record TranslationStreamSnapshot(
    StageExecutionToken ExecutionToken,
    string CumulativeText,
    TranslationStreamState State,
    string ProviderId,
    string? TerminalErrorCode);

public sealed class RuntimeStreamSequenceGate
{
    private Guid _channelRunId;
    private Guid _stageId;
    private int _attempt;
    private long _sequence;

    public void Accept(StageExecutionToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (_sequence == 0)
        {
            _channelRunId = token.Channel.ChannelRunId;
            _stageId = token.StageId;
            _attempt = token.Attempt;
        }
        else if (_channelRunId != token.Channel.ChannelRunId ||
            _stageId != token.StageId || _attempt != token.Attempt)
        {
            throw new RuntimeContractException(RuntimeContractError.StreamIdentityChanged);
        }

        if (token.StreamSequence != _sequence + 1)
        {
            throw new RuntimeContractException(RuntimeContractError.StreamSequenceOutOfOrder);
        }

        _sequence = token.StreamSequence;
    }
}

public sealed record PolicyRevision(
    long Revision,
    Guid ProfileId,
    long ProfileRevision,
    IReadOnlyDictionary<RegionId, string> RegionPolicies);

public sealed record PolicyAcknowledgement(long Revision, bool Accepted, string? RejectionCode);

public sealed class RuntimePolicyAcknowledgementGate
{
    private readonly object _gate = new();
    private readonly HashSet<long> _sent = [];
    private long _lastAcknowledged;
    private string? _lastRejectionCode;

    public long LastAcknowledged { get { lock (_gate) return _lastAcknowledged; } }
    public string? LastRejectionCode { get { lock (_gate) return _lastRejectionCode; } }

    public void RecordSent(long revision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        lock (_gate) _sent.Add(revision);
    }

    public void Accept(PolicyAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.Accepted
                ? !string.IsNullOrWhiteSpace(acknowledgement.RejectionCode)
                : string.IsNullOrWhiteSpace(acknowledgement.RejectionCode))
            throw new ArgumentException("Accepted acknowledgements cannot have a rejection code, and rejected acknowledgements require one.", nameof(acknowledgement));
        lock (_gate)
        {
            if (!_sent.Contains(acknowledgement.Revision))
                throw new RuntimeContractException(RuntimeContractError.PolicyAcknowledgementNotSent);
            if (acknowledgement.Revision <= _lastAcknowledged)
                throw new RuntimeContractException(RuntimeContractError.PolicyAcknowledgementOutOfOrder);
            _lastAcknowledged = acknowledgement.Revision;
            _lastRejectionCode = acknowledgement.Accepted ? null : acknowledgement.RejectionCode;
            _sent.RemoveWhere(revision => revision <= _lastAcknowledged);
        }
    }
}

public enum DegradationLifecycle
{
    Started,
    Changed,
    Recovered,
}

public sealed record DegradationSnapshot(
    long PolicyRevision,
    DegradationLifecycle Lifecycle,
    string CauseCode,
    IReadOnlyDictionary<string, string> Before,
    IReadOnlyDictionary<string, string> After,
    string ImpactMessageKey,
    string RecoveryConditionKey);

public enum RuntimeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record RuntimeDiagnosticEvent(
    string ErrorCode,
    string MessageKey,
    IReadOnlyDictionary<string, string> Arguments,
    RuntimeDiagnosticSeverity Severity,
    DateTimeOffset OccurredAtUtc);

public sealed record RuntimeThumbnail(
    TargetInstanceId TargetInstanceId,
    long FrameSequence,
    string MimeType,
    ReadOnlyMemory<byte> EncodedImage,
    int PixelWidth,
    int PixelHeight);

public enum OverlaySlotState
{
    Waiting,
    Streaming,
    Success,
    Fallback,
    Timeout,
    Failure,
    Cancelled,
}

public sealed record OverlaySlotSnapshot(
    Guid SlotId,
    int Order,
    OverlaySlotState State,
    string Text,
    string Label)
{
    public int StageIndex { get; init; }
}

public enum OverlayBackgroundTreatment
{
    Replacement,
    Translucent,
    Offset,
    FloatingPanel,
    Opaque,
    TemporalCache,
    AutomaticContrast,
    NoCover,
}

public enum OverlayTextAlignment
{
    Left,
    Center,
    Right,
}

public sealed record OverlayPixelRect(int X, int Y, int Width, int Height);

public sealed record OverlayRegionStyleSnapshot(
    OverlayBackgroundTreatment Background,
    OverlayTextAlignment Alignment,
    string BackgroundColor,
    string TextColor,
    double Opacity,
    double BlurRadius,
    double Padding,
    double PreferredFontSize,
    double MinimumFontSize,
    int MaximumLines)
{
    public OverlayPixelRect? DestinationBounds { get; init; }
    public string OutlineColor { get; init; } = "#FF000000";
    public double OutlineWidth { get; init; } = 1;
    public int MinimumDwellMilliseconds { get; init; } = 500;
    public int CrossfadeMilliseconds { get; init; } = 120;
    public bool ReducedMotion { get; init; }
}

public sealed record OverlayRegionSnapshot
{
    public OverlayRegionSnapshot(
        Guid regionId,
        OverlayPixelRect bounds,
        OverlayRegionStyleSnapshot style,
        IEnumerable<OverlaySlotSnapshot> orderedSlots)
    {
        if (regionId == Guid.Empty) throw new ArgumentException("Region ID cannot be empty.", nameof(regionId));
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(orderedSlots);
        bool destinationRequired = style.Background is
            OverlayBackgroundTreatment.Offset or OverlayBackgroundTreatment.FloatingPanel;
        bool destinationValid = destinationRequired
            ? style.DestinationBounds is { Width: >= 1 and <= 16_384, Height: >= 1 and <= 16_384 }
            : style.DestinationBounds is null;
        if (bounds.Width is < 1 or > 16_384 || bounds.Height is < 1 or > 16_384 ||
            !destinationValid ||
            style.Opacity is < 0 or > 1 ||
            style.BlurRadius is < 0 or > 64 || style.Padding < 0 || style.PreferredFontSize <= 0 ||
            style.MinimumFontSize <= 0 || style.PreferredFontSize < style.MinimumFontSize ||
            style.MaximumLines < 1 || !double.IsFinite(style.OutlineWidth) ||
            style.OutlineWidth is < 0 or > 8 || !Enum.IsDefined(style.Background) ||
            style.MinimumDwellMilliseconds is < 0 or > 3_000 ||
            style.CrossfadeMilliseconds is < 0 or > 500 ||
            !Enum.IsDefined(style.Alignment) || !IsArgbColor(style.BackgroundColor) ||
            !IsArgbColor(style.TextColor) || !IsArgbColor(style.OutlineColor))
            throw new ArgumentException("Overlay region geometry or style is invalid.");
        OverlaySlotSnapshot[] slots = orderedSlots.OrderBy(slot => slot.Order).ToArray();
        if (slots.Length > RuntimeCapabilities.VersionOne.MaxTranslationChannelsPerRegion ||
            slots.Any(slot => slot.SlotId == Guid.Empty || slot.Order < 0 || slot.StageIndex < 0) ||
            slots.Select(slot => slot.SlotId).Distinct().Count() != slots.Length ||
            slots.Select(slot => slot.Order).Distinct().Count() != slots.Length)
            throw new ArgumentException("Overlay slots must be bounded and have unique identities and order.", nameof(orderedSlots));
        RegionId = regionId;
        Bounds = bounds;
        Style = style;
        OrderedSlots = Array.AsReadOnly(slots);
    }

    private static bool IsArgbColor(string? value) =>
        value is { Length: 9 } && value[0] == '#' &&
        value.AsSpan(1).ToString().All(char.IsAsciiHexDigit);

    public Guid RegionId { get; }
    public OverlayPixelRect Bounds { get; }
    public OverlayRegionStyleSnapshot Style { get; }
    public IReadOnlyList<OverlaySlotSnapshot> OrderedSlots { get; }
}

public sealed record OverlayDesiredState
{
    public OverlayDesiredState(
        Guid runtimeEpoch,
        TargetInstanceId targetInstanceId,
        long overlayRevision,
        IEnumerable<OverlayRegionSnapshot> regions)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(overlayRevision, 1);
        ArgumentNullException.ThrowIfNull(regions);
        OverlayRegionSnapshot[] regionArray = regions.ToArray();
        if (regionArray.Length > RuntimeCapabilities.VersionOne.MaxRegionsPerTarget ||
            regionArray.Select(region => region.RegionId).Distinct().Count() != regionArray.Length)
            throw new ArgumentException("Overlay regions must be bounded and uniquely identified.", nameof(regions));

        RuntimeEpoch = runtimeEpoch;
        TargetInstanceId = targetInstanceId;
        OverlayRevision = overlayRevision;
        Regions = Array.AsReadOnly(regionArray);
    }

    public Guid RuntimeEpoch { get; }
    public TargetInstanceId TargetInstanceId { get; }
    public long OverlayRevision { get; }
    public IReadOnlyList<OverlayRegionSnapshot> Regions { get; }
}

public sealed record RuntimeReconnectSnapshot
{
    public RuntimeReconnectSnapshot(
        Guid runtimeEpoch,
        long profileRevision,
        long policyRevision,
        IEnumerable<TargetSnapshot> targets,
        RuntimeCapabilities capabilities,
        RuntimeBudgetSnapshot budget)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(policyRevision, 1);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(budget);
        TargetSnapshot[] ownedTargets = targets.ToArray();
        if (ownedTargets.Select(target => target.TargetInstanceId).Distinct().Count() != ownedTargets.Length)
        {
            throw new ArgumentException("Reconnect targets must be unique.", nameof(targets));
        }
        if (budget.RuntimeEpoch != runtimeEpoch || capabilities.ProtocolVersion != RuntimeProtocol.CurrentVersion)
        {
            throw new ArgumentException("Reconnect state must belong to the negotiated runtime.");
        }

        RuntimeEpoch = runtimeEpoch;
        ProfileRevision = profileRevision;
        PolicyRevision = policyRevision;
        Targets = Array.AsReadOnly(ownedTargets);
        Capabilities = capabilities;
        Budget = budget;
    }

    public Guid RuntimeEpoch { get; }
    public long ProfileRevision { get; }
    public long PolicyRevision { get; }
    public IReadOnlyList<TargetSnapshot> Targets { get; }
    public RuntimeCapabilities Capabilities { get; }
    public RuntimeBudgetSnapshot Budget { get; }
}
