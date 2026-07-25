using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

/// <summary>
/// Decorates a translation pipeline so the facade can (a) surface every OCR result to the
/// UI and (b) suspend translation without halting the EngineHost. While paused, OCR
/// results are still published for display but are not forwarded into the inner pipeline,
/// so no translation or overlay work occurs. This is the closest real mechanism to a
/// "pause all" command: the EngineHost protocol has no pause message, so suspension is
/// applied at the app boundary where OCR results enter translation.
/// </summary>
public sealed class PausableRuntimeTranslationPipeline : IRuntimeTranslationPipeline
{
    private readonly IRuntimeTranslationPipeline _inner;
    private readonly IEngineRuntimeEventPublisher? _publisher;
    private int _paused;

    public PausableRuntimeTranslationPipeline(
        IRuntimeTranslationPipeline inner,
        IEngineRuntimeEventPublisher? publisher = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _publisher = publisher;
    }

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public void Pause() => Volatile.Write(ref _paused, 1);

    public void Resume() => Volatile.Write(ref _paused, 0);

    public void Register(RuntimeTranslationTarget target) => _inner.Register(target);

    public ValueTask ReplaceAsync(
        RuntimeTranslationTarget target,
        CancellationToken cancellationToken) =>
        _inner.ReplaceAsync(target, cancellationToken);

    public void Unregister(TargetInstanceId targetInstanceId) =>
        _inner.Unregister(targetInstanceId);

    public ValueTask EnqueueAsync(OcrResultSnapshot result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        _publisher?.PublishOcrResult(result);
        if (Volatile.Read(ref _paused) != 0)
        {
            return ValueTask.CompletedTask;
        }
        return _inner.EnqueueAsync(result, cancellationToken);
    }

    public Task DrainAsync(CancellationToken cancellationToken) =>
        _inner.DrainAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// Decorates an overlay sink with a visibility gate. When hidden, each desired-state apply
/// is replaced with an empty-region state for the same target and revision, which clears
/// the EngineHost overlay through the real <see cref="RuntimeMessageKind.OverlayDesiredState"/>
/// message. Hiding therefore takes effect on the next overlay update per target; showing
/// resumes passing states through unchanged.
/// </summary>
public sealed class VisibilityGatingOverlaySink : IRuntimeOverlaySink
{
    private readonly IRuntimeOverlaySink _inner;
    private int _hidden;

    public VisibilityGatingOverlaySink(IRuntimeOverlaySink inner, bool visible = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _hidden = visible ? 0 : 1;
    }

    public bool IsVisible => Volatile.Read(ref _hidden) == 0;

    public void SetVisible(bool visible) => Volatile.Write(ref _hidden, visible ? 0 : 1);

    public ValueTask ApplyAsync(OverlayDesiredState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Volatile.Read(ref _hidden) != 0 && state.Regions.Count > 0)
        {
            state = new OverlayDesiredState(
                state.RuntimeEpoch,
                state.TargetInstanceId,
                state.OverlayRevision,
                []);
        }
        return _inner.ApplyAsync(state, cancellationToken);
    }
}

/// <summary>
/// Translation record sink that surfaces every produced output to the facade publisher
/// before forwarding to an optional inner sink (for example the persistent history store).
/// </summary>
public sealed class EngineRuntimeTranslationRecordSink
    : IRuntimeTranslationRecordSink, IRuntimeTranslationSessionSink
{
    private readonly IEngineRuntimeEventPublisher _publisher;
    private readonly IRuntimeTranslationRecordSink? _inner;

    public EngineRuntimeTranslationRecordSink(
        IEngineRuntimeEventPublisher publisher,
        IRuntimeTranslationRecordSink? inner = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
        _inner = inner;
    }

    public async ValueTask SaveAsync(
        Guid profileId,
        TextGeneration source,
        IReadOnlyList<TranslationOutput> outputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        foreach (TranslationOutput output in outputs)
        {
            _publisher.PublishTranslationOutput(profileId, output);
        }
        if (_inner is not null)
        {
            await _inner.SaveAsync(profileId, source, outputs, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void ProfileStopped(Guid profileId)
    {
        if (_inner is IRuntimeTranslationSessionSink session)
        {
            session.ProfileStopped(profileId);
        }
    }
}

/// <summary>
/// Default control surface backed by the pause pipeline, visibility overlay sink and the
/// authenticated EngineHost session. Manual OCR schedules one pass on the next usable frame;
/// recognition results continue through the normal OCR event pipeline.
/// </summary>
public sealed class DefaultEngineRuntimeControl : IEngineRuntimeControl
{
    private readonly PausableRuntimeTranslationPipeline _pause;
    private readonly VisibilityGatingOverlaySink _overlay;
    private readonly IRuntimeEngineHostSession _session;
    private readonly TimeSpan _commandTimeout;
    private readonly RuntimeBackendCoordinator? _coordinator;

    public DefaultEngineRuntimeControl(
        PausableRuntimeTranslationPipeline pause,
        VisibilityGatingOverlaySink overlay,
        IRuntimeEngineHostSession session,
        TimeSpan commandTimeout)
        : this(pause, overlay, session, commandTimeout, coordinator: null)
    {
    }

    public DefaultEngineRuntimeControl(
        PausableRuntimeTranslationPipeline pause,
        VisibilityGatingOverlaySink overlay,
        IRuntimeEngineHostSession session,
        TimeSpan commandTimeout,
        RuntimeBackendCoordinator? coordinator)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(session);
        if (commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        _pause = pause;
        _overlay = overlay;
        _session = session;
        _commandTimeout = commandTimeout;
        _coordinator = coordinator;
    }

    public ValueTask PauseAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pause.Pause();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pause.Resume();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _overlay.SetVisible(visible);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RequestManualOcrAsync(CancellationToken cancellationToken)
    {
        RuntimeManualOcrAcknowledgement acknowledgement =
            await _session.RequestManualOcrAsync(_commandTimeout, cancellationToken)
                .ConfigureAwait(false);
        if (!acknowledgement.Accepted)
            throw new EngineRuntimeCommandRejectedException(
                "manualOcr",
                acknowledgement.ErrorCode ?? "ocr.manual.rejected");
    }

    public ValueTask ApplyProfileAsync(
        RuntimeProfileBinding binding,
        CancellationToken cancellationToken) =>
        _coordinator?.ApplyProfileAsync(binding, cancellationToken) ??
        ValueTask.FromException(
            new EngineRuntimeUnsupportedOperationException("applyProfile"));

    public async ValueTask<RuntimeThumbnail> RequestThumbnailAsync(
        TargetInstanceId targetInstanceId,
        int maximumLongEdge,
        CancellationToken cancellationToken)
    {
        RuntimeThumbnailAcknowledgement acknowledgement =
            await _session.RequestThumbnailAsync(
                new RuntimeThumbnailRequest(targetInstanceId, maximumLongEdge),
                _commandTimeout,
                cancellationToken).ConfigureAwait(false);
        if (!acknowledgement.Accepted || acknowledgement.Thumbnail is null)
            throw new EngineRuntimeCommandRejectedException(
                "thumbnail",
                acknowledgement.ErrorCode ?? "thumbnail.rejected");
        return acknowledgement.Thumbnail;
    }
}
