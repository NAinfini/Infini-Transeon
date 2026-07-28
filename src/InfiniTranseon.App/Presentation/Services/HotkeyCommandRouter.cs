using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Immutable foreground identity sampled synchronously when WM_HOTKEY arrives. Window and monitor
/// handles are already normalized by the Win32 host; this pure value keeps target matching
/// deterministic and unit-testable.
/// </summary>
public readonly record struct ForegroundTargetSnapshot(
    ulong RootWindowHandle,
    ulong MonitorHandle)
{
    public static ForegroundTargetSnapshot Empty { get; } = new(0, 0);
}

public static class ForegroundTargetResolver
{
    public static RuntimeTargetSelection Resolve(
        ForegroundTargetSnapshot foreground,
        IReadOnlyList<RuntimeTargetDescriptor> runningTargets)
    {
        ArgumentNullException.ThrowIfNull(runningTargets);
        AppHotkeyTargetReference[] matches = runningTargets
            .Where(target => target.Kind switch
            {
                RuntimeCaptureTargetKind.Window =>
                    foreground.RootWindowHandle != 0 &&
                    target.NativeHandle == foreground.RootWindowHandle,
                RuntimeCaptureTargetKind.Monitor =>
                    foreground.MonitorHandle != 0 &&
                    target.NativeHandle == foreground.MonitorHandle,
                RuntimeCaptureTargetKind.DesktopRegion => false,
                _ => false,
            })
            .Select(target => target.Reference)
            .Distinct()
            .ToArray();
        return RuntimeTargetSelection.Explicit(matches);
    }
}

public sealed class HotkeyCommandRouter
{
    private readonly IRuntimeControlService _runtime;

    public HotkeyCommandRouter(IRuntimeControlService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public async Task<RuntimeScopedControlResult> ExecuteAsync(
        AppHotkeyBinding binding,
        ForegroundTargetSnapshot foreground,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        RuntimeTargetSelection selection = binding.Scope switch
        {
            AppHotkeyScope.AllRunningTargets => RuntimeTargetSelection.All,
            AppHotkeyScope.ForegroundMatchingTarget => ForegroundTargetResolver.Resolve(
                foreground,
                _runtime.GetRuntimeTargetDescriptors()),
            AppHotkeyScope.SpecificTargetGroup => RuntimeTargetSelection.Explicit(
                binding.EffectiveSpecificTargets),
            _ => throw new ArgumentOutOfRangeException(
                nameof(binding),
                binding.Scope,
                "Unknown hotkey target scope."),
        };

        return binding.Action switch
        {
            AppHotkeyAction.ToggleOverlay =>
                await _runtime.ToggleOverlayAsync(selection, cancellationToken)
                    .ConfigureAwait(false),
            AppHotkeyAction.PauseAll =>
                await _runtime.TogglePausedAsync(selection, cancellationToken)
                    .ConfigureAwait(false),
            AppHotkeyAction.ManualOcr =>
                await _runtime.RequestManualOcrAsync(selection, cancellationToken)
                    .ConfigureAwait(false),
            AppHotkeyAction.CycleTranslationGroup =>
                await CycleTranslationGroupAsync(cancellationToken).ConfigureAwait(false),
            AppHotkeyAction.RetranslateCurrent =>
                new RuntimeScopedControlResult(
                    false,
                    "hotkey.retranslate.comingSoon",
                    0),
            AppHotkeyAction.EmergencyStop =>
                await StopAllAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(binding),
                binding.Action,
                "Unknown hotkey action."),
        };
    }

    private async Task<RuntimeScopedControlResult> CycleTranslationGroupAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TranslationGroupOption> groups =
            await _runtime.GetActiveTranslationGroupsAsync(cancellationToken).ConfigureAwait(false);
        if (groups.Count < 2)
            return new RuntimeScopedControlResult(false, "translationGroup.unavailable", 0);
        // The active group is reported first by the runtime facade, so choose the next configured
        // group without deriving a hidden foreground/specific scope.
        TranslationGroupOption next = groups[1];
        TranslationGroupSwitchResult result = await _runtime
            .SwitchTranslationGroupAsync(next.TranslationGroupId, cancellationToken)
            .ConfigureAwait(false);
        return new RuntimeScopedControlResult(
            result.Applied,
            result.StatusCode,
            result.ReplayedTargetCount + result.WaitingTargetCount);
    }

    private async Task<RuntimeScopedControlResult> StopAllAsync(
        CancellationToken cancellationToken)
    {
        int targetCount = _runtime.GetRuntimeTargetDescriptors().Count;
        await _runtime.StopAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeScopedControlResult(
            true,
            "runtime.control.stopped",
            targetCount);
    }
}
