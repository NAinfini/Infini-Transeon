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
/// Default control surface backed by the pause pipeline and visibility overlay sink.
/// Manual OCR is not exposed by protocol v1 (the EngineHost drives its own OCR cadence and
/// has no app-initiated recognition message), so it fails loudly rather than pretending to
/// succeed.
/// </summary>
public sealed class DefaultEngineRuntimeControl : IEngineRuntimeControl
{
    private readonly PausableRuntimeTranslationPipeline _pause;
    private readonly VisibilityGatingOverlaySink _overlay;

    public DefaultEngineRuntimeControl(
        PausableRuntimeTranslationPipeline pause,
        VisibilityGatingOverlaySink overlay)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(overlay);
        _pause = pause;
        _overlay = overlay;
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

    public ValueTask RequestManualOcrAsync(CancellationToken cancellationToken) =>
        throw new EngineRuntimeUnsupportedOperationException("manualOcr");
}
