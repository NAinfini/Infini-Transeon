using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Runtime;

public interface IRuntimeOverlaySink
{
    ValueTask ApplyAsync(OverlayDesiredState state, CancellationToken cancellationToken);
}

public interface IRuntimeTranslationRecordSink
{
    ValueTask SaveAsync(
        Guid profileId,
        TextGeneration source,
        IReadOnlyList<TranslationOutput> outputs,
        CancellationToken cancellationToken);
}

public interface IRuntimeTranslationSessionSink
{
    void ProfileStopped(Guid profileId);
}

public interface IRuntimeTranslationPipeline : IAsyncDisposable
{
    void Register(RuntimeTranslationTarget target);
    void Unregister(TargetInstanceId targetInstanceId);
    ValueTask EnqueueAsync(OcrResultSnapshot result, CancellationToken cancellationToken);
    Task DrainAsync(CancellationToken cancellationToken);
}

public sealed class RuntimeEngineHostOverlaySink : IRuntimeOverlaySink
{
    private readonly IRuntimeEngineHostSession _session;
    private readonly TimeSpan _timeout;

    public RuntimeEngineHostOverlaySink(IRuntimeEngineHostSession session, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _session = session;
        _timeout = timeout;
    }

    public async ValueTask ApplyAsync(
        OverlayDesiredState state,
        CancellationToken cancellationToken)
    {
        RuntimeOverlayAcknowledgement acknowledgement = await _session.ApplyOverlayAsync(
            state, _timeout, cancellationToken).ConfigureAwait(false);
        if (!acknowledgement.Accepted)
            throw new InvalidOperationException(
                acknowledgement.ErrorCode ?? "overlay.runtime.rejected");
    }
}

public sealed record RuntimeTranslationTarget
{
    public RuntimeTranslationTarget(
        Guid runtimeEpoch,
        TargetInstanceId targetInstanceId,
        CaptureTargetId captureTargetId,
        long profileRevision,
        ProfileDocument profile,
        ProfileTarget profileTarget,
        int targetPixelWidth,
        int targetPixelHeight,
        TranslationRunOptions runOptions,
        bool reducedMotion = false)
    {
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentNullException.ThrowIfNull(captureTargetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profileTarget);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPixelWidth, 16_384);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPixelHeight, 16_384);
        ArgumentNullException.ThrowIfNull(runOptions);
        if (runOptions.ProfileId != profile.ProfileId ||
            captureTargetId.Value != profileTarget.TargetId)
            throw new ArgumentException("Translation target does not match its profile identities.");
        RuntimeEpoch = runtimeEpoch;
        TargetInstanceId = targetInstanceId;
        CaptureTargetId = captureTargetId;
        ProfileRevision = profileRevision;
        Profile = profile;
        ProfileTarget = profileTarget;
        TargetPixelWidth = targetPixelWidth;
        TargetPixelHeight = targetPixelHeight;
        RunOptions = runOptions;
        ReducedMotion = reducedMotion;
    }

    public Guid RuntimeEpoch { get; }
    public TargetInstanceId TargetInstanceId { get; }
    public CaptureTargetId CaptureTargetId { get; }
    public long ProfileRevision { get; }
    public ProfileDocument Profile { get; }
    public ProfileTarget ProfileTarget { get; }
    public int TargetPixelWidth { get; }
    public int TargetPixelHeight { get; }
    public TranslationRunOptions RunOptions { get; }
    public bool ReducedMotion { get; }
}

public sealed record RuntimePipelineFailure(
    string ErrorCode,
    TargetInstanceId? TargetInstanceId,
    TextTrackId? TextTrackId,
    Exception Exception);

public sealed class RuntimeTranslationPipeline : IRuntimeTranslationPipeline
{
    private readonly record struct ExecutionKey(Guid TargetInstanceId, Guid RegionId);

