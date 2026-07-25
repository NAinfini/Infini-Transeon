using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.State;

/// <summary>
/// A live OCR recognition result that was admitted by <see cref="RuntimeStateStore"/> (i.e. it
/// belongs to the current runtime epoch and is not superseded by a newer source generation).
/// Carries the full source-generation identity so consumers can correlate it with subsequent
/// <see cref="LiveTranslationReceived"/> events for the same text track.
/// </summary>
public sealed record LiveOcrRecognized(
    Guid RuntimeEpoch,
    TargetInstanceId TargetInstanceId,
    CaptureAreaKey Area,
    TextTrackId TextTrackId,
    long SourceGeneration,
    long ProfileRevision,
    IReadOnlyList<TextLine> Lines,
    string ModelId,
    string ModelVersion,
    bool IsStable,
    string? TerminalErrorCode,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// A live translation output that was admitted by <see cref="RuntimeStateStore"/> (current source
/// generation, current channel run, and a stage/attempt/stream position ahead of anything already
/// observed for that display slot). Mirrors <see cref="TranslationOutput"/> plus the identity chain
/// and profile context needed to route it to the right UI surface.
/// </summary>
public sealed record LiveTranslationReceived(
    Guid RuntimeEpoch,
    Guid ProfileId,
    TargetInstanceId TargetInstanceId,
    CaptureAreaKey Area,
    TextTrackId TextTrackId,
    TranslationChannelId ChannelId,
    Guid ChannelRunId,
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
    string? SupersededReason,
    DateTimeOffset OccurredAtUtc);

/// <summary>A diagnostic forwarded from <see cref="IEngineRuntime.DiagnosticRaised"/>.</summary>
public sealed record RuntimeDiagnosticRaised(
    string ErrorCode,
    string MessageKey,
    RuntimeDiagnosticSeverity Severity,
    DateTimeOffset OccurredAtUtc);

/// <summary>Discriminates the payload kind carried by a <see cref="RuntimeHubEvent"/>.</summary>
public enum RuntimeHubEventKind
{
    OcrRecognized,
    TranslationReceived,
    DiagnosticRaised,
}

/// <summary>
/// One ring-buffer entry. <see cref="Payload"/> is a <see cref="LiveOcrRecognized"/>,
/// <see cref="LiveTranslationReceived"/> or <see cref="RuntimeDiagnosticRaised"/> depending on
/// <see cref="Kind"/>.
/// </summary>
public sealed record RuntimeHubEvent(RuntimeHubEventKind Kind, DateTimeOffset OccurredAtUtc, object Payload);

/// <summary>
/// WinUI-free, in-memory presentation event bus fed by engine events that already passed
/// <see cref="RuntimeStateStore"/> admission. Raises typed events for live subscribers (Home run
/// panel, overlay-adjacent UI) and keeps a bounded ring buffer snapshot for late subscribers (the
/// future Activity page). Also tallies results rejected by admission, by reason, so the diagnostic
/// surface can show how many stale/late results were dropped. Kept free of WinUI types so it stays
/// unit-testable and is registered as a plain DI singleton.
/// </summary>
/// <remarks>
/// Threading: <see cref="PublishOcrRecognized"/>, <see cref="PublishTranslationReceived"/>,
/// <see cref="PublishDiagnosticRaised"/> and <see cref="RecordAdmissionRejected"/> raise their
/// corresponding .NET event synchronously on the calling thread. For engine-sourced events that
/// thread is whatever thread the EngineHost transport delivers the callback on — not necessarily
/// the UI thread. Marshalling onto the UI dispatcher (via <c>IUiDispatcher</c>, per the
/// architecture doc §7.3) is the responsibility of subscribers; this hub does not dispatch.
/// The ring buffer and counters are internally synchronized and safe to read/write concurrently.
/// </remarks>
public sealed class RuntimeEventHub
{
    /// <summary>Maximum number of retained ring-buffer entries; oldest entries are evicted first.</summary>
    public const int RingBufferCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<RuntimeHubEvent> _ring = new(RingBufferCapacity);
    private readonly Dictionary<RuntimeStateAdmission, long> _admissionRejectedCounts = new()
    {
        [RuntimeStateAdmission.RejectedStaleSourceGeneration] = 0,
        [RuntimeStateAdmission.RejectedStaleChannel] = 0,
        [RuntimeStateAdmission.RejectedStaleStage] = 0,
    };

    /// <summary>Raised when a live OCR result is published.</summary>
    public event EventHandler<LiveOcrRecognized>? OcrRecognized;

    /// <summary>Raised when a live translation output is published.</summary>
    public event EventHandler<LiveTranslationReceived>? TranslationReceived;

    /// <summary>Raised when an engine diagnostic is published.</summary>
    public event EventHandler<RuntimeDiagnosticRaised>? DiagnosticRaised;

    /// <summary>Total count of results rejected by admission, across every reason.</summary>
    public long TotalAdmissionRejectedCount
    {
        get { lock (_gate) return _admissionRejectedCounts.Values.Sum(); }
    }

    /// <summary>Publishes an admitted OCR result: appends it to the ring buffer and raises <see cref="OcrRecognized"/>.</summary>
    public void PublishOcrRecognized(LiveOcrRecognized payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Enqueue(new RuntimeHubEvent(RuntimeHubEventKind.OcrRecognized, payload.OccurredAtUtc, payload));
        OcrRecognized?.Invoke(this, payload);
    }

    /// <summary>Publishes an admitted translation output: appends it to the ring buffer and raises <see cref="TranslationReceived"/>.</summary>
    public void PublishTranslationReceived(LiveTranslationReceived payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Enqueue(new RuntimeHubEvent(RuntimeHubEventKind.TranslationReceived, payload.OccurredAtUtc, payload));
        TranslationReceived?.Invoke(this, payload);
    }

    /// <summary>Publishes a diagnostic: appends it to the ring buffer and raises <see cref="DiagnosticRaised"/>.</summary>
    public void PublishDiagnosticRaised(RuntimeDiagnosticRaised payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Enqueue(new RuntimeHubEvent(RuntimeHubEventKind.DiagnosticRaised, payload.OccurredAtUtc, payload));
        DiagnosticRaised?.Invoke(this, payload);
    }

    /// <summary>Records a result rejected by <see cref="RuntimeStateStore"/> admission, by reason.</summary>
    public void RecordAdmissionRejected(RuntimeStateAdmission reason)
    {
        if (reason == RuntimeStateAdmission.Accepted)
        {
            throw new ArgumentException("Accepted admissions are not rejections.", nameof(reason));
        }

        lock (_gate)
        {
            _admissionRejectedCounts[reason] = _admissionRejectedCounts.GetValueOrDefault(reason) + 1;
        }
    }

    /// <summary>The current rejection count for a single admission reason.</summary>
    public long GetAdmissionRejectedCount(RuntimeStateAdmission reason)
    {
        lock (_gate) return _admissionRejectedCounts.GetValueOrDefault(reason);
    }

    /// <summary>A snapshot of the rejection counts, by reason.</summary>
    public IReadOnlyDictionary<RuntimeStateAdmission, long> AdmissionRejectedCounts()
    {
        lock (_gate) return new Dictionary<RuntimeStateAdmission, long>(_admissionRejectedCounts);
    }

    /// <summary>A snapshot of the ring buffer, oldest first, for late subscribers (e.g. the Activity page).</summary>
    public IReadOnlyList<RuntimeHubEvent> Snapshot()
    {
        lock (_gate) return [.. _ring];
    }

    private void Enqueue(RuntimeHubEvent hubEvent)
    {
        lock (_gate)
        {
            if (_ring.Count == RingBufferCapacity)
            {
                _ring.Dequeue();
            }

            _ring.Enqueue(hubEvent);
        }
    }
}
