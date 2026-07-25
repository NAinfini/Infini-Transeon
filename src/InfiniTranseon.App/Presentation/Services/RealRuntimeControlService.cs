using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Storage;
using InfiniTranseon.Core.Diagnostics;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Raised when a profile cannot be turned into a runnable engine binding. Carries a stable
/// reason code (localized by the UI as <c>engine.start.{ReasonCode}</c>) plus the human detail
/// (target names, probe errors) shown verbatim.
/// </summary>
public sealed class EngineStartException : Exception
{
    public EngineStartException(string reasonCode, string detail)
        : base($"engine.start.{reasonCode}: {detail}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
        Detail = detail;
    }

    public string ReasonCode { get; }

    public string Detail { get; }

    public string LocalizationKey => $"engine.start.{ReasonCode}";
}

/// <summary>
/// Real engine runtime facade for the UI. Each start loads the profile document, resolves its
/// enabled capture targets against the live capture probe (window handles, monitor handles,
/// pixel sizes), builds the runtime profile binding, and launches a fresh one-shot
/// <see cref="EngineRuntimeService"/>. Every failure path is explicit: unknown profiles,
/// unmatched targets, a missing EngineHost executable (status carries the searched paths), and
/// protocol-unsupported operations all propagate typed errors to the caller.
/// </summary>
public sealed class RealRuntimeControlService : IRuntimeControlService, IAsyncDisposable
{
    /// <summary>Creates the one-shot engine for a resolved binding (test seam).</summary>
    public delegate IEngineRuntime EngineFactory(
        ProfileDocument profile,
        RuntimeProfileBinding binding,
        IRuntimeTranslationRecordSink? historySink,
        ApplicationSettings settings);

    private readonly ProfileRepository _profiles;
    private readonly ICaptureProbe _captureProbe;
    private readonly ISettingsService _settings;
    private readonly IBoundCredentialStore _credentials;
    private readonly RuntimeCapabilitiesService _capabilities;
    private readonly RuntimeStateStore _runtimeState;
    private readonly RuntimeEventHub _eventHub;
    private readonly AppDataOptions _options;
    private readonly EngineFactory _engineFactory;
    private readonly AppStatusLog? _statusLog;
    private readonly CustomRestAdapterStore? _customRestAdapters;
    private readonly LocalModelManagementService? _localModels;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Throttle for the Trace-level admission-rejected status event: logging every single rejection
    // would flood the JSONL log during a burst of stale results (e.g. right after a translator-group
    // switch floods the old channel run's in-flight results). Only the 1st, (N+1)th, (2N+1)th, ...
    // rejection is logged; the hub's per-reason counters (see RuntimeEventHub) remain exact.
    private const int RejectionLogInterval = 50;
    private int _rejectionLogCounter;

    // Set whenever a new engine instance is subscribed (fresh start or hot/cold restart) and cleared
    // once the first live OCR/translation event of that instance has told RuntimeStateStore to adopt
    // its runtime epoch. IEngineRuntime carries no epoch of its own; the epoch only exists inside the
    // tokens on OCR/translation/budget payloads, so it must be learned from the first such event.
    private bool _epochPending;

    private IEngineRuntime? _engine;
    private ProfileDocument? _activeProfile;
    private RuntimeProfileBinding? _activeBinding;
    private EngineRuntimeStatusChange? _lastChange;
    private bool _isPaused;
    private bool _isOverlayVisible = true;

    public RealRuntimeControlService(
        ProfileRepository profiles,
        ICaptureProbe captureProbe,
        ISettingsService settings,
        IBoundCredentialStore credentials,
        RuntimeCapabilitiesService capabilities,
        RuntimeStateStore runtimeState,
        RuntimeEventHub eventHub,
        AppDataOptions options,
        AppStatusLog? statusLog = null,
        CustomRestAdapterStore? customRestAdapters = null,
        LocalModelManagementService? localModels = null)
        : this(
            profiles,
            captureProbe,
            settings,
            credentials,
            capabilities,
            runtimeState,
            eventHub,
            options,
            engineFactory: null,
            statusLog: statusLog,
            customRestAdapters: customRestAdapters,
            localModels: localModels)
    {
    }