    private sealed class ActiveExecution(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly object _gate = new();
    private readonly TranslationOrchestrator _orchestrator;
    private readonly IRuntimeOverlaySink _overlay;
    private readonly OcrTextGenerationGate _textGate;
    private readonly Func<RuntimePipelineFailure, CancellationToken, ValueTask> _failure;
    private readonly IRuntimeTranslationRecordSink? _records;
    private readonly ITranslationDegradationPolicy? _degradationPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, RuntimeTranslationTarget> _targets = [];
    private readonly Dictionary<Guid, OverlayStateCoordinator> _overlays = [];
    private readonly Dictionary<ExecutionKey, ActiveExecution> _executions = [];
    private readonly ConcurrentQueue<Exception> _observerFailures = new();
    private int _disposed;

    public RuntimeTranslationPipeline(
        TranslationOrchestrator orchestrator,
        IRuntimeOverlaySink overlay,
        OcrTextGenerationGate textGate,
        Func<RuntimePipelineFailure, CancellationToken, ValueTask> failure,
        IRuntimeTranslationRecordSink? records = null,
        TimeProvider? timeProvider = null,
        ITranslationDegradationPolicy? degradationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(textGate);
        ArgumentNullException.ThrowIfNull(failure);
        _orchestrator = orchestrator;
        _overlay = overlay;
        _textGate = textGate;
        _failure = failure;
        _records = records;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _degradationPolicy = degradationPolicy;
    }

    public void Register(RuntimeTranslationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (!_targets.TryAdd(target.TargetInstanceId.Value, target))
                throw new InvalidOperationException("pipeline.targetAlreadyRegistered");
            _overlays.Add(
                target.TargetInstanceId.Value,
                new OverlayStateCoordinator(
                    target.RuntimeEpoch, target.TargetInstanceId, _timeProvider));
        }
    }

