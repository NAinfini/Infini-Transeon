using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Storage;

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
        IRuntimeTranslationRecordSink? historySink);

    private readonly ProfileRepository _profiles;
    private readonly ICaptureProbe _captureProbe;
    private readonly ISettingsService _settings;
    private readonly IBoundCredentialStore _credentials;
    private readonly IRuntimeCapabilitiesService _capabilities;
    private readonly AppDataOptions _options;
    private readonly EngineFactory _engineFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        IRuntimeCapabilitiesService capabilities,
        AppDataOptions options)
        : this(profiles, captureProbe, settings, credentials, capabilities, options, engineFactory: null)
    {
    }

    // Test seam: inject a scripted engine factory instead of launching the native EngineHost.
    public RealRuntimeControlService(
        ProfileRepository profiles,
        ICaptureProbe captureProbe,
        ISettingsService settings,
        IBoundCredentialStore credentials,
        IRuntimeCapabilitiesService capabilities,
        AppDataOptions options,
        EngineFactory? engineFactory)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(captureProbe);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(options);
        _profiles = profiles;
        _captureProbe = captureProbe;
        _settings = settings;
        _credentials = credentials;
        _capabilities = capabilities;
        _options = options;
        _engineFactory = engineFactory ?? ((_, binding, history) =>
            EngineRuntimeComposition.CreateEngine(binding, _credentials, history));
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
            IRuntimeTranslationRecordSink? historySink =
                await CreateHistorySinkAsync(cancellationToken).ConfigureAwait(false);

            IEngineRuntime engine = _engineFactory(profile, binding, historySink);
            engine.StatusChanged += OnEngineStatusChanged;
            engine.TargetsChanged += OnEngineTargetsChanged;
            engine.BudgetUpdated += OnEngineBudgetUpdated;
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
    }

    public async Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        IEngineRuntime engine = RequireEngine();
        await engine.SetOverlayVisibleAsync(visible, cancellationToken).ConfigureAwait(false);
        _isOverlayVisible = visible;
    }

    public async Task RequestManualOcrAsync(CancellationToken cancellationToken = default)
    {
        IEngineRuntime engine = RequireEngine();
        // Protocol v1 has no manual-OCR message; this propagates the typed unsupported
        // exception so the UI can disable the affordance with an honest reason.
        await engine.RequestManualOcrAsync(cancellationToken).ConfigureAwait(false);
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
            engine.BudgetUpdated -= OnEngineBudgetUpdated;
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnEngineStatusChanged(object? sender, EngineRuntimeStatusChange change)
    {
        _lastChange = change;
        StatusChanged?.Invoke(this, change);
    }

    private void OnEngineTargetsChanged(object? sender, EngineTargetSnapshotEvent _) =>
        TargetsChanged?.Invoke(this, EventArgs.Empty);

    private void OnEngineBudgetUpdated(object? sender, EngineBudgetEvent budget) =>
        _capabilities.UpdateBudget(budget.Snapshot);

    private async Task<IRuntimeTranslationRecordSink?> CreateHistorySinkAsync(
        CancellationToken cancellationToken)
    {
        ApplicationSettings settings =
            await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
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
                        string.Equals(candidate.Kind, "Display", StringComparison.OrdinalIgnoreCase) &&
                        candidate.Capturable && candidate.NativeHandle != 0)
                    .ToArray();
                CaptureProbeTarget? match =
                    displays.FirstOrDefault(candidate => candidate.DisplayName.Contains(
                        target.Name, StringComparison.OrdinalIgnoreCase)) ??
                    (displays.Length == 1 ? displays[0] : null);
                return match ?? throw new EngineStartException("targetNotFound", target.Name);
            }
            default:
                // Profiles carry no desktop pixel rectangle yet, so a fixed desktop region cannot
                // be bound honestly; fail explicitly rather than capture a guessed area.
                throw new EngineStartException("desktopRegionUnsupported", target.Name);
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