    // Test seam: inject a scripted engine factory instead of launching the native EngineHost.
    public RealRuntimeControlService(
        ProfileRepository profiles,
        ICaptureProbe captureProbe,
        ISettingsService settings,
        IBoundCredentialStore credentials,
        RuntimeCapabilitiesService capabilities,
        RuntimeStateStore runtimeState,
        RuntimeEventHub eventHub,
        AppDataOptions options,
        EngineFactory? engineFactory,
        AppStatusLog? statusLog = null,
        CustomRestAdapterStore? customRestAdapters = null,
        LocalModelManagementService? localModels = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(captureProbe);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(eventHub);
        ArgumentNullException.ThrowIfNull(options);
        _profiles = profiles;
        _captureProbe = captureProbe;
        _settings = settings;
        _credentials = credentials;
        _capabilities = capabilities;
        _runtimeState = runtimeState;
        _eventHub = eventHub;
        _options = options;
        _statusLog = statusLog;
        _customRestAdapters = customRestAdapters;
        _localModels = localModels;
        _engineFactory = engineFactory ?? ((_, binding, history, settings) =>
            EngineRuntimeComposition.CreateEngine(
                binding,
                _credentials,
                history,
                _options.DatabasePath,
                _customRestAdapters?.Load(),
                settings.PerformancePreset,
                settings.ReducedMotion,
                settings.EffectiveProviderEndpoints,
                _localModels,
                _options));
    }

    public EngineRuntimeStatus Status =>
        _engine?.Status ?? _lastChange?.Status ?? EngineRuntimeStatus.Stopped;

    public EngineRuntimeStatusChange? LastChange => _lastChange;

    public bool IsPaused => _isPaused;

    public bool IsOverlayVisible => _isOverlayVisible;

    public event EventHandler<EngineRuntimeStatusChange>? StatusChanged;

    public event EventHandler? TargetsChanged;

    public IReadOnlyList<RunningTarget> GetRunningTargets()
    {
        IEngineRuntime? engine = _engine;
        ProfileDocument? profile = _activeProfile;
        RuntimeProfileBinding? binding = _activeBinding;
        if (engine is null || profile is null || binding is null)
        {
            return [];
        }

        return engine.TargetSnapshots
            .Select(snapshot =>
            {
                RuntimeTargetBinding? bound = binding.Targets.FirstOrDefault(
                    target => target.TargetInstanceId == snapshot.TargetInstanceId);
                int regionCount = bound?.ProfileTarget.Regions.Count(region => region.Enabled) ?? 0;
                (string health, Controls.StatusSeverity severity) = Describe(snapshot.State);
                return new RunningTarget(
                    profile.Name,
                    bound?.ProfileTarget.Name ?? snapshot.TargetInstanceId.Value.ToString("n"),
                    health,
                    severity,
                    // Protocol v1 publishes no per-target latency metric; never fabricate one.
                    "—",
                    regionCount.ToString());
            })
            .ToArray();
    }

