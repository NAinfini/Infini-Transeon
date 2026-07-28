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
public sealed class PausableRuntimeTranslationPipeline :
    IRuntimeTranslationPipeline,
    ITargetScopedRuntimeTranslationPipeline,
    IReplayableRuntimeTranslationPipeline
{
    private readonly IRuntimeTranslationPipeline _inner;
    private readonly IEngineRuntimeEventPublisher? _publisher;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _registeredTargets = [];
    private readonly HashSet<Guid> _pausedTargets = [];
    private bool _allPaused;

    public PausableRuntimeTranslationPipeline(
        IRuntimeTranslationPipeline inner,
        IEngineRuntimeEventPublisher? publisher = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _publisher = publisher;
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _allPaused ||
                    (_registeredTargets.Count > 0 &&
                     _registeredTargets.IsSubsetOf(_pausedTargets));
            }
        }
    }

    public bool IsTargetPaused(TargetInstanceId targetInstanceId)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        lock (_gate)
        {
            return _allPaused || _pausedTargets.Contains(targetInstanceId.Value);
        }
    }

    public async ValueTask PauseAllAsync(CancellationToken cancellationToken)
    {
        TargetInstanceId[] targets;
        lock (_gate)
        {
            _allPaused = true;
            _pausedTargets.UnionWith(_registeredTargets);
            targets = _registeredTargets.Select(id => new TargetInstanceId(id)).ToArray();
        }
        if (targets.Length > 0)
        {
            await SetInnerPausedAsync(targets, paused: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask ResumeAllAsync(CancellationToken cancellationToken)
    {
        TargetInstanceId[] targets;
        lock (_gate)
        {
            targets = _registeredTargets.Select(id => new TargetInstanceId(id)).ToArray();
            _allPaused = false;
            _pausedTargets.Clear();
        }
        if (targets.Length > 0)
        {
            await SetInnerPausedAsync(targets, paused: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask SetTargetsPausedAsync(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
        bool paused,
        CancellationToken cancellationToken)
    {
        TargetInstanceId[] targets = ValidateTargets(targetInstanceIds);
        lock (_gate)
        {
            if (_allPaused && !paused)
            {
                _allPaused = false;
                _pausedTargets.UnionWith(_registeredTargets);
            }
            foreach (TargetInstanceId target in targets)
            {
                if (paused) _pausedTargets.Add(target.Value);
                else _pausedTargets.Remove(target.Value);
            }
        }
        await SetInnerPausedAsync(targets, paused, cancellationToken).ConfigureAwait(false);
    }

    public void Register(RuntimeTranslationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _inner.Register(target);
        lock (_gate)
        {
            _registeredTargets.Add(target.TargetInstanceId.Value);
            if (_allPaused) _pausedTargets.Add(target.TargetInstanceId.Value);
        }
    }

    public ValueTask ReplaceAsync(
        RuntimeTranslationTarget target,
        CancellationToken cancellationToken) =>
        _inner.ReplaceAsync(target, cancellationToken);

    public void Unregister(TargetInstanceId targetInstanceId)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        lock (_gate)
        {
            _registeredTargets.Remove(targetInstanceId.Value);
            _pausedTargets.Remove(targetInstanceId.Value);
        }
        _inner.Unregister(targetInstanceId);
    }

    public ValueTask EnqueueAsync(OcrResultSnapshot result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        _publisher?.PublishOcrResult(result);
        if (IsTargetPaused(result.ExecutionToken.Source.TargetInstanceId))
        {
            return ValueTask.CompletedTask;
        }
        return _inner.EnqueueAsync(result, cancellationToken);
    }

    public ValueTask<RuntimeVisibleReplayResult> ReplayVisibleAsync(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
        CancellationToken cancellationToken) =>
        _inner is IReplayableRuntimeTranslationPipeline replayable
            ? replayable.ReplayVisibleAsync(targetInstanceIds, cancellationToken)
            : ValueTask.FromException<RuntimeVisibleReplayResult>(
                new EngineRuntimeUnsupportedOperationException("translationGroupReplay"));

    public Task DrainAsync(CancellationToken cancellationToken) =>
        _inner.DrainAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private ValueTask SetInnerPausedAsync(
        IReadOnlyCollection<TargetInstanceId> targets,
        bool paused,
        CancellationToken cancellationToken) =>
        _inner is ITargetScopedRuntimeTranslationPipeline targetScoped
            ? targetScoped.SetTargetsPausedAsync(targets, paused, cancellationToken)
            : ValueTask.FromException(
                new EngineRuntimeUnsupportedOperationException("targetScopedPause"));

    private static TargetInstanceId[] ValidateTargets(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceIds);
        TargetInstanceId[] targets = targetInstanceIds.ToArray();
        if (targets.Length is < 1 or > 128 ||
            targets.Any(target => target is null || target.Value == Guid.Empty) ||
            targets.Distinct().Count() != targets.Length)
        {
            throw new ArgumentException(
                "Target-scoped control requires unique, non-empty target identities.",
                nameof(targetInstanceIds));
        }
        return targets;
    }
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
    private sealed class TargetVisibilityState
    {
        public required bool Visible { get; set; }
        public long LastTransportRevision { get; set; }
        public OverlayDesiredState? Desired { get; set; }
    }

    private readonly IRuntimeOverlaySink _inner;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<Guid, TargetVisibilityState> _targets = [];
    private bool _defaultVisible;

    public VisibilityGatingOverlaySink(IRuntimeOverlaySink inner, bool visible = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _defaultVisible = visible;
    }

    public bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return _targets.Count == 0
                    ? _defaultVisible
                    : _targets.Values.All(target => target.Visible);
            }
        }
    }

    public bool IsTargetVisible(TargetInstanceId targetInstanceId)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        lock (_gate)
        {
            return _targets.TryGetValue(
                targetInstanceId.Value,
                out TargetVisibilityState? target)
                ? target.Visible
                : _defaultVisible;
        }
    }

    public async ValueTask SetAllVisibleAsync(
        bool visible,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OverlayDesiredState[] states;
            lock (_gate)
            {
                _defaultVisible = visible;
                var pending = new List<OverlayDesiredState>(_targets.Count);
                foreach (TargetVisibilityState target in _targets.Values)
                {
                    target.Visible = visible;
                    if (target.Desired is not null)
                    {
                        pending.Add(CreateTransportState(target));
                    }
                }
                states = pending.ToArray();
            }
            foreach (OverlayDesiredState state in states)
            {
                await _inner.ApplyAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask SetTargetsVisibleAsync(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
        bool visible,
        CancellationToken cancellationToken)
    {
        TargetInstanceId[] targets = ValidateTargets(targetInstanceIds);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OverlayDesiredState[] states;
            lock (_gate)
            {
                var pending = new List<OverlayDesiredState>(targets.Length);
                foreach (TargetInstanceId targetId in targets)
                {
                    TargetVisibilityState target = GetOrCreateTarget(targetId.Value);
                    target.Visible = visible;
                    if (target.Desired is not null)
                    {
                        pending.Add(CreateTransportState(target));
                    }
                }
                states = pending.ToArray();
            }
            foreach (OverlayDesiredState state in states)
            {
                await _inner.ApplyAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask ApplyAsync(
        OverlayDesiredState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OverlayDesiredState transport;
            lock (_gate)
            {
                TargetVisibilityState target =
                    GetOrCreateTarget(state.TargetInstanceId.Value);
                target.Desired = state;
                transport = CreateTransportState(target);
            }
            await _inner.ApplyAsync(transport, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private TargetVisibilityState GetOrCreateTarget(Guid targetId)
    {
        if (!_targets.TryGetValue(targetId, out TargetVisibilityState? target))
        {
            target = new TargetVisibilityState { Visible = _defaultVisible };
            _targets.Add(targetId, target);
        }
        return target;
    }

    private static TargetInstanceId[] ValidateTargets(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceIds);
        TargetInstanceId[] targets = targetInstanceIds.ToArray();
        if (targets.Length is < 1 or > 128 ||
            targets.Any(target => target is null || target.Value == Guid.Empty) ||
            targets.Distinct().Count() != targets.Length)
        {
            throw new ArgumentException(
                "Target-scoped control requires unique, non-empty target identities.",
                nameof(targetInstanceIds));
        }
        return targets;
    }

    private static OverlayDesiredState CreateTransportState(TargetVisibilityState target)
    {
        OverlayDesiredState desired = target.Desired
            ?? throw new InvalidOperationException("overlay.visibility.noDesiredState");
        long nextRevision = desired.OverlayRevision > target.LastTransportRevision
            ? desired.OverlayRevision
            : checked(target.LastTransportRevision + 1);
        target.LastTransportRevision = nextRevision;
        return new OverlayDesiredState(
            desired.RuntimeEpoch,
            desired.TargetInstanceId,
            nextRevision,
            target.Visible ? desired.Regions : []);
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
public sealed class DefaultEngineRuntimeControl :
    IEngineRuntimeControl,
    ITargetScopedEngineRuntimeControl
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

    public ValueTask PauseAllAsync(CancellationToken cancellationToken) =>
        _pause.PauseAllAsync(cancellationToken);

    public ValueTask ResumeAllAsync(CancellationToken cancellationToken) =>
        _pause.ResumeAllAsync(cancellationToken);

    public ValueTask SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken) =>
        _overlay.SetAllVisibleAsync(visible, cancellationToken);

    public ValueTask SetTargetsPausedAsync(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
        bool paused,
        CancellationToken cancellationToken) =>
        _pause.SetTargetsPausedAsync(targetInstanceIds, paused, cancellationToken);

    public ValueTask SetTargetsOverlayVisibleAsync(
        IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
        bool visible,
        CancellationToken cancellationToken) =>
        _overlay.SetTargetsVisibleAsync(targetInstanceIds, visible, cancellationToken);

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

    public async ValueTask RequestManualOcrAsync(
        RuntimeManualOcrRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RuntimeManualOcrAcknowledgement acknowledgement =
            await _session.RequestManualOcrAsync(
                request,
                _commandTimeout,
                cancellationToken).ConfigureAwait(false);
        if (!acknowledgement.Accepted)
        {
            throw new EngineRuntimeCommandRejectedException(
                "manualOcr",
                acknowledgement.ErrorCode ?? "ocr.manual.rejected");
        }
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