    public void Unregister(TargetInstanceId targetInstanceId)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Guid? stoppedProfile = null;
        lock (_gate)
        {
            if (!_targets.Remove(targetInstanceId.Value, out RuntimeTranslationTarget? removed)) return;
            _overlays.Remove(targetInstanceId.Value);
            ExecutionKey[] keys = _executions.Keys
                .Where(key => key.TargetInstanceId == targetInstanceId.Value)
                .ToArray();
            foreach (ExecutionKey key in keys)
            {
                if (_executions.Remove(key, out ActiveExecution? execution))
                    execution.Cancellation.Cancel();
            }
            if (!_targets.Values.Any(target => target.Profile.ProfileId == removed.Profile.ProfileId))
                stoppedProfile = removed.Profile.ProfileId;
        }
        if (stoppedProfile is Guid profileId && _records is IRuntimeTranslationSessionSink session)
            session.ProfileStopped(profileId);
    }

    public ValueTask EnqueueAsync(
        OcrResultSnapshot result,
        CancellationToken cancellationToken) =>
        EnqueueCoreAsync(result, cancellationToken, reconcileRemainingArea: true);

    private async ValueTask EnqueueCoreAsync(
        OcrResultSnapshot result,
        CancellationToken cancellationToken,
        bool reconcileRemainingArea)
    {
        ArgumentNullException.ThrowIfNull(result);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        SourceGenerationToken source = result.ExecutionToken.Source;
        RuntimeTranslationTarget? target;
        OverlayStateCoordinator? coordinator;
        lock (_gate)
        {
            _targets.TryGetValue(source.TargetInstanceId.Value, out target);
            _overlays.TryGetValue(source.TargetInstanceId.Value, out coordinator);
        }
        if (target is null || coordinator is null)
        {
            await ReportAsync(
                "pipeline.targetNotRegistered", source, new InvalidOperationException(
                    "OCR result target is not registered."), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (source.RuntimeEpoch != target.RuntimeEpoch ||
            source.ProfileRevision != target.ProfileRevision)
        {
            await ReportAsync(
                "pipeline.staleSource", source, new InvalidOperationException(
                    "OCR result belongs to a stale runtime or profile revision."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        ProfileRegion? region;
        try { region = ResolveRegion(target.ProfileTarget, source.Area); }
        catch (Exception exception)
        {
            await ReportAsync("pipeline.regionInvalid", source, exception, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (region is null)
        {
            await ReportAsync(
                "pipeline.regionNotRegistered", source, new InvalidOperationException(
                    "OCR result region is not registered."), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!region.Enabled || !region.TranslationEnabled) return;

        if (source.Area.Kind == CaptureAreaKind.RemainingArea)
        {
            TextLine[] filtered = result.Lines.Where(line =>
                !target.ProfileTarget.Regions.Any(explicitRegion =>
                    explicitRegion.Enabled && OverlapFraction(
                        line.Bounds, explicitRegion.Bounds) >= 0.5)).ToArray();
            result = result with { Lines = Array.AsReadOnly(filtered) };
            TextLine[] ordered = filtered
                .OrderBy(line => line.Bounds.Y)
                .ThenBy(line => line.Bounds.X)
                .ToArray();
            if (reconcileRemainingArea)
            {
                Guid[] retainedRegionIds = ordered.Length > 1
                    ? ordered.Select(line => DeriveAutomaticTrack(source, line).Value).ToArray()
                    : ordered.Length == 1
                        ? [source.TextTrackId.Value]
                        : [];
                await ReconcileAutomaticOverlaysAsync(
                    target,
                    coordinator,
                    source,
                    retainedRegionIds.ToHashSet(),
                    cancellationToken).ConfigureAwait(false);
            }
            if (filtered.Length > 1)
            {
                for (int index = 0; index < ordered.Length; index++)
                {
                    TextTrackId track = DeriveAutomaticTrack(source, ordered[index]);
                    var childSource = new SourceGenerationToken(
                        source.RuntimeEpoch,
                        source.TargetInstanceId,
                        source.Area,
                        track,
                        source.SourceGeneration,
                        source.ProfileRevision);
                    var childExecution = new OcrExecutionToken(
                        childSource,
                        result.ExecutionToken.OcrRunId,
                        result.ExecutionToken.Attempt,
                        result.ExecutionToken.ResultSequence);
                    await EnqueueCoreAsync(
                        result with
                        {
                            ExecutionToken = childExecution,
                            Lines = Array.AsReadOnly([ordered[index]]),
                        },
                        cancellationToken,
                        reconcileRemainingArea: false).ConfigureAwait(false);
                }
                return;
            }
        }

        TextGeneration? generation;
        bool cleared;
        try
        {
            bool ready = _textGate.TryCreate(
                result,
                target.CaptureTargetId,
                region,
                _timeProvider.GetUtcNow(),
                Stopwatch.GetTimestamp(),
                source.SourceGeneration,
                out generation,
                out cleared);
            if (!ready || generation is null)
            {
                if (cleared)
                    await ClearOverlayAsync(
                        target, region, coordinator, source, cancellationToken)
                        .ConfigureAwait(false);
                return;
            }
        }
        catch (OcrTextGenerationException exception)
        {
            await ReportAsync(exception.Code, source, exception, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            await ReportAsync("pipeline.ocrPostProcessingFailed", source, exception,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<TranslationChannelDefinition> channels;
        try { channels = ProfileTranslationFactory.CreateChannels(region); }
        catch (Exception exception)
        {
            await ReportAsync("pipeline.channelsInvalid", source, exception, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (_degradationPolicy?.ShouldPauseOptionalRefinement(region.RegionId) == true)
        {
            channels = channels.Select(channel => channel with
            {
                RefinementSteps = Array.Empty<RefinementStepDefinition>(),
            }).ToArray();
        }
        Guid displayRegionId = source.Area.Kind == CaptureAreaKind.UserRegion
            ? region.RegionId
            : source.TextTrackId.Value;
        var key = new ExecutionKey(target.TargetInstanceId.Value, displayRegionId);
        ActiveExecution execution;
        lock (_gate)
        {
            if (_executions.Remove(key, out ActiveExecution? previous))
                previous.Cancellation.Cancel();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            execution = new ActiveExecution(linked);
            execution.Task = RunDeferredAsync(
                key, execution, target, region, coordinator, generation, channels);
            _executions.Add(key, execution);
        }
        _ = ObserveCompletionAsync(key, execution);
    }

    private async ValueTask ClearOverlayAsync(
        RuntimeTranslationTarget target,
        ProfileRegion region,
        OverlayStateCoordinator coordinator,
        SourceGenerationToken source,
        CancellationToken cancellationToken)
    {
        Guid displayRegionId = source.Area.Kind == CaptureAreaKind.UserRegion
            ? region.RegionId
            : source.TextTrackId.Value;
        var key = new ExecutionKey(target.TargetInstanceId.Value, displayRegionId);
        lock (_gate)
        {
            if (_executions.Remove(key, out ActiveExecution? execution))
                execution.Cancellation.Cancel();
        }

        try
        {
            if (coordinator.RemoveRegion(displayRegionId, out OverlayDesiredState? state))
                await _overlay.ApplyAsync(state!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ReportAsync(
                "pipeline.overlayClearFailed", source, exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask ReconcileAutomaticOverlaysAsync(
        RuntimeTranslationTarget target,
        OverlayStateCoordinator coordinator,
        SourceGenerationToken source,
        IReadOnlySet<Guid> retainedRegionIds,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!coordinator.RemoveAreaRegionsExcept(
                    source.Area,
                    retainedRegionIds,
                    out IReadOnlyList<Guid> removedRegionIds,
                    out OverlayDesiredState? state))
                return;
            lock (_gate)
            {
                foreach (Guid regionId in removedRegionIds)
                {
                    var key = new ExecutionKey(target.TargetInstanceId.Value, regionId);
                    if (_executions.Remove(key, out ActiveExecution? execution))
                        execution.Cancellation.Cancel();
                }
            }
            await _overlay.ApplyAsync(state!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ReportAsync(
                "pipeline.overlayReconciliationFailed", source, exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] active;
            lock (_gate) active = _executions.Values.Select(item => item.Task).ToArray();
            if (active.Length == 0) break;
            await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!_observerFailures.IsEmpty)
            throw new AggregateException(_observerFailures);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        ActiveExecution[] active;
        lock (_gate)
        {
            active = _executions.Values.ToArray();
            foreach (ActiveExecution execution in active) execution.Cancellation.Cancel();
        }
        var failures = new List<Exception>();
        try
        {
            await Task.WhenAll(active.Select(item => item.Task)).ConfigureAwait(false);
        }
        catch
        {
            foreach (Exception exception in active
                         .Select(item => item.Task.Exception)
                         .Where(exception => exception is not null)
                         .SelectMany(exception => exception!.Flatten().InnerExceptions))
            {
                if (!failures.Contains(exception, ReferenceEqualityComparer.Instance))
                    failures.Add(exception);
            }
        }
        finally
        {
            foreach (ActiveExecution execution in active) execution.Cancellation.Dispose();
            _shutdown.Dispose();
        }
        while (_observerFailures.TryDequeue(out Exception? observerFailure))
        {
            foreach (Exception exception in observerFailure is AggregateException aggregate
                         ? aggregate.Flatten().InnerExceptions
                         : [observerFailure])
            {
                if (!failures.Contains(exception, ReferenceEqualityComparer.Instance))
                    failures.Add(exception);
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private async Task RunAsync(
        ExecutionKey key,
        ActiveExecution execution,
        RuntimeTranslationTarget target,
        ProfileRegion region,
        OverlayStateCoordinator coordinator,
        TextGeneration generation,
        IReadOnlyList<TranslationChannelDefinition> channels)
    {
        CancellationToken cancellationToken = execution.Cancellation.Token;
        try
        {
            (OverlayPixelRect bounds, OverlayRegionStyleSnapshot style) =
                ProfileOverlaySnapshotFactory.Create(
                    region,
                    target.TargetPixelWidth,
                    target.TargetPixelHeight,
                    generation.SourceToken.Area.Kind == CaptureAreaKind.UserRegion
                        ? null
                        : generation.SourceBounds,
                    target.ReducedMotion);
            Guid displayRegionId = generation.SourceToken.Area.Kind == CaptureAreaKind.UserRegion
                ? region.RegionId
                : generation.SourceToken.TextTrackId.Value;
            OverlayDesiredState initial = coordinator.BeginRegion(
                displayRegionId, generation.SourceToken, bounds, style, channels);
            await _overlay.ApplyAsync(initial, cancellationToken).ConfigureAwait(false);
            var outputs = new List<TranslationOutput>();
            await foreach (TranslationOutput output in _orchestrator.RunAsync(
                               generation,
                               channels,
                               target.RunOptions,
                               cancellationToken).ConfigureAwait(false))
            {
                outputs.Add(output);
                TimeSpan refinementDelay = coordinator.GetRefinementDelay(
                    output, region.Overlay.MinimumDwell);
                if (refinementDelay > TimeSpan.Zero)
                {
                    await Task.Delay(refinementDelay, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (coordinator.TryApply(output, out OverlayDesiredState? state))
                    await _overlay.ApplyAsync(state!, cancellationToken).ConfigureAwait(false);
            }
            if (_records is not null)
                await _records.SaveAsync(
                    target.Profile.ProfileId,
                    generation,
                    outputs.AsReadOnly(),
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ReportAsync(
                "pipeline.translationFailed",
                generation.SourceToken,
                exception,
                _shutdown.Token).ConfigureAwait(false);
        }
    }

    private async Task RunDeferredAsync(
        ExecutionKey key,
        ActiveExecution execution,
        RuntimeTranslationTarget target,
        ProfileRegion region,
        OverlayStateCoordinator coordinator,
        TextGeneration generation,
        IReadOnlyList<TranslationChannelDefinition> channels)
    {
        await Task.Yield();
        await RunAsync(
            key, execution, target, region, coordinator, generation, channels)
            .ConfigureAwait(false);
    }

    private async Task ObserveCompletionAsync(ExecutionKey key, ActiveExecution execution)
    {
        try { await execution.Task.ConfigureAwait(false); }
        catch (Exception exception) { _observerFailures.Enqueue(exception); }
        finally
        {
            lock (_gate)
            {
                if (_executions.TryGetValue(key, out ActiveExecution? current) &&
                    ReferenceEquals(current, execution))
                    _executions.Remove(key);
            }
            execution.Cancellation.Dispose();
        }
    }

    private ValueTask ReportAsync(
        string errorCode,
        SourceGenerationToken source,
        Exception exception,
        CancellationToken cancellationToken) =>
        _failure(new RuntimePipelineFailure(
            errorCode,
            source.TargetInstanceId,
            source.TextTrackId,
            exception), cancellationToken);

    private static ProfileRegion? ResolveRegion(
        ProfileTarget target,
        CaptureAreaKey area)
    {
        IEnumerable<ProfileRegion> candidates = target.Regions;
        if (target.RemainingAreaRegion is not null)
            candidates = candidates.Append(target.RemainingAreaRegion);
        ProfileRegion[] matches = candidates.Where(region =>
            region.AreaMode == area.Kind &&
            (area.Kind != CaptureAreaKind.UserRegion ||
                region.RegionId == area.UserRegionId?.Value)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("Multiple profile regions match one OCR area.");
        return matches.SingleOrDefault();
    }

    private static double OverlapFraction(NormalizedRect candidate, NormalizedRect exclusion)
    {
        double left = Math.Max(candidate.X, exclusion.X);
        double top = Math.Max(candidate.Y, exclusion.Y);
        double right = Math.Min(candidate.X + candidate.Width, exclusion.X + exclusion.Width);
        double bottom = Math.Min(candidate.Y + candidate.Height, exclusion.Y + exclusion.Height);
        if (right <= left || bottom <= top) return 0;
        return (right - left) * (bottom - top) /
            (candidate.Width * candidate.Height);
    }

    private static TextTrackId DeriveAutomaticTrack(
        SourceGenerationToken parent,
        TextLine line)
    {
        Span<byte> identity = stackalloc byte[32];
        parent.TextTrackId.Value.TryWriteBytes(identity[..16]);
        WriteCoordinate(identity[16..20], line.Bounds.X);
        WriteCoordinate(identity[20..24], line.Bounds.Y);
        WriteCoordinate(identity[24..28], line.Bounds.Width);
        WriteCoordinate(identity[28..32], line.Bounds.Height);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        digest[7] = (byte)((digest[7] & 0x0f) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new TextTrackId(new Guid(digest[..16]));

        static void WriteCoordinate(Span<byte> destination, double value) =>
            BinaryPrimitives.WriteInt32LittleEndian(
                destination,
                checked((int)Math.Round(value * 1024, MidpointRounding.AwayFromZero)));
    }
}