    public async Task StartAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null && _engine.Status is EngineRuntimeStatus.Running
                or EngineRuntimeStatus.Starting or EngineRuntimeStatus.Locating
                or EngineRuntimeStatus.Restarting or EngineRuntimeStatus.Stopping)
            {
                throw new EngineStartException("alreadyRunning", _activeProfile?.Name ?? string.Empty);
            }

            // EngineRuntimeService instances are one-shot; a previous stopped/faulted instance is
            // replaced by a fresh launch.
            await DisposeEngineAsync().ConfigureAwait(false);

            ProfileDocument profile =
                await _profiles.LoadAsync(profileId, cancellationToken).ConfigureAwait(false)
                ?? throw new EngineStartException("profileNotFound", profileId.ToString("D"));
            RuntimeProfileBinding binding =
                await ResolveBindingAsync(profile, cancellationToken).ConfigureAwait(false);
            ApplicationSettings applicationSettings =
                await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            IRuntimeTranslationRecordSink? historySink =
                CreateHistorySink(applicationSettings);

            IEngineRuntime engine = _engineFactory(
                profile,
                binding,
                historySink,
                applicationSettings);
            Subscribe(engine);
            _engine = engine;
            _activeProfile = profile;
            _activeBinding = binding;
            try
            {
                await engine.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The last status change (Faulted / ExecutableNotFound with searched paths) was
                // already captured through OnEngineStatusChanged before the throw.
                await DisposeEngineAsync().ConfigureAwait(false);
                throw;
            }

            _isPaused = false;
            _isOverlayVisible = true;
            TargetsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IEngineRuntime? engine = _engine;
            if (engine is null)
            {
                return;
            }
            await engine.StopAsync(cancellationToken).ConfigureAwait(false);
            await DisposeEngineAsync().ConfigureAwait(false);
            TargetsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        IEngineRuntime engine = RequireEngine();
        if (paused)
        {
            await engine.PauseAllAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await engine.ResumeAllAsync(cancellationToken).ConfigureAwait(false);
        }
        _isPaused = paused;
        _statusLog?.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "runtime.control",
            paused ? "runtime.control.paused" : "runtime.control.resumed",
            "status.runtime.control.pause",
            StatusEventSeverity.Information,
            new Dictionary<string, object?>
            {
                ["paused"] = paused,
            }));
        TargetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        IEngineRuntime engine = RequireEngine();
        await engine.SetOverlayVisibleAsync(visible, cancellationToken).ConfigureAwait(false);
        _isOverlayVisible = visible;
        _statusLog?.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "runtime.control",
            visible ? "runtime.overlay.shown" : "runtime.overlay.hidden",
            "status.runtime.control.overlay",
            StatusEventSeverity.Information,
            new Dictionary<string, object?>
            {
                ["visible"] = visible,
            }));
        TargetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RequestManualOcrAsync(CancellationToken cancellationToken = default)
    {
        IEngineRuntime engine = RequireEngine();
        try
        {
            await engine.RequestManualOcrAsync(cancellationToken).ConfigureAwait(false);
            _statusLog?.Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "runtime.control",
                "runtime.ocr.manual.scheduled",
                "status.runtime.control.manualOcr",
                StatusEventSeverity.Information,
                new Dictionary<string, object?>()));
        }
        catch (EngineRuntimeCommandRejectedException exception)
        {
            _statusLog?.Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "runtime.control",
                exception.ErrorCode,
                "status.runtime.control.manualOcrRejected",
                StatusEventSeverity.Warning,
                new Dictionary<string, object?>
                {
                    ["operation"] = exception.OperationKey,
                }));
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _statusLog?.Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "runtime.control",
                "runtime.ocr.manual.failed",
                "status.runtime.control.manualOcrFailed",
                StatusEventSeverity.Error,
                new Dictionary<string, object?>
                {
                    ["exceptionType"] = exception.GetType().Name,
                }));
            throw;
        }
    }

    public async Task<ProfileRuntimeApplyResult> ApplyProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProfileDocument updated =
                await _profiles.LoadAsync(profileId, cancellationToken).ConfigureAwait(false)
                ?? throw new EngineStartException("profileNotFound", profileId.ToString("D"));
            if (_engine is null ||
                _engine.Status != EngineRuntimeStatus.Running ||
                _activeProfile?.ProfileId != profileId ||
                _activeBinding is null)
            {
                return ProfileRuntimeApplyResult.SavedOnly;
            }

            if (_engine is IHotConfigurableEngineRuntime hot &&
                TryCreateHotBinding(updated, _activeBinding, out RuntimeProfileBinding? binding))
            {
                try
                {
                    await hot.ApplyProfileAsync(binding!, cancellationToken)
                        .ConfigureAwait(false);
                    _activeProfile = updated;
                    _activeBinding = binding;
                    _statusLog?.Record(new StatusEvent(
                        DateTimeOffset.UtcNow,
                        "runtime.configuration",
                        "runtime.configuration.hotApplied",
                        "status.runtime.configuration.hotApplied",
                        StatusEventSeverity.Information,
                        new Dictionary<string, object?>
                        {
                            ["profileId"] = profileId,
                            ["profileRevision"] = binding!.ProfileRevision,
                        }));
                    TargetsChanged?.Invoke(this, EventArgs.Empty);
                    return ProfileRuntimeApplyResult.HotApplied;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _statusLog?.Record(new StatusEvent(
                        DateTimeOffset.UtcNow,
                        "runtime.configuration",
                        "runtime.configuration.hotApplyFailed",
                        "status.runtime.configuration.restartFallback",
                        StatusEventSeverity.Warning,
                        new Dictionary<string, object?>
                        {
                            ["profileId"] = profileId,
                            ["exceptionType"] = exception.GetType().Name,
                        }));
                }
            }

            bool paused = _isPaused;
            bool overlayVisible = _isOverlayVisible;
            await DisposeEngineAsync().ConfigureAwait(false);
            RuntimeProfileBinding restartedBinding =
                await ResolveBindingAsync(updated, cancellationToken).ConfigureAwait(false);
            ApplicationSettings applicationSettings =
                await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            IRuntimeTranslationRecordSink? historySink =
                CreateHistorySink(applicationSettings);
            IEngineRuntime restarted = _engineFactory(
                updated,
                restartedBinding,
                historySink,
                applicationSettings);
            Subscribe(restarted);
            _engine = restarted;
            _activeProfile = updated;
            _activeBinding = restartedBinding;
            try
            {
                await restarted.StartAsync(cancellationToken).ConfigureAwait(false);
                if (paused)
                    await restarted.PauseAllAsync(cancellationToken).ConfigureAwait(false);
                if (!overlayVisible)
                    await restarted.SetOverlayVisibleAsync(
                        false,
                        cancellationToken).ConfigureAwait(false);
                _isPaused = paused;
                _isOverlayVisible = overlayVisible;
                TargetsChanged?.Invoke(this, EventArgs.Empty);
                return ProfileRuntimeApplyResult.Restarted;
            }
            catch
            {
                await DisposeEngineAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeThumbnail?> RequestThumbnailAsync(
        Guid targetId,
        int maximumLongEdge,
        CancellationToken cancellationToken = default)
    {
        if (targetId == Guid.Empty)
            throw new ArgumentException("Target ID cannot be empty.", nameof(targetId));
        IEngineRuntime? engine = _engine;
        RuntimeProfileBinding? binding = _activeBinding;
        if (engine is null ||
            engine.Status != EngineRuntimeStatus.Running ||
            binding is null)
            return null;
        RuntimeTargetBinding? target = binding.Targets.FirstOrDefault(item =>
            item.ProfileTarget.TargetId == targetId);
        if (target is null) return null;
        return await engine.RequestThumbnailAsync(
            target.TargetInstanceId,
            maximumLongEdge,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeEngineAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private IEngineRuntime RequireEngine() =>
        _engine ?? throw new InvalidOperationException("engine.runtime.notRunning");

    private async ValueTask DisposeEngineAsync()
    {
        IEngineRuntime? engine = _engine;
        _engine = null;
        _activeProfile = null;
        _activeBinding = null;
        _isPaused = false;
        _isOverlayVisible = true;
        if (engine is not null)
        {
            engine.StatusChanged -= OnEngineStatusChanged;
            engine.TargetsChanged -= OnEngineTargetsChanged;
            engine.OcrResultReceived -= OnEngineOcrResultReceived;
            engine.TranslationOutputReceived -= OnEngineTranslationOutputReceived;
            engine.BudgetUpdated -= OnEngineBudgetUpdated;
            engine.DiagnosticRaised -= OnEngineDiagnosticRaised;
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Subscribe(IEngineRuntime engine)
    {
        // A fresh engine instance means a fresh runtime epoch; the store adopts it from the first
        // live OCR/translation event so tokens still in flight from the previous instance are
        // rejected as stale rather than silently mixed into the new session's state.
        _epochPending = true;
        engine.StatusChanged += OnEngineStatusChanged;
        engine.TargetsChanged += OnEngineTargetsChanged;
        engine.OcrResultReceived += OnEngineOcrResultReceived;
        engine.TranslationOutputReceived += OnEngineTranslationOutputReceived;
        engine.BudgetUpdated += OnEngineBudgetUpdated;
        engine.DiagnosticRaised += OnEngineDiagnosticRaised;
    }

    private void OnEngineStatusChanged(object? sender, EngineRuntimeStatusChange change)
    {
        _lastChange = change;
        _statusLog?.Record(new StatusEvent(
            change.OccurredAtUtc,
            "runtime.engine",
            change.ErrorCode ?? $"engine.status.{change.Status.ToString().ToLowerInvariant()}",
            "status.runtime.engine.lifecycle",
            change.ErrorCode is null
                ? StatusEventSeverity.Information
                : StatusEventSeverity.Error,
            new Dictionary<string, object?>
            {
                ["state"] = change.Status,
                ["searchedPathCount"] = change.SearchedPaths.Count,
            }));
        StatusChanged?.Invoke(this, change);
    }

    private void OnEngineTargetsChanged(object? sender, EngineTargetSnapshotEvent target)
    {
        TargetLifecycleEvent lifecycle = target.Lifecycle;
        _statusLog?.Record(new StatusEvent(
            lifecycle.OccurredAtUtc,
            "runtime.capture",
            lifecycle.ErrorCode ?? $"capture.state.{lifecycle.Target.State}",
            "status.runtime.capture.lifecycle",
            lifecycle.ErrorCode is null
                ? StatusEventSeverity.Information
                : StatusEventSeverity.Warning,
            new Dictionary<string, object?>
            {
                ["targetId"] = lifecycle.Target.TargetId.Value,
                ["targetInstanceId"] = lifecycle.Target.TargetInstanceId.Value,
                ["state"] = lifecycle.Target.State,
                ["lifecycleSequence"] = lifecycle.LifecycleSequence,
                ["pixelWidth"] = lifecycle.Target.PixelWidth,
                ["pixelHeight"] = lifecycle.Target.PixelHeight,
                ["dpi"] = lifecycle.Target.Dpi,
                ["nativeErrorCode"] = lifecycle.NativeErrorCode,
            }));
        TargetsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEngineBudgetUpdated(object? sender, EngineBudgetEvent budget)
    {
        _capabilities.UpdateBudget(budget.Snapshot);
        double peakUtilization = budget.Snapshot.Pools
            .Where(pool => pool.Limit > 0)
            .Select(pool => (pool.Committed + pool.Reserved) / (double)pool.Limit)
            .DefaultIfEmpty(0)
            .Max();
        _statusLog?.Record(new StatusEvent(
            budget.Snapshot.CapturedAtUtc,
            "runtime.performance",
            "runtime.budget.updated",
            "status.runtime.performance.budget",
            StatusEventSeverity.Information,
            new Dictionary<string, object?>
            {
                ["runtimeEpoch"] = budget.Snapshot.RuntimeEpoch,
                ["snapshotRevision"] = budget.Snapshot.SnapshotRevision,
                ["poolCount"] = budget.Snapshot.Pools.Count,
                ["peakUtilization"] = peakUtilization,
            }));
    }

    private void OnEngineDiagnosticRaised(object? sender, EngineDiagnostic diagnostic)
    {
        _statusLog?.Record(new StatusEvent(
            diagnostic.OccurredAtUtc,
            "runtime.diagnostic",
            diagnostic.ErrorCode,
            "status.runtime.diagnostic",
            diagnostic.Severity >= RuntimeDiagnosticSeverity.Error
                ? StatusEventSeverity.Error
                : StatusEventSeverity.Warning,
            new Dictionary<string, object?>()));
        // Persistent JSONL logging above is unchanged; this additionally pushes the diagnostic onto
        // the in-memory hub so the (future) Activity page can render it without polling the log file.
        _eventHub.PublishDiagnosticRaised(new RuntimeDiagnosticRaised(
            diagnostic.ErrorCode,
            diagnostic.MessageKey,
            diagnostic.Severity,
            diagnostic.OccurredAtUtc));
    }

    private void OnEngineOcrResultReceived(object? sender, EngineOcrResultEvent ocrEvent)
    {
        OcrResultSnapshot result = ocrEvent.Result;
        SourceGenerationToken source = result.ExecutionToken.Source;
        AdvanceEpochIfPending(source.RuntimeEpoch);

        RuntimeStateAdmission admission = _runtimeState.AdmitSourceGeneration(source);
        if (admission == RuntimeStateAdmission.Accepted)
        {
            _eventHub.PublishOcrRecognized(new LiveOcrRecognized(
                source.RuntimeEpoch,
                source.TargetInstanceId,
                source.Area,
                source.TextTrackId,
                source.SourceGeneration,
                source.ProfileRevision,
                result.Lines,
                result.ModelId,
                result.ModelVersion,
                result.IsStable,
                result.TerminalErrorCode,
                ocrEvent.OccurredAtUtc));
        }
        else
        {
            RecordAdmissionRejection(admission, "ocr");
        }
    }

    private void OnEngineTranslationOutputReceived(
        object? sender, EngineTranslationOutputEvent translationEvent)
    {
        TranslationOutput output = translationEvent.Output;
        ChannelExecutionToken channel = output.ExecutionToken.Channel;
        SourceGenerationToken source = channel.Source;
        AdvanceEpochIfPending(source.RuntimeEpoch);

        RuntimeStateAdmission admission = _runtimeState.AdmitTranslation(output);
        if (admission == RuntimeStateAdmission.Accepted)
        {
            _eventHub.PublishTranslationReceived(new LiveTranslationReceived(
                source.RuntimeEpoch,
                translationEvent.ProfileId,
                source.TargetInstanceId,
                source.Area,
                source.TextTrackId,
                output.ChannelId,
                channel.ChannelRunId,
                output.ImmutableSlotId,
                output.StageId,
                output.StageIndex,
                output.Attempt,
                output.Stage,
                output.Text,
                output.ProviderId,
                output.Latency,
                output.EstimatedCost,
                output.CostCurrency,
                output.CacheHit,
                output.StreamCompleted,
                output.FallbackFromProviderId,
                output.TerminalErrorCode,
                output.SupersededReason,
                translationEvent.OccurredAtUtc));
        }
        else
        {
            RecordAdmissionRejection(admission, "translation");
        }
    }

    private void AdvanceEpochIfPending(Guid runtimeEpoch)
    {
        if (!_epochPending)
        {
            return;
        }

        _runtimeState.AdvanceEpoch(runtimeEpoch);
        _epochPending = false;
    }

    private void RecordAdmissionRejection(RuntimeStateAdmission admission, string source)
    {
        _eventHub.RecordAdmissionRejected(admission);

        // Log at most one Trace status event per RejectionLogInterval rejections (this counts
        // rejections across both OCR and translation admission, which is deliberate: the point of
        // the throttle is to keep the JSONL log readable, not to log per-source-type bursts).
        int count = Interlocked.Increment(ref _rejectionLogCounter);
        if ((count - 1) % RejectionLogInterval != 0)
        {
            return;
        }

        _statusLog?.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "runtime.state",
            $"runtime.state.{admission}",
            "status.runtime.state.admissionRejected",
            StatusEventSeverity.Trace,
            new Dictionary<string, object?>
            {
                ["source"] = new StatusIdentifier(source),
                ["rejectionCount"] = count,
            }));
    }

    private IRuntimeTranslationRecordSink? CreateHistorySink(
        ApplicationSettings settings)
    {
        HistoryOptions historyOptions = settings.HistoryRetention switch
        {
            HistoryRetention.Days30 => new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(30)),
            HistoryRetention.Days90 => new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(90)),
            _ => new HistoryOptions(Enabled: false),
        };
        if (!historyOptions.Enabled)
        {
            return null;
        }
        return new RuntimeTranslationRecordSink(
            new RecentTranslationBuffer(),
            new HistoryRepository(_options.DatabasePath, historyOptions));
    }

    private async Task<RuntimeProfileBinding> ResolveBindingAsync(
        ProfileDocument profile,
        CancellationToken cancellationToken)
    {
        ProfileTarget[] enabled = profile.Targets.Where(target => target.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            throw new EngineStartException("noEnabledTargets", profile.Name);
        }

        CaptureProbeResult probe = await _captureProbe
            .ProbeAsync(new CaptureProbeRequest(NameFilter: null), cancellationToken)
            .ConfigureAwait(false);
        Contracts.Translation.GlossaryEntry[] glossary = ProfileDocumentData
            .ReadGlossary(profile)
            .Select(entry => new Contracts.Translation.GlossaryEntry(entry.SourceTerm, entry.TargetTerm))
            .ToArray();
        var runOptions = ProfileTranslationFactory.CreateRunOptions(
            profile,
            scene: null,
            speaker: null,
            recentSource: [],
            recentTranslation: [],
            EngineRuntimeComposition.AttemptTimeout,
            EngineRuntimeComposition.MaximumOutputCharacters,
            EngineRuntimeComposition.MaximumOutputTokens,
            glossary);

        long commandRevision = 1;
        var bindings = new List<RuntimeTargetBinding>(enabled.Length);
        foreach (ProfileTarget target in enabled)
        {
            if (target.Kind == CaptureTargetKind.DesktopFixedRegion)
            {
                OverlayPixelRect region = target.DesktopRegion ??
                    throw new EngineStartException("desktopRegionRequired", target.Name);
                bindings.Add(new RuntimeTargetBinding(
                    target,
                    new TargetInstanceId(Guid.NewGuid()),
                    nativeHandle: 0,
                    desktopRegion: region,
                    region.Width,
                    region.Height,
                    commandRevision++,
                    configurationRevision: 1,
                    runOptions));
                continue;
            }

            CaptureProbeTarget resolved = ResolveTarget(target, probe.Targets);
            bindings.Add(new RuntimeTargetBinding(
                target,
                new TargetInstanceId(Guid.NewGuid()),
                resolved.NativeHandle,
                desktopRegion: null,
                resolved.PixelWidth,
                resolved.PixelHeight,
                commandRevision++,
                configurationRevision: 1,
                runOptions));
        }

        return new RuntimeProfileBinding(profile, profileRevision: 1, bindings);
    }

    private static bool TryCreateHotBinding(
        ProfileDocument profile,
        RuntimeProfileBinding current,
        out RuntimeProfileBinding? updated)
    {
        ProfileTarget[] enabled = profile.Targets.Where(target => target.Enabled).ToArray();
        if (profile.ProfileId != current.Profile.ProfileId ||
            enabled.Length != current.Targets.Count ||
            !enabled.Select(target => target.TargetId).ToHashSet()
                .SetEquals(current.Targets.Select(target => target.ProfileTarget.TargetId)))
        {
            updated = null;
            return false;
        }

        Contracts.Translation.GlossaryEntry[] glossary = ProfileDocumentData
            .ReadGlossary(profile)
            .Select(entry => new Contracts.Translation.GlossaryEntry(
                entry.SourceTerm,
                entry.TargetTerm))
            .ToArray();
        TranslationRunOptions runOptions =
            ProfileTranslationFactory.CreateRunOptions(
                profile,
                scene: null,
                speaker: null,
                recentSource: [],
                recentTranslation: [],
                EngineRuntimeComposition.AttemptTimeout,
                EngineRuntimeComposition.MaximumOutputCharacters,
                EngineRuntimeComposition.MaximumOutputTokens,
                glossary);
        long profileRevision = checked(current.ProfileRevision + 1);
        var targets = new List<RuntimeTargetBinding>(enabled.Length);
        foreach (ProfileTarget target in enabled)
        {
            RuntimeTargetBinding existing = current.Targets.Single(item =>
                item.ProfileTarget.TargetId == target.TargetId);
            if (target.Kind != existing.ProfileTarget.Kind ||
                target.MachineBinding != existing.ProfileTarget.MachineBinding)
            {
                updated = null;
                return false;
            }
            targets.Add(new RuntimeTargetBinding(
                target,
                existing.TargetInstanceId,
                existing.NativeHandle,
                existing.DesktopRegion,
                existing.TargetPixelWidth,
                existing.TargetPixelHeight,
                existing.CommandRevision,
                checked(existing.ConfigurationRevision + 1),
                runOptions));
        }
        updated = new RuntimeProfileBinding(profile, profileRevision, targets);
        return true;
    }

    private static CaptureProbeTarget ResolveTarget(
        ProfileTarget target,
        IReadOnlyList<CaptureProbeTarget> candidates)
    {
        switch (target.Kind)
        {
            case CaptureTargetKind.Window:
            {
                CaptureProbeTarget[] windows = candidates
                    .Where(candidate =>
                        string.Equals(candidate.Kind, "Window", StringComparison.OrdinalIgnoreCase) &&
                        candidate.Capturable && candidate.NativeHandle != 0)
                    .ToArray();
                string wanted = target.MachineBinding?.WindowTitle is { Length: > 0 } title
                    ? title
                    : target.Name;
                CaptureProbeTarget? match =
                    windows.FirstOrDefault(candidate => string.Equals(
                        candidate.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)) ??
                    windows.FirstOrDefault(candidate => candidate.DisplayName.Contains(
                        wanted, StringComparison.OrdinalIgnoreCase));
                return match ?? throw new EngineStartException("targetNotFound", wanted);
            }
            case CaptureTargetKind.Display:
            {
                CaptureProbeTarget[] displays = candidates
                    .Where(candidate =>
                        (string.Equals(candidate.Kind, "Display", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(candidate.Kind, "Monitor", StringComparison.OrdinalIgnoreCase)) &&
                        candidate.Capturable && candidate.NativeHandle != 0)
                    .ToArray();
                CaptureProbeTarget? match =
                    displays.FirstOrDefault(candidate => candidate.DisplayName.Contains(
                        target.Name, StringComparison.OrdinalIgnoreCase)) ??
                    (displays.Length == 1 ? displays[0] : null);
                return match ?? throw new EngineStartException("targetNotFound", target.Name);
            }
            default:
                throw new EngineStartException("targetKindUnsupported", target.Kind.ToString());
        }
    }

    private static (string Health, Controls.StatusSeverity Severity) Describe(
        TargetLifecycleState state) => state switch
    {
        TargetLifecycleState.Running or TargetLifecycleState.RunningWithCaptureBorder =>
            (nameof(TargetLifecycleState.Running), Controls.StatusSeverity.Success),
        TargetLifecycleState.Available or TargetLifecycleState.WaitingForMatch =>
            (state.ToString(), Controls.StatusSeverity.Info),
        TargetLifecycleState.DeviceLost or TargetLifecycleState.Closed =>
            (state.ToString(), Controls.StatusSeverity.Critical),
        _ => (state.ToString(), Controls.StatusSeverity.Warning),
    };
}
