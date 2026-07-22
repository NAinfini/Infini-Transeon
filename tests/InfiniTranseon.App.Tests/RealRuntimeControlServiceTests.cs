using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Storage;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// Exercises the real UI runtime facade end-to-end against a real SQLite profile store and a
/// scripted engine, so target resolution, one-shot engine replacement, and every explicit
/// failure reason code are covered without launching the native EngineHost.
/// </summary>
public sealed class RealRuntimeControlServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "infini-transeon-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; leftover temp files must never fail the test run.
        }
    }

    private static CaptureProbeTarget Window(string name, ulong handle = 0x1234) =>
        new(new CaptureTargetId(Guid.NewGuid()), name, "Window", 1920, 1080, 96, Capturable: true, ErrorCode: null)
        {
            NativeHandle = handle,
        };

    private async Task<(RealRuntimeControlService Service, Guid ProfileId, ScriptedEngine Engine)> BuildAsync(
        IReadOnlyList<CaptureProbeTarget> probeTargets,
        CancellationToken ct,
        string targetKind = "Window",
        bool failStart = false)
    {
        var options = new AppDataOptions(_root);
        options.EnsureRootExists();
        var repository = new ProfileRepository(options.DatabasePath);
        var profileService = new RealProfileService(repository, options.DatabasePath);
        Guid profileId = await profileService.SaveAsync(
            new ProfileEditModel(
                Guid.Empty, "Visual novel", "ja", "zh-Hans", Guid.NewGuid(), "Notepad", targetKind,
                "1920x1080", "translation.deepl",
                [new ProfileRegionDraft("Dialogue", RegionPriorityLevel.P0)]),
            ct);

        var engine = new ScriptedEngine { FailStart = failStart };
        var service = new RealRuntimeControlService(
            repository,
            new ScriptedCaptureProbe(probeTargets),
            new FakeSettingsService(),
            new InMemoryCredentialStore(),
            new RuntimeCapabilitiesService(),
            options,
            (_, binding, _) =>
            {
                engine.Binding = binding;
                return engine;
            });
        return (service, profileId, engine);
    }

    [Fact]
    public async Task Start_resolves_window_target_and_reports_running_targets()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, ScriptedEngine engine) =
            await BuildAsync([Window("Notepad — untitled", handle: 0xBEEF)], ct);
        await using (service)
        {
            await service.StartAsync(profileId, ct);

            Assert.Equal(EngineRuntimeStatus.Running, service.Status);
            Assert.NotNull(engine.Binding);
            RuntimeTargetBinding bound = Assert.Single(engine.Binding!.Targets);
            Assert.Equal(0xBEEFUL, bound.NativeHandle);
            Assert.Equal(1920, bound.TargetPixelWidth);
            Assert.Equal(1080, bound.TargetPixelHeight);

            RunningTarget target = Assert.Single(service.GetRunningTargets());
            Assert.Equal("Visual novel", target.ProfileName);
            Assert.Equal("Notepad", target.WindowTitle);
            // Protocol v1 publishes no latency metric; the value must be the placeholder, never a number.
            Assert.Equal("—", target.LatencyP95);

            await service.SetPausedAsync(true, ct);
            Assert.True(service.IsPaused);
            Assert.True(engine.Paused);

            await Assert.ThrowsAsync<EngineRuntimeUnsupportedOperationException>(
                () => service.RequestManualOcrAsync(ct));

            await service.StopAsync(ct);
            Assert.Equal(EngineRuntimeStatus.Stopped, service.Status);
            Assert.Empty(service.GetRunningTargets());
            Assert.True(engine.Disposed);
        }
    }

    [Fact]
    public async Task Start_rejects_unknown_profile_with_stable_reason_code()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, _, _) = await BuildAsync([Window("Notepad")], ct);
        await using (service)
        {
            EngineStartException error = await Assert.ThrowsAsync<EngineStartException>(
                () => service.StartAsync(Guid.NewGuid(), ct));
            Assert.Equal("profileNotFound", error.ReasonCode);
            Assert.Equal("engine.start.profileNotFound", error.LocalizationKey);
        }
    }

    [Fact]
    public async Task Start_rejects_unmatched_window_with_the_wanted_title_in_detail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, _) =
            await BuildAsync([Window("Calculator")], ct);
        await using (service)
        {
            EngineStartException error = await Assert.ThrowsAsync<EngineStartException>(
                () => service.StartAsync(profileId, ct));
            Assert.Equal("targetNotFound", error.ReasonCode);
            Assert.Equal("Notepad", error.Detail);
            Assert.Equal(EngineRuntimeStatus.Stopped, service.Status);
        }
    }

    [Fact]
    public async Task Start_rejects_desktop_region_targets_explicitly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, _) =
            await BuildAsync([Window("Notepad")], ct, targetKind: "DesktopFixedRegion");
        await using (service)
        {
            EngineStartException error = await Assert.ThrowsAsync<EngineStartException>(
                () => service.StartAsync(profileId, ct));
            Assert.Equal("desktopRegionUnsupported", error.ReasonCode);
        }
    }

    [Fact]
    public async Task Start_rejects_second_start_while_engine_is_running()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, _) =
            await BuildAsync([Window("Notepad")], ct);
        await using (service)
        {
            await service.StartAsync(profileId, ct);
            EngineStartException error = await Assert.ThrowsAsync<EngineStartException>(
                () => service.StartAsync(profileId, ct));
            Assert.Equal("alreadyRunning", error.ReasonCode);
        }
    }

    [Fact]
    public async Task Failed_launch_keeps_executable_not_found_status_and_allows_a_fresh_start()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, ScriptedEngine engine) =
            await BuildAsync([Window("Notepad")], ct, failStart: true);
        await using (service)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(profileId, ct));

            // The last transition (with the searched paths) must survive engine disposal so the
            // UI banner can show what was probed.
            Assert.Equal(EngineRuntimeStatus.ExecutableNotFound, service.Status);
            Assert.NotNull(service.LastChange);
            Assert.Contains(@"C:\probed\EngineHost.exe", service.LastChange!.SearchedPaths);
            Assert.True(engine.Disposed);
            Assert.Empty(service.GetRunningTargets());

            // A fresh start must be possible: the one-shot faulted instance is replaced.
            engine.FailStart = false;
            await service.StartAsync(profileId, ct);
            Assert.Equal(EngineRuntimeStatus.Running, service.Status);
        }
    }

    private sealed class ScriptedCaptureProbe(IReadOnlyList<CaptureProbeTarget> targets) : ICaptureProbe
    {
        public ValueTask<CaptureProbeResult> ProbeAsync(
            CaptureProbeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CaptureProbeResult(targets));
    }

    private sealed class InMemoryCredentialStore : IBoundCredentialStore
    {
        private readonly Dictionary<string, (string Secret, CredentialBinding Binding)> _store = [];

        public ValueTask WriteAsync(
            string reference, string secret, CredentialBinding binding, CancellationToken cancellationToken)
        {
            _store[reference] = (secret, binding);
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReadAsync(
            string reference, CredentialBinding expectedBinding, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_store.TryGetValue(reference, out var entry) ? entry.Secret : null);

        public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken)
        {
            _store.Remove(reference);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Deterministic engine double that mirrors the real facade's event ordering.</summary>
    private sealed class ScriptedEngine : IEngineRuntime
    {
        private readonly List<TargetSnapshot> _snapshots = [];

        public RuntimeProfileBinding? Binding { get; set; }

        public bool FailStart { get; set; }

        public bool Paused { get; private set; }

        public bool Disposed { get; private set; }

        public EngineRuntimeStatus Status { get; private set; } = EngineRuntimeStatus.Stopped;

        public IReadOnlyList<TargetSnapshot> TargetSnapshots => _snapshots;

        public event EventHandler<EngineRuntimeStatusChange>? StatusChanged;

        public event EventHandler<EngineTargetSnapshotEvent>? TargetsChanged;

        public event EventHandler<EngineOcrResultEvent>? OcrResultReceived;

        public event EventHandler<EngineTranslationOutputEvent>? TranslationOutputReceived;

        public event EventHandler<EngineBudgetEvent>? BudgetUpdated;

        public event EventHandler<EngineDiagnostic>? DiagnosticRaised;

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            Transition(EngineRuntimeStatus.Locating);
            if (FailStart)
            {
                Status = EngineRuntimeStatus.ExecutableNotFound;
                StatusChanged?.Invoke(this, new EngineRuntimeStatusChange(
                    EngineRuntimeStatus.ExecutableNotFound,
                    DateTimeOffset.UtcNow,
                    "engine.runtime.executableNotFound",
                    [@"C:\probed\EngineHost.exe"]));
                throw new InvalidOperationException("engine.runtime.executableNotFound");
            }
            Transition(EngineRuntimeStatus.Starting);
            foreach (RuntimeTargetBinding target in Binding?.Targets ?? [])
            {
                _snapshots.Add(new TargetSnapshot(
                    target.TargetInstanceId,
                    new CaptureTargetId(target.ProfileTarget.TargetId),
                    TargetLifecycleState.Running,
                    target.TargetPixelWidth,
                    target.TargetPixelHeight,
                    96));
            }
            Transition(EngineRuntimeStatus.Running);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Transition(EngineRuntimeStatus.Stopping);
            _snapshots.Clear();
            Transition(EngineRuntimeStatus.Stopped);
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAllAsync(CancellationToken cancellationToken)
        {
            Paused = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAllAsync(CancellationToken cancellationToken)
        {
            Paused = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask RequestManualOcrAsync(CancellationToken cancellationToken) =>
            throw new EngineRuntimeUnsupportedOperationException("manualOcr");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private void Transition(EngineRuntimeStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, new EngineRuntimeStatusChange(status, DateTimeOffset.UtcNow));
            // Touch the unused events so the compiler treats the interface as fully implemented.
            _ = TargetsChanged;
            _ = OcrResultReceived;
            _ = TranslationOutputReceived;
            _ = BudgetUpdated;
            _ = DiagnosticRaised;
        }
    }
}
