namespace InfiniTranseon.Contracts.Runtime;

/// <summary>
/// Lifecycle states surfaced by <see cref="IEngineRuntime"/> for UI consumption. The
/// facade is a small state machine over the real EngineHost launch/handshake/restart
/// machinery; every transition is explicit so that failures are visible rather than
/// silently degraded.
/// </summary>
public enum EngineRuntimeStatus
{
    /// <summary>No EngineHost process has been started (initial and post-stop state).</summary>
    Stopped = 0,

    /// <summary>The facade is resolving the EngineHost executable on disk.</summary>
    Locating = 1,

    /// <summary>The EngineHost process is being launched and the handshake is in flight.</summary>
    Starting = 2,

    /// <summary>The EngineHost is running and the backend is streaming events.</summary>
    Running = 3,

    /// <summary>The EngineHost exited or faulted unexpectedly and is being restarted.</summary>
    Restarting = 4,

    /// <summary>A graceful shutdown of the EngineHost is in progress.</summary>
    Stopping = 5,

    /// <summary>
    /// The EngineHost could not be started and no further restart will be attempted (for
    /// example the restart budget was exhausted or the first launch failed).
    /// </summary>
    Faulted = 6,

    /// <summary>
    /// The EngineHost executable could not be located. <see cref="EngineRuntimeStatusChange.SearchedPaths"/>
    /// carries every probed location so the failure can be diagnosed.
    /// </summary>
    ExecutableNotFound = 7,
}

/// <summary>A single observed lifecycle transition of <see cref="IEngineRuntime"/>.</summary>
public sealed record EngineRuntimeStatusChange
{
    public EngineRuntimeStatusChange(
        EngineRuntimeStatus status,
        DateTimeOffset occurredAtUtc,
        string? errorCode = null,
        IReadOnlyList<string>? searchedPaths = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Status change time must use UTC.", nameof(occurredAtUtc));
        }

        Status = status;
        OccurredAtUtc = occurredAtUtc;
        ErrorCode = errorCode;
        SearchedPaths = searchedPaths is null
            ? Array.Empty<string>()
            : Array.AsReadOnly(searchedPaths.ToArray());
    }

    public EngineRuntimeStatus Status { get; }
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Stable diagnostic code when the transition carries a failure; otherwise null.</summary>
    public string? ErrorCode { get; }

    /// <summary>Locations probed by the locator; only populated for <see cref="EngineRuntimeStatus.ExecutableNotFound"/>.</summary>
    public IReadOnlyList<string> SearchedPaths { get; }
}

/// <summary>An OCR recognition result surfaced to the UI as it arrives from the EngineHost.</summary>
public sealed record EngineOcrResultEvent(OcrResultSnapshot Result, DateTimeOffset OccurredAtUtc);

/// <summary>A translation output surfaced to the UI as the translation pipeline produces it.</summary>
public sealed record EngineTranslationOutputEvent(
    Guid ProfileId,
    TranslationOutput Output,
    DateTimeOffset OccurredAtUtc);

/// <summary>A capture-target lifecycle change surfaced to the UI.</summary>
public sealed record EngineTargetSnapshotEvent(TargetLifecycleEvent Lifecycle);

/// <summary>A runtime budget snapshot surfaced to the UI.</summary>
public sealed record EngineBudgetEvent(RuntimeBudgetSnapshot Snapshot);

/// <summary>A diagnostic surfaced to the UI (per-channel credential gaps, pipeline failures, restarts).</summary>
public sealed record EngineDiagnostic(
    string ErrorCode,
    string MessageKey,
    RuntimeDiagnosticSeverity Severity,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// UI-facing facade over the EngineHost runtime. Implementations own the process
/// lifecycle, handshake, restart supervision and event fan-out; consumers observe
/// status and events and issue coarse control commands.
/// </summary>
public interface IEngineRuntime : IAsyncDisposable
{
    /// <summary>The most recent observed lifecycle status.</summary>
    EngineRuntimeStatus Status { get; }

    /// <summary>Raised on every lifecycle transition.</summary>
    event EventHandler<EngineRuntimeStatusChange>? StatusChanged;

    /// <summary>The current set of capture-target snapshots, keyed by their instance.</summary>
    IReadOnlyList<TargetSnapshot> TargetSnapshots { get; }

    /// <summary>Raised when a capture target's lifecycle state changes.</summary>
    event EventHandler<EngineTargetSnapshotEvent>? TargetsChanged;

    /// <summary>Raised when the EngineHost emits an OCR result.</summary>
    event EventHandler<EngineOcrResultEvent>? OcrResultReceived;

    /// <summary>Raised when the translation pipeline produces an output.</summary>
    event EventHandler<EngineTranslationOutputEvent>? TranslationOutputReceived;

    /// <summary>Raised when the EngineHost publishes a runtime budget snapshot.</summary>
    event EventHandler<EngineBudgetEvent>? BudgetUpdated;

    /// <summary>Raised for pipeline failures, per-channel credential gaps and restart notices.</summary>
    event EventHandler<EngineDiagnostic>? DiagnosticRaised;

    /// <summary>Locate, launch and hand-shake the EngineHost, then begin streaming events.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>Gracefully shut the EngineHost down and stop streaming events.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Suspend translation of new OCR results across every active target.</summary>
    ValueTask PauseAllAsync(CancellationToken cancellationToken);

    /// <summary>Resume translation of new OCR results across every active target.</summary>
    ValueTask ResumeAllAsync(CancellationToken cancellationToken);

    /// <summary>Show or hide the translation overlay for every active target.</summary>
    ValueTask SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken);

    /// <summary>Request an out-of-cadence OCR pass, when the backend supports it.</summary>
    ValueTask RequestManualOcrAsync(CancellationToken cancellationToken);

    /// <summary>Request one bounded preview frame for a running target.</summary>
    ValueTask<RuntimeThumbnail> RequestThumbnailAsync(
        TargetInstanceId targetInstanceId,
        int maximumLongEdge,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<RuntimeThumbnail>(
            new NotSupportedException("engine.runtime.unsupported.thumbnail"));

}

/// <summary>
/// Optional target-scoped control surface for a single running engine session. Explicit target
/// collections are always non-empty and never fall back to all targets.
/// </summary>
public interface ITargetScopedEngineRuntime
{
    ValueTask SetTargetsPausedAsync(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
        bool paused,
        CancellationToken cancellationToken);

    ValueTask SetTargetsOverlayVisibleAsync(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
        bool visible,
        CancellationToken cancellationToken);

    ValueTask RequestManualOcrAsync(
        RuntimeManualOcrRequest request,
        CancellationToken cancellationToken);
}
