using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.State;
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
        OverlayPixelRect? desktopRegion = null,
        bool failStart = false)
    {
        (RealRuntimeControlService service, Guid profileId, ScriptedEngine engine, _, _) =
            await BuildWithRuntimeAsync(probeTargets, ct, targetKind, desktopRegion, failStart);
        return (service, profileId, engine);
    }

    /// <summary>Like <see cref="BuildAsync"/> but also exposes the RuntimeStateStore/RuntimeEventHub
    /// instances wired into the service, for tests that assert on the C2 live-event data path.</summary>
    private async Task<(
        RealRuntimeControlService Service,
        Guid ProfileId,
        ScriptedEngine Engine,
        RuntimeStateStore RuntimeState,
        RuntimeEventHub EventHub)> BuildWithRuntimeAsync(
        IReadOnlyList<CaptureProbeTarget> probeTargets,
        CancellationToken ct,
        string targetKind = "Window",
        OverlayPixelRect? desktopRegion = null,
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
                [new ProfileRegionDraft("Dialogue", RegionPriorityLevel.P0)],
                desktopRegion),
            ct);

        var engine = new ScriptedEngine { FailStart = failStart };
        var runtimeState = new RuntimeStateStore();
        var eventHub = new RuntimeEventHub();
        var service = new RealRuntimeControlService(
            repository,
            new ScriptedCaptureProbe(probeTargets),
            new FakeSettingsService(),
            new InMemoryCredentialStore(),
            new RuntimeCapabilitiesService(),
            runtimeState,
            eventHub,
            options,
            (_, binding, _, _) =>
            {
                engine.Binding = binding;
                return engine;
            });
        return (service, profileId, engine, runtimeState, eventHub);
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

            await service.RequestManualOcrAsync(ct);
            Assert.Equal(1, engine.ManualOcrRequests);

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
    public async Task Start_binds_desktop_region_without_a_native_handle()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var region = new OverlayPixelRect(-1920, 120, 1280, 720);
        (RealRuntimeControlService service, Guid profileId, ScriptedEngine engine) =
            await BuildAsync([], ct, targetKind: "DesktopFixedRegion", desktopRegion: region);
        await using (service)
        {
            await service.StartAsync(profileId, ct);

            RuntimeTargetBinding binding = Assert.Single(engine.Binding!.Targets);
            Assert.Equal(0UL, binding.NativeHandle);
            Assert.Equal(region, binding.DesktopRegion);
            Assert.Equal(1280, binding.TargetPixelWidth);
            Assert.Equal(720, binding.TargetPixelHeight);
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

    // --- C2: engine live events -> RuntimeStateStore admission -> RuntimeEventHub -----------------

    private static readonly TargetInstanceId LiveTarget =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TextTrackId LiveTrack =
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly TranslationChannelId LiveChannel =
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly Guid LiveSlot = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LiveStage = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static SourceGenerationToken LiveSource(
        Guid epoch, long generation, long profileRevision = 1) =>
        new(epoch, LiveTarget, CaptureAreaKey.FullTarget, LiveTrack, generation, profileRevision);

    private static EngineOcrResultEvent LiveOcrEvent(SourceGenerationToken source, long resultSequence = 1) =>
        new(
            new OcrResultSnapshot(
                new OcrExecutionToken(source, Guid.NewGuid(), 1, resultSequence),
                [new TextLine("こんにちは", new NormalizedRect(0, 0, 0.5, 0.2), 0.97)],
                "paddle-ocr",
                "1.0",
                true,
                null),
            DateTimeOffset.UtcNow);

    private static EngineTranslationOutputEvent LiveTranslationEvent(
        Guid profileId, ChannelExecutionToken channel, int stageSequence, int attempt, long streamSequence, string text) =>
        new(
            profileId,
            new TranslationOutput(
                LiveChannel,
                new StageExecutionToken(channel, LiveStage, stageSequence, attempt, streamSequence),
                channel.ImmutableSlotId,
                LiveStage,
                0,
                attempt,
                TranslationStage.Initial,
                text,
                "deepl",
                TimeSpan.FromMilliseconds(120),
                null,
                null,
                false,
                true,
                null,
                null,
                null),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task Accepted_translation_flows_from_engine_event_through_admission_into_the_hub()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, ScriptedEngine engine, _, RuntimeEventHub hub) =
            await BuildWithRuntimeAsync([Window("Notepad")], ct);
        await using (service)
        {
            await service.StartAsync(profileId, ct);

            LiveTranslationReceived? received = null;
            hub.TranslationReceived += (_, payload) => received = payload;

            Guid epoch = Guid.NewGuid();
            SourceGenerationToken source = LiveSource(epoch, generation: 1);
            ChannelExecutionToken channel = new(source, LiveChannel, Guid.NewGuid(), LiveSlot);

            // The translation pipeline only ever runs against a source generation the OCR stream
            // already established; wire the OCR event through first, as the real engine would.
            engine.RaiseOcrResult(LiveOcrEvent(source));
            engine.RaiseTranslationOutput(
                LiveTranslationEvent(profileId, channel, stageSequence: 1, attempt: 1, streamSequence: 1, "Hello"));

            Assert.NotNull(received);
            Assert.Equal("Hello", received!.Text);
            Assert.Equal(profileId, received.ProfileId);
            Assert.Equal(epoch, received.RuntimeEpoch);
            Assert.Equal(LiveTarget, received.TargetInstanceId);
            Assert.Equal(LiveTrack, received.TextTrackId);
            Assert.Equal(channel.ChannelRunId, received.ChannelRunId);
            Assert.Equal(0, hub.TotalAdmissionRejectedCount);

            IReadOnlyList<RuntimeHubEvent> snapshot = hub.Snapshot();
            Assert.Equal(2, snapshot.Count);
            Assert.Equal(RuntimeHubEventKind.OcrRecognized, snapshot[0].Kind);
            Assert.Equal(RuntimeHubEventKind.TranslationReceived, snapshot[1].Kind);
        }
    }

    [Fact]
    public async Task Stale_translation_from_a_superseded_generation_is_rejected_and_counted_not_published()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, ScriptedEngine engine, _, RuntimeEventHub hub) =
            await BuildWithRuntimeAsync([Window("Notepad")], ct);
        await using (service)
        {
            await service.StartAsync(profileId, ct);

            var receivedTranslations = new List<LiveTranslationReceived>();
            hub.TranslationReceived += (_, payload) => receivedTranslations.Add(payload);

            Guid epoch = Guid.NewGuid();
            SourceGenerationToken oldSource = LiveSource(epoch, generation: 1);
            ChannelExecutionToken oldChannel = new(oldSource, LiveChannel, Guid.NewGuid(), LiveSlot);

            engine.RaiseOcrResult(LiveOcrEvent(oldSource));
            engine.RaiseTranslationOutput(
                LiveTranslationEvent(profileId, oldChannel, stageSequence: 1, attempt: 1, streamSequence: 1, "Hello"));

            // A newer source generation supersedes the old one in RuntimeStateStore.
            SourceGenerationToken newSource = LiveSource(epoch, generation: 2);
            engine.RaiseOcrResult(LiveOcrEvent(newSource, resultSequence: 2));

            // A late translation result still referencing the superseded generation must be rejected,
            // not published, and counted.
            engine.RaiseTranslationOutput(
                LiveTranslationEvent(profileId, oldChannel, stageSequence: 1, attempt: 1, streamSequence: 2, "Late"));

            Assert.Single(receivedTranslations);
            Assert.Equal("Hello", receivedTranslations[0].Text);
            Assert.Equal(
                1,
                hub.GetAdmissionRejectedCount(RuntimeStateAdmission.RejectedStaleSourceGeneration));
            Assert.Equal(1, hub.TotalAdmissionRejectedCount);

            // Only the two accepted events (OCR gen1, translation gen1) plus the accepted OCR gen2
            // reach the ring buffer; the rejected late translation does not.
            Assert.Equal(3, hub.Snapshot().Count);
        }
    }

    [Fact]
    public async Task Epoch_advance_on_restart_rejects_prior_run_tokens()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (RealRuntimeControlService service, Guid profileId, ScriptedEngine engine, RuntimeStateStore runtimeState, RuntimeEventHub hub) =
            await BuildWithRuntimeAsync([Window("Notepad")], ct);
        await using (service)
        {
            await service.StartAsync(profileId, ct);

            Guid firstRunEpoch = Guid.NewGuid();
            SourceGenerationToken firstRunSource = LiveSource(firstRunEpoch, generation: 1);
            engine.RaiseOcrResult(LiveOcrEvent(firstRunSource));
            Assert.Equal(firstRunEpoch, runtimeState.CurrentEpoch);
            Assert.Equal(0, hub.TotalAdmissionRejectedCount);

            // Simulate an EngineHost restart: stop, then start again. RealRuntimeControlService
            // re-subscribes the (scripted) engine and arms a pending epoch advance for the new run.
            await service.StopAsync(ct);
            await service.StartAsync(profileId, ct);

            Guid secondRunEpoch = Guid.NewGuid();
            SourceGenerationToken secondRunSource = LiveSource(secondRunEpoch, generation: 1);
            engine.RaiseOcrResult(LiveOcrEvent(secondRunSource));
            Assert.Equal(secondRunEpoch, runtimeState.CurrentEpoch);

            // A late result still carrying the first run's epoch must now be rejected as stale, even
            // though its own generation number was never before observed in the new epoch.
            engine.RaiseOcrResult(LiveOcrEvent(LiveSource(firstRunEpoch, generation: 2)));
            Assert.Equal(
                1,
                hub.GetAdmissionRejectedCount(RuntimeStateAdmission.RejectedStaleSourceGeneration));
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
        public int ManualOcrRequests { get; private set; }

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

        public ValueTask RequestManualOcrAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManualOcrRequests++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        /// <summary>Test seam: raises OcrResultReceived exactly as the real engine would.</summary>
        public void RaiseOcrResult(EngineOcrResultEvent ocrEvent) =>
            OcrResultReceived?.Invoke(this, ocrEvent);

        /// <summary>Test seam: raises TranslationOutputReceived exactly as the real engine would.</summary>
        public void RaiseTranslationOutput(EngineTranslationOutputEvent translationEvent) =>
            TranslationOutputReceived?.Invoke(this, translationEvent);

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
