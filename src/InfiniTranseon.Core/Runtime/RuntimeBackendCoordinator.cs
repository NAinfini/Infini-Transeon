using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Runtime;

public sealed record RuntimeTargetBinding
{
    public RuntimeTargetBinding(
        ProfileTarget profileTarget,
        TargetInstanceId targetInstanceId,
        ulong nativeHandle,
        OverlayPixelRect? desktopRegion,
        int targetPixelWidth,
        int targetPixelHeight,
        long commandRevision,
        long configurationRevision,
        TranslationRunOptions runOptions)
    {
        ArgumentNullException.ThrowIfNull(profileTarget);
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPixelWidth, 16_384);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPixelHeight, 16_384);
        ArgumentOutOfRangeException.ThrowIfLessThan(commandRevision, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(commandRevision, long.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(configurationRevision, 1);
        ArgumentNullException.ThrowIfNull(runOptions);
        RuntimeCaptureTargetKind runtimeKind = ToRuntimeKind(profileTarget.Kind);
        _ = new RuntimeCaptureTargetCommand(
            RuntimeCaptureTargetOperation.Upsert,
            commandRevision,
            new CaptureTargetId(profileTarget.TargetId),
            targetInstanceId,
            runtimeKind,
            nativeHandle,
            desktopRegion);
        ProfileTarget = profileTarget;
        TargetInstanceId = targetInstanceId;
        NativeHandle = nativeHandle;
        DesktopRegion = desktopRegion;
        TargetPixelWidth = targetPixelWidth;
        TargetPixelHeight = targetPixelHeight;
        CommandRevision = commandRevision;
        ConfigurationRevision = configurationRevision;
        RunOptions = runOptions;
    }

    public ProfileTarget ProfileTarget { get; }
    public TargetInstanceId TargetInstanceId { get; }
    public ulong NativeHandle { get; }
    public OverlayPixelRect? DesktopRegion { get; }
    public int TargetPixelWidth { get; }
    public int TargetPixelHeight { get; }
    public long CommandRevision { get; }
    public long ConfigurationRevision { get; }
    public TranslationRunOptions RunOptions { get; }

    internal static RuntimeCaptureTargetKind ToRuntimeKind(CaptureTargetKind kind) => kind switch
    {
        CaptureTargetKind.Window => RuntimeCaptureTargetKind.Window,
        CaptureTargetKind.Display => RuntimeCaptureTargetKind.Monitor,
        CaptureTargetKind.DesktopFixedRegion => RuntimeCaptureTargetKind.DesktopRegion,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed record RuntimeProfileBinding
{
    public RuntimeProfileBinding(
        ProfileDocument profile,
        long profileRevision,
        IEnumerable<RuntimeTargetBinding> targets)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        ArgumentNullException.ThrowIfNull(targets);
        RuntimeTargetBinding[] ownedTargets = targets.ToArray();
        if (ownedTargets.Length == 0 ||
            ownedTargets.Select(item => item.TargetInstanceId).Distinct().Count() !=
                ownedTargets.Length ||
            ownedTargets.Any(item => !item.ProfileTarget.Enabled ||
                item.RunOptions.ProfileId != profile.ProfileId ||
                item.RunOptions.StrictOffline != profile.StrictOffline ||
                !profile.Targets.Any(target => ReferenceEquals(target, item.ProfileTarget))))
            throw new ArgumentException(
                "Runtime target bindings do not match the profile.", nameof(targets));
        Profile = profile;
        ProfileRevision = profileRevision;
        Targets = Array.AsReadOnly(ownedTargets);
    }

    public ProfileDocument Profile { get; }
    public long ProfileRevision { get; }
    public IReadOnlyList<RuntimeTargetBinding> Targets { get; }
}

public sealed class RuntimeBackendCoordinator : IRecoverableRuntimeBackend, ITranslationGroupReplayResultSource
{
    private sealed record BoundTarget(
        ProfileDocument Profile,
        long ProfileRevision,
        RuntimeTargetBinding Binding);

    private readonly IRuntimeEngineHostSession _session;
    private readonly List<BoundTarget> _bindings;
    private readonly IRuntimeTranslationPipeline _pipeline;
    private readonly RuntimeEngineEventDispatcher _dispatcher;
    private readonly TimeSpan _commandTimeout;
    private readonly IReadOnlyList<IRuntimePerformanceController> _performance;
    private readonly bool _reducedMotion;
    private readonly object _budgetGate = new();
    private readonly Func<RuntimeBudgetSnapshot, CancellationToken, ValueTask>? _budgetSnapshot;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly List<BoundTarget> _active = [];
    private Task? _eventPump;
    private int _performanceStarted;
    private int _started;
    private int _disposed;
    private RuntimeBudgetSnapshot? _latestBudgetSnapshot;
    private RuntimeVisibleReplayResult? _lastTranslationGroupReplay;

    public RuntimeBackendCoordinator(
        IRuntimeEngineHostSession session,
        ProfileDocument profile,
        long profileRevision,
        IEnumerable<RuntimeTargetBinding> bindings,
        IRuntimeTranslationPipeline pipeline,
        RuntimeCloudOcrEventHandler cloudOcr,
        Func<TargetLifecycleEvent, CancellationToken, ValueTask> targetLifecycle,
        TimeSpan commandTimeout,
        IRuntimePerformanceController? performanceController = null,
        bool reducedMotion = false,
        Func<RuntimeBudgetSnapshot, CancellationToken, ValueTask>? budgetSnapshot = null)
        : this(
            session,
            [new RuntimeProfileBinding(profile, profileRevision, bindings)],
            pipeline,
            cloudOcr,
            targetLifecycle,
            commandTimeout,
            performanceController is null ? [] : [performanceController],
            reducedMotion,
            budgetSnapshot)
    {
    }

    public RuntimeBackendCoordinator(
        IRuntimeEngineHostSession session,
        IEnumerable<RuntimeProfileBinding> profiles,
        IRuntimeTranslationPipeline pipeline,
        RuntimeCloudOcrEventHandler cloudOcr,
        Func<TargetLifecycleEvent, CancellationToken, ValueTask> targetLifecycle,
        TimeSpan commandTimeout,
        IEnumerable<IRuntimePerformanceController>? performanceControllers = null,
        bool reducedMotion = false,
        Func<RuntimeBudgetSnapshot, CancellationToken, ValueTask>? budgetSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(cloudOcr);
        ArgumentNullException.ThrowIfNull(targetLifecycle);
        if (commandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        RuntimeProfileBinding[] ownedProfiles = profiles.ToArray();
        IRuntimePerformanceController[] ownedPerformance =
            performanceControllers?.ToArray() ?? [];
        BoundTarget[] ownedBindings = ownedProfiles
            .SelectMany(profile => profile.Targets.Select(target =>
                new BoundTarget(profile.Profile, profile.ProfileRevision, target)))
            .ToArray();
        if (ownedProfiles.Length == 0 ||
            ownedProfiles.Select(item => item.Profile.ProfileId).Distinct().Count() !=
                ownedProfiles.Length ||
            ownedBindings.Length > RuntimeCapabilities.VersionOne.MaxTargets ||
            ownedBindings.Select(item => item.Binding.TargetInstanceId).Distinct().Count() !=
                ownedBindings.Length)
            throw new ArgumentException(
                "Runtime profile bindings must be non-empty, unique and within capability limits.",
                nameof(profiles));
        if (ownedPerformance.Any(controller => controller is null))
            throw new ArgumentException(
                "Performance controllers cannot contain null.",
                nameof(performanceControllers));
        _session = session;
        _bindings = [.. ownedBindings];
        _pipeline = pipeline;
        _dispatcher = new RuntimeEngineEventDispatcher(
            cloudOcr,
            pipeline.EnqueueAsync,
            targetLifecycle,
            HandleBudgetSnapshotAsync);
        _commandTimeout = commandTimeout;
        _performance = Array.AsReadOnly(ownedPerformance);
        _reducedMotion = reducedMotion;
        _budgetSnapshot = budgetSnapshot;
    }

    public RuntimeBudgetSnapshot? LatestBudgetSnapshot
    {
        get { lock (_budgetGate) return _latestBudgetSnapshot; }
    }

    public RuntimeVisibleReplayResult? LastTranslationGroupReplay
    {
        get { lock (_budgetGate) return _lastTranslationGroupReplay; }
    }

    public Task Completion => Task.WhenAll(
        [_eventPump ?? Task.CompletedTask, .. _performance.Select(item => item.Completion)]);

    private async ValueTask HandleBudgetSnapshotAsync(
        RuntimeBudgetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.RuntimeEpoch != _session.RuntimeEpoch)
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
        lock (_budgetGate)
        {
            if (_latestBudgetSnapshot is not null &&
                snapshot.SnapshotRevision <= _latestBudgetSnapshot.SnapshotRevision)
                throw new RuntimeContractException(
                    RuntimeContractError.BudgetSnapshotOutOfOrder);
            _latestBudgetSnapshot = snapshot;
        }
        if (_budgetSnapshot is not null)
            await _budgetSnapshot(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Runtime backend coordinator has already started.");
        _eventPump = _dispatcher.PumpAsync(
            _session.ReadEventsAsync(_stop.Token),
            _stop.Token);
        try
        {
            foreach (BoundTarget boundTarget in _bindings)
            {
                RuntimeTargetBinding binding = boundTarget.Binding;
                cancellationToken.ThrowIfCancellationRequested();
                RuntimeCaptureTargetAcknowledgement capture =
                    await _session.ApplyCaptureTargetAsync(
                        CaptureCommand(binding, RuntimeCaptureTargetOperation.Upsert),
                        _commandTimeout,
                        cancellationToken).ConfigureAwait(false);
                if (!capture.Accepted)
                    throw new RuntimeBackendStartException(
                        capture.ErrorCode ?? "runtime.capture.rejected",
                        binding.TargetInstanceId);
                _active.Add(boundTarget);
                _pipeline.Register(new RuntimeTranslationTarget(
                    _session.RuntimeEpoch,
                    binding.TargetInstanceId,
                    new CaptureTargetId(binding.ProfileTarget.TargetId),
                    boundTarget.ProfileRevision,
                    boundTarget.Profile,
                    binding.ProfileTarget,
                    binding.TargetPixelWidth,
                    binding.TargetPixelHeight,
                    binding.RunOptions,
                    _reducedMotion));
                RuntimeProcessingConfiguration configuration =
                    ProfileRuntimeConfigurationFactory.Create(
                        binding.ProfileTarget,
                        binding.TargetInstanceId,
                        binding.ConfigurationRevision,
                        boundTarget.Profile.ProfileId,
                        boundTarget.ProfileRevision);
                RuntimeProcessingConfigurationAcknowledgement processing =
                    await _session.ApplyProcessingConfigurationAsync(
                        configuration,
                        _commandTimeout,
                        cancellationToken).ConfigureAwait(false);
                if (!processing.Accepted)
                    throw new RuntimeBackendStartException(
                        processing.ErrorCode ?? "runtime.processing.rejected",
                        binding.TargetInstanceId);
            }
            for (int index = 0; index < _performance.Count; index++)
            {
                _performanceStarted = index + 1;
                _performance[index].Start();
            }
        }
        catch (Exception startupFailure)
        {
            var rollbackFailures = new List<Exception>();
            for (int index = _performanceStarted - 1; index >= 0; index--)
            {
                try { await _performance[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { rollbackFailures.Add(exception); }
            }
            _performanceStarted = 0;
            try { await RollbackAsync().ConfigureAwait(false); }
            catch (Exception exception) { rollbackFailures.Add(exception); }
            _stop.Cancel();
            if (_eventPump is not null)
            {
                try { await _eventPump.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    rollbackFailures.Add(exception);
                }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException([startupFailure, .. rollbackFailures]);
            throw;
        }
    }

    public async ValueTask ApplyProfileAsync(
        RuntimeProfileBinding profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("runtime.backend.notStarted");

        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BoundTarget[] existing = _bindings
                .Where(item => item.Profile.ProfileId == profile.Profile.ProfileId)
                .ToArray();
            if (existing.Length != profile.Targets.Count ||
                existing.Select(item => item.Binding.TargetInstanceId).ToHashSet()
                    .SetEquals(profile.Targets.Select(item => item.TargetInstanceId)) is false)
            {
                throw new InvalidOperationException("runtime.profile.targetSetChanged");
            }

            var replacements = new List<BoundTarget>(profile.Targets.Count);
            foreach (RuntimeTargetBinding binding in profile.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BoundTarget current = existing.Single(item =>
                    item.Binding.TargetInstanceId == binding.TargetInstanceId);
                if (binding.ConfigurationRevision <= current.Binding.ConfigurationRevision ||
                    binding.ProfileTarget.TargetId != current.Binding.ProfileTarget.TargetId ||
                    binding.NativeHandle != current.Binding.NativeHandle ||
                    binding.DesktopRegion != current.Binding.DesktopRegion)
                {
                    throw new InvalidOperationException(
                        "runtime.profile.targetBindingChanged");
                }

                RuntimeProcessingConfiguration configuration =
                    ProfileRuntimeConfigurationFactory.Create(
                        binding.ProfileTarget,
                        binding.TargetInstanceId,
                        binding.ConfigurationRevision,
                        profile.Profile.ProfileId,
                        profile.ProfileRevision);
                RuntimeProcessingConfigurationAcknowledgement processing =
                    await _session.ApplyProcessingConfigurationAsync(
                        configuration,
                        _commandTimeout,
                        cancellationToken).ConfigureAwait(false);
                if (!processing.Accepted)
                    throw new EngineRuntimeCommandRejectedException(
                        "applyProfile",
                        processing.ErrorCode ?? "runtime.processing.rejected");

                await _pipeline.ReplaceAsync(
                    new RuntimeTranslationTarget(
                        _session.RuntimeEpoch,
                        binding.TargetInstanceId,
                        new CaptureTargetId(binding.ProfileTarget.TargetId),
                        profile.ProfileRevision,
                        profile.Profile,
                        binding.ProfileTarget,
                        binding.TargetPixelWidth,
                        binding.TargetPixelHeight,
                        binding.RunOptions,
                        _reducedMotion),
                    cancellationToken).ConfigureAwait(false);
                replacements.Add(new BoundTarget(
                    profile.Profile,
                    profile.ProfileRevision,
                    binding));
            }

            _bindings.RemoveAll(item =>
                item.Profile.ProfileId == profile.Profile.ProfileId);
            _bindings.AddRange(replacements);
            _active.RemoveAll(item =>
                item.Profile.ProfileId == profile.Profile.ProfileId);
            _active.AddRange(replacements);
            if (_pipeline is IReplayableRuntimeTranslationPipeline replayable)
            {
                RuntimeVisibleReplayResult replay = await replayable.ReplayVisibleAsync(
                    profile.Targets.Select(target => target.TargetInstanceId).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                lock (_budgetGate) _lastTranslationGroupReplay = replay;
            }
            else
            {
                lock (_budgetGate) _lastTranslationGroupReplay = new RuntimeVisibleReplayResult(
                    ReplayedTargetCount: 0,
                    WaitingTargetCount: profile.Targets.Count);
            }
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var failures = new List<Exception>();
        for (int index = _performance.Count - 1; index >= 0; index--)
        {
            try { await _performance[index].DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }
        _performanceStarted = 0;
        try { await RollbackAsync().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        _stop.Cancel();
        if (_eventPump is not null)
        {
            try { await _eventPump.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) { failures.Add(exception); }
        }
        try { await _pipeline.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        _stop.Dispose();
        _configurationGate.Dispose();
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private async ValueTask RollbackAsync()
    {
        var failures = new List<Exception>();
        for (int index = _active.Count - 1; index >= 0; index--)
        {
            RuntimeTargetBinding binding = _active[index].Binding;
            try { _pipeline.Unregister(binding.TargetInstanceId); }
            catch (Exception exception) { failures.Add(exception); }
            try {
                RuntimeCaptureTargetAcknowledgement removal =
                    await _session.ApplyCaptureTargetAsync(
                    CaptureCommand(binding, RuntimeCaptureTargetOperation.Remove),
                    _commandTimeout,
                    CancellationToken.None).ConfigureAwait(false);
                if (!removal.Accepted)
                    throw new RuntimeBackendStartException(
                        removal.ErrorCode ?? "runtime.capture.removeRejected",
                        binding.TargetInstanceId);
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        _active.Clear();
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static RuntimeCaptureTargetCommand CaptureCommand(
        RuntimeTargetBinding binding,
        RuntimeCaptureTargetOperation operation) => new(
            operation,
            operation == RuntimeCaptureTargetOperation.Upsert
                ? binding.CommandRevision
                : binding.CommandRevision + 1,
            new CaptureTargetId(binding.ProfileTarget.TargetId),
            binding.TargetInstanceId,
            RuntimeTargetBinding.ToRuntimeKind(binding.ProfileTarget.Kind),
            operation == RuntimeCaptureTargetOperation.Upsert ? binding.NativeHandle : 0,
            operation == RuntimeCaptureTargetOperation.Upsert ? binding.DesktopRegion : null);
}

public sealed class RuntimeBackendStartException : Exception
{
    public RuntimeBackendStartException(string errorCode, TargetInstanceId targetInstanceId)
        : base(errorCode)
    {
        if (string.IsNullOrEmpty(errorCode) || errorCode.Length > 128 ||
            errorCode.Any(character => character is not (>= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("Runtime start error code is invalid.", nameof(errorCode));
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ErrorCode = errorCode;
        TargetInstanceId = targetInstanceId;
    }

    public string ErrorCode { get; }
    public TargetInstanceId TargetInstanceId { get; }
}
