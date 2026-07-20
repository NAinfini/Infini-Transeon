namespace InfiniTranseon.Contracts.Runtime;

public enum RuntimeContractError
{
    StreamIdentityChanged,
    StreamSequenceOutOfOrder,
    PolicyAcknowledgementOutOfOrder,
    PolicyAcknowledgementNotSent,
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
    Closed,
    WaitingForMatch,
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
    string? ErrorCode);

public sealed record OcrResultSnapshot(
    OcrExecutionToken ExecutionToken,
    IReadOnlyList<TextLine> Lines,
    string ModelId,
    string ModelVersion,
    bool IsStable,
    string? TerminalErrorCode);

public sealed record CloudOcrCropRequest
{
    private readonly byte[] _encodedCrop;

    public CloudOcrCropRequest(
        OcrExecutionToken executionToken,
        string mimeType,
        ReadOnlySpan<byte> encodedCrop,
        int pixelWidth,
        int pixelHeight,
        bool explicitCloudConsent)
    {
        ArgumentNullException.ThrowIfNull(executionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        if (!explicitCloudConsent)
        {
            throw new ArgumentException("Cloud OCR requires explicit profile consent.", nameof(explicitCloudConsent));
        }
        if (encodedCrop.IsEmpty || encodedCrop.Length > RuntimeProtocol.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(encodedCrop));
        }

        ExecutionToken = executionToken;
        MimeType = mimeType;
        _encodedCrop = encodedCrop.ToArray();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        ExplicitCloudConsent = true;
    }

    public OcrExecutionToken ExecutionToken { get; }
    public string MimeType { get; }
    public ReadOnlyMemory<byte> EncodedCrop => _encodedCrop;
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public bool ExplicitCloudConsent { get; }
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
    long ProfileRevision,
    IReadOnlyDictionary<RegionId, string> RegionPolicies);

public sealed record PolicyAcknowledgement(long Revision, bool Accepted, string? RejectionCode);

public sealed class RuntimePolicyAcknowledgementGate
{
    private readonly HashSet<long> _sent = [];
    private long _lastAcknowledged;

    public void RecordSent(long revision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        _sent.Add(revision);
    }

    public void Accept(PolicyAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!_sent.Contains(acknowledgement.Revision))
        {
            throw new RuntimeContractException(RuntimeContractError.PolicyAcknowledgementNotSent);
        }
        if (acknowledgement.Revision <= _lastAcknowledged)
        {
            throw new RuntimeContractException(RuntimeContractError.PolicyAcknowledgementOutOfOrder);
        }

        _lastAcknowledged = acknowledgement.Revision;
        _sent.RemoveWhere(revision => revision <= _lastAcknowledged);
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
    string Label);

public sealed record OverlayDesiredState
{
    public OverlayDesiredState(
        Guid runtimeEpoch,
        TargetInstanceId targetInstanceId,
        long overlayRevision,
        IEnumerable<OverlaySlotSnapshot> orderedSlots)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(overlayRevision, 1);
        ArgumentNullException.ThrowIfNull(orderedSlots);
        OverlaySlotSnapshot[] slots = orderedSlots.OrderBy(slot => slot.Order).ToArray();
        if (slots.Length > RuntimeCapabilities.VersionOne.MaxTranslationChannelsPerRegion ||
            slots.Any(slot => slot.SlotId == Guid.Empty || slot.Order < 0) ||
            slots.Select(slot => slot.SlotId).Distinct().Count() != slots.Length ||
            slots.Select(slot => slot.Order).Distinct().Count() != slots.Length)
        {
            throw new ArgumentException("Overlay slots must be bounded and have unique identities and order.", nameof(orderedSlots));
        }

        RuntimeEpoch = runtimeEpoch;
        TargetInstanceId = targetInstanceId;
        OverlayRevision = overlayRevision;
        OrderedSlots = Array.AsReadOnly(slots);
    }

    public Guid RuntimeEpoch { get; }
    public TargetInstanceId TargetInstanceId { get; }
    public long OverlayRevision { get; }
    public IReadOnlyList<OverlaySlotSnapshot> OrderedSlots { get; }
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
