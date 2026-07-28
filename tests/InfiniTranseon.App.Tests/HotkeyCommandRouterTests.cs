using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Tests;

public sealed class HotkeyCommandRouterTests
{
    [Fact]
    public void Foreground_resolver_matches_exact_root_window_or_monitor_without_fallback()
    {
        RuntimeTargetDescriptor window = Target(RuntimeCaptureTargetKind.Window, 0x1111);
        RuntimeTargetDescriptor monitor = Target(RuntimeCaptureTargetKind.Monitor, 0x2222);
        RuntimeTargetDescriptor desktop = Target(RuntimeCaptureTargetKind.DesktopRegion, 0);

        RuntimeTargetSelection windowSelection = ForegroundTargetResolver.Resolve(
            new ForegroundTargetSnapshot(0x1111, 0x9999),
            [window, monitor, desktop]);
        Assert.Equal([window.Reference], windowSelection.Targets);

        RuntimeTargetSelection monitorSelection = ForegroundTargetResolver.Resolve(
            new ForegroundTargetSnapshot(0x9999, 0x2222),
            [window, monitor, desktop]);
        Assert.Equal([monitor.Reference], monitorSelection.Targets);

        RuntimeTargetSelection noMatch = ForegroundTargetResolver.Resolve(
            new ForegroundTargetSnapshot(0x3333, 0x4444),
            [window, monitor, desktop]);
        Assert.Equal(RuntimeTargetSelectionMode.ExplicitTargets, noMatch.Mode);
        Assert.Empty(noMatch.Targets);
    }

    [Fact]
    public async Task Router_preserves_specific_and_foreground_empty_selections()
    {
        RuntimeTargetDescriptor target = Target(RuntimeCaptureTargetKind.Window, 0x1111);
        var runtime = new RecordingRuntime([target]);
        var router = new HotkeyCommandRouter(runtime);

        RuntimeScopedControlResult specific = await router.ExecuteAsync(
            new AppHotkeyBinding(
                AppHotkeyAction.PauseAll,
                "Ctrl + Alt + P",
                Scope: AppHotkeyScope.SpecificTargetGroup,
                SpecificTargets: [target.Reference]),
            ForegroundTargetSnapshot.Empty,
            TestContext.Current.CancellationToken);
        Assert.True(specific.Applied);
        Assert.Equal([target.Reference], runtime.LastSelection!.Targets);

        RuntimeScopedControlResult noMatch = await router.ExecuteAsync(
            new AppHotkeyBinding(
                AppHotkeyAction.ToggleOverlay,
                "Ctrl + Alt + T",
                Scope: AppHotkeyScope.ForegroundMatchingTarget),
            new ForegroundTargetSnapshot(0x9999, 0),
            TestContext.Current.CancellationToken);
        Assert.False(noMatch.Applied);
        Assert.Equal(RuntimeTargetSelectionMode.ExplicitTargets, runtime.LastSelection!.Mode);
        Assert.Empty(runtime.LastSelection.Targets);
    }

    [Fact]
    public async Task Emergency_stop_always_stops_the_whole_runtime()
    {
        RuntimeTargetDescriptor target = Target(RuntimeCaptureTargetKind.Window, 0x1111);
        var runtime = new RecordingRuntime([target]);
        var router = new HotkeyCommandRouter(runtime);

        RuntimeScopedControlResult result = await router.ExecuteAsync(
            new AppHotkeyBinding(
                AppHotkeyAction.EmergencyStop,
                "Ctrl + Alt + Escape"),
            ForegroundTargetSnapshot.Empty,
            TestContext.Current.CancellationToken);

        Assert.True(runtime.Stopped);
        Assert.True(result.Applied);
        Assert.Equal(1, result.ResolvedTargetCount);
    }

    private static RuntimeTargetDescriptor Target(
        RuntimeCaptureTargetKind kind,
        ulong handle) => new(
            new AppHotkeyTargetReference(Guid.NewGuid(), Guid.NewGuid()),
            new TargetInstanceId(Guid.NewGuid()),
            kind,
            handle);

    private sealed class RecordingRuntime(
        IReadOnlyList<RuntimeTargetDescriptor> targets) : IRuntimeControlService
    {
        public EngineRuntimeStatus Status => EngineRuntimeStatus.Running;
        public EngineRuntimeStatusChange? LastChange => null;
        public bool IsPaused => false;
        public bool IsOverlayVisible => true;
        public RuntimeTargetSelection? LastSelection { get; private set; }
        public bool Stopped { get; private set; }

        public event EventHandler<EngineRuntimeStatusChange>? StatusChanged;
        public event EventHandler? TargetsChanged;

        public IReadOnlyList<RunningTarget> GetRunningTargets() => [];
        public IReadOnlyList<RuntimeTargetDescriptor> GetRuntimeTargetDescriptors() => targets;
        public Task StartAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetOverlayVisibleAsync(
            bool visible,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RequestManualOcrAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RuntimeScopedControlResult> TogglePausedAsync(
            RuntimeTargetSelection selection,
            CancellationToken cancellationToken = default) =>
            Record(selection);

        public Task<RuntimeScopedControlResult> ToggleOverlayAsync(
            RuntimeTargetSelection selection,
            CancellationToken cancellationToken = default) =>
            Record(selection);

        public Task<RuntimeScopedControlResult> RequestManualOcrAsync(
            RuntimeTargetSelection selection,
            CancellationToken cancellationToken = default) =>
            Record(selection);

        private Task<RuntimeScopedControlResult> Record(RuntimeTargetSelection selection)
        {
            LastSelection = selection;
            bool applied = selection.Mode == RuntimeTargetSelectionMode.AllTargets ||
                selection.Targets.Count > 0;
            return Task.FromResult(new RuntimeScopedControlResult(
                applied,
                applied ? "applied" : "runtime.control.noMatchingTarget",
                selection.Mode == RuntimeTargetSelectionMode.AllTargets
                    ? targets.Count
                    : selection.Targets.Count));
        }

        private void TouchEvents()
        {
            _ = StatusChanged;
            _ = TargetsChanged;
        }
    }
}
