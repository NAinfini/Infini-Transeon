using InfiniTranseon.App.Composition;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniTranseon.App.Tests;

public sealed class ViewModelBehaviorTests
{
    private static ServiceProvider Build() => PresentationComposition.Build();

    [Fact]
    public async Task ProfileCenter_view_model_is_populated_from_fakes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<ProfileCenterViewModel>();

        await viewModel.InitializeAsync(ct);

        Assert.NotEmpty(viewModel.Profiles);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsEmpty);
    }

    [Fact]
    public async Task RunningTargets_view_model_walks_engine_lifecycle_and_populates_targets()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<RunningTargetsViewModel>();

        await viewModel.InitializeAsync(ct);
        Assert.Equal(EngineRuntimeStatus.Stopped, viewModel.EngineStatus);
        Assert.Empty(viewModel.Targets);
        Assert.NotNull(viewModel.SelectedProfile);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));

        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasError);
        Assert.Equal(EngineRuntimeStatus.Running, viewModel.EngineStatus);
        Assert.NotEmpty(viewModel.Targets);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.True(viewModel.StopCommand.CanExecute(null));

        await viewModel.TogglePauseCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsPaused);
        await viewModel.ToggleOverlayCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsOverlayVisible);

        await viewModel.StopCommand.ExecuteAsync(null);
        Assert.Equal(EngineRuntimeStatus.Stopped, viewModel.EngineStatus);
        Assert.Empty(viewModel.Targets);
    }

    [Fact]
    public async Task RunningTargets_manual_ocr_disables_itself_with_the_protocol_reason()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<RunningTargetsViewModel>();
        await viewModel.InitializeAsync(ct);
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.True(viewModel.ManualOcrCommand.CanExecute(null));
        await viewModel.ManualOcrCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsManualOcrUnavailable);
        Assert.True(viewModel.ManualOcrCommand.CanExecute(null));
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public void Engine_status_presenter_maps_every_state_to_a_distinct_resource_key()
    {
        EngineRuntimeStatus[] states = Enum.GetValues<EngineRuntimeStatus>();
        string[] keys = states.Select(EngineStatusPresenter.ResourceKeyFor).ToArray();

        Assert.Equal(8, states.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (EngineRuntimeStatus state in states)
        {
            // Severity mapping must also be total.
            _ = EngineStatusPresenter.SeverityFor(state);
        }
    }

    [Fact]
    public void Engine_status_presenter_reports_error_code_and_searched_paths_verbatim()
    {
        var change = new EngineRuntimeStatusChange(
            EngineRuntimeStatus.ExecutableNotFound,
            DateTimeOffset.UtcNow,
            "engine.runtime.executableNotFound",
            [@"C:\a\host.exe", @"C:\b\host.exe"]);

        string detail = EngineStatusPresenter.DetailFor(change);

        Assert.Contains("engine.runtime.executableNotFound", detail);
        Assert.Contains(@"C:\a\host.exe", detail);
        Assert.Contains(@"C:\b\host.exe", detail);
        Assert.Equal(string.Empty, EngineStatusPresenter.DetailFor(null));
    }

    [Fact]
    public async Task History_view_model_is_populated_with_channels()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<HistoryViewModel>();

        await viewModel.InitializeAsync(ct);

        Assert.NotEmpty(viewModel.Events);
        Assert.All(viewModel.Events, evt => Assert.NotEmpty(evt.Channels));
    }

    // Pure, WinUI-free coverage for the history date-grouping boundary (spec 5.8: Today / Yesterday /
    // specific dates). Exercises HistoryDateGrouping.Group directly — no view model, no UI host.
    [Fact]
    public void HistoryDateGrouping_classifies_today_yesterday_and_earlier_events()
    {
        var now = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        HistoryEvent Evt(string label, DateTimeOffset capturedAt) =>
            new(label, label, "Region", []) { CapturedAtUtc = capturedAt };

        HistoryEvent today = Evt("today", now.AddHours(-1));
        HistoryEvent yesterday = Evt("yesterday", now.AddDays(-1));
        HistoryEvent earlier = Evt("earlier", now.AddDays(-5));

        IReadOnlyList<HistoryDateGroup> groups = HistoryDateGrouping.Group(
            [earlier, today, yesterday],
            now);

        Assert.Equal(3, groups.Count);
        Assert.Equal(HistoryDateGroupKind.Today, groups[0].Kind);
        Assert.Equal(today.SourceText, Assert.Single(groups[0].Items).SourceText);
        Assert.Equal(HistoryDateGroupKind.Yesterday, groups[1].Kind);
        Assert.Equal(yesterday.SourceText, Assert.Single(groups[1].Items).SourceText);
        Assert.Equal(HistoryDateGroupKind.Earlier, groups[2].Kind);
        Assert.Equal(earlier.SourceText, Assert.Single(groups[2].Items).SourceText);
        Assert.Equal(DateOnly.FromDateTime(earlier.CapturedAtUtc.ToLocalTime().DateTime), groups[2].Date);
    }

    [Fact]
    public void HistoryDateGrouping_groups_multiple_events_on_the_same_day_together()
    {
        var now = new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
        HistoryEvent Evt(string label, DateTimeOffset capturedAt) =>
            new(label, label, "Region", []) { CapturedAtUtc = capturedAt };

        IReadOnlyList<HistoryDateGroup> groups = HistoryDateGrouping.Group(
            [Evt("first", now.AddHours(-1)), Evt("second", now.AddHours(-2))],
            now);

        HistoryDateGroup group = Assert.Single(groups);
        Assert.Equal(HistoryDateGroupKind.Today, group.Kind);
        Assert.Equal(2, group.Items.Count);
    }

    [Fact]
    public async Task Diagnostics_view_model_is_populated_from_fakes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<DiagnosticsViewModel>();

        await viewModel.InitializeAsync(ct);

        Assert.NotEmpty(viewModel.Events);
    }

    [Fact]
    public async Task Glossary_view_model_is_populated_from_fakes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<GlossaryViewModel>();

        await viewModel.InitializeAsync(ct);

        Assert.NotEmpty(viewModel.Entries);
        Assert.True(viewModel.HasActiveProfile);
        Assert.False(viewModel.NoActiveProfile);
    }

    [Fact]
    public async Task ServicesModels_view_model_is_populated_from_fakes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<ServicesModelsViewModel>();

        await viewModel.InitializeAsync(ct);

        Assert.NotEmpty(viewModel.Providers);
    }

    [Fact]
    public void Settings_view_model_exposes_protocol_ceilings()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SettingsViewModel>();

        Assert.Equal(RuntimeCapabilities.VersionOne.MaxTargets, viewModel.MaxTargets);
        Assert.Equal(RuntimeCapabilities.VersionOne.MaxRegionsPerTarget, viewModel.MaxRegionsPerTarget);
        Assert.Equal(
            RuntimeCapabilities.VersionOne.MaxTranslationChannelsPerRegion,
            viewModel.MaxTranslationChannelsPerRegion);
        Assert.Equal("0.1.0", viewModel.UpdateSnapshot.CurrentVersion);
    }

    [Fact]
    public async Task Settings_view_model_checks_for_updates_through_the_update_service()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SettingsViewModel>();
        await viewModel.InitializeAsync(ct);

        Assert.Equal(AppUpdateStatus.Idle, viewModel.UpdateSnapshot.Status);

        await viewModel.CheckForUpdatesAsync(ct);

        Assert.Equal(AppUpdateStatus.UpToDate, viewModel.UpdateSnapshot.Status);
        Assert.True(viewModel.CanCheckForUpdates);
        Assert.False(viewModel.CanDownloadUpdate);
    }

    [Fact]
    public async Task Settings_view_model_update_theme_persists_through_service_and_raises_change()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var settingsService = provider.GetRequiredService<ISettingsService>();
        var viewModel = provider.GetRequiredService<SettingsViewModel>();
        await viewModel.InitializeAsync(ct);

        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await viewModel.UpdateThemeAsync(UiThemePreference.Dark, ct);

        Assert.Equal(UiThemePreference.Dark, viewModel.Settings.Theme);
        Assert.Equal(UiThemePreference.Dark, (await settingsService.GetSettingsAsync(ct)).Theme);
        Assert.Contains(nameof(SettingsViewModel.Settings), changed);
    }

    [Fact]
    public async Task Settings_hotkeys_are_editable_disableable_and_conflict_checked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SettingsViewModel>();
        await viewModel.InitializeAsync(ct);
        HotkeyEditorRow overlay = viewModel.Hotkeys.Single(row =>
            row.Action == AppHotkeyAction.ToggleOverlay);
        overlay.Gesture = "Ctrl + Shift + T";
        overlay.Enabled = false;

        await viewModel.SaveHotkeyRowsAsync(ct);

        Assert.False(viewModel.HasError);
        AppHotkeyBinding saved = viewModel.Settings.EffectiveHotkeys.Single(binding =>
            binding.Action == AppHotkeyAction.ToggleOverlay);
        Assert.Equal("Ctrl + Shift + T", saved.Gesture);
        Assert.False(saved.Enabled);

        HotkeyEditorRow pause = viewModel.Hotkeys.Single(row =>
            row.Action == AppHotkeyAction.PauseAll);
        pause.Gesture = viewModel.Hotkeys.Single(row =>
            row.Action == AppHotkeyAction.ManualOcr).Gesture;
        await viewModel.SaveHotkeyRowsAsync(ct);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public void SetupWizard_view_model_starts_on_first_step()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();

        Assert.Equal(0, viewModel.CurrentStepIndex);
        Assert.Equal(1, viewModel.CurrentStepNumber);
        Assert.True(viewModel.IsStep1);
        Assert.Equal("auto", viewModel.SourceLanguage);
        Assert.False(viewModel.CanGoBack);
        Assert.False(viewModel.IsLastStep);
        Assert.False(viewModel.BackCommand.CanExecute(null));

        // A brand-new, unconfigured profile has neither a name nor a capture target yet, so step 1's
        // gate blocks Next until both are supplied (see SetupStepGateReason.NeedsProfileName).
        Assert.False(viewModel.CanGoNext);
        Assert.False(viewModel.NextCommand.CanExecute(null));
        Assert.Equal(SetupStepGateReason.NeedsProfileName, viewModel.CurrentStepGateReason);
    }

    [Fact]
    public async Task SetupWizard_only_offers_selectable_translation_providers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();

        await viewModel.InitializeAsync(ct);

        Assert.Contains(viewModel.Providers, item => item.Name == "DeepL");
        Assert.Contains(viewModel.Providers, item => item.Name == "Baidu Translate");
        Assert.DoesNotContain(viewModel.Providers, item => item.Name == "Local MADLAD-400 3B");
        Assert.All(viewModel.Providers,
            item => Assert.True(item.IsSelectable && item.IsTranslationProvider));
    }

    [Fact]
    public async Task SetupWizard_only_enables_runtime_test_for_a_ready_provider()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();
        await viewModel.InitializeAsync(ct);
        viewModel.ProfileName = "Readiness test";

        viewModel.SelectedProvider = viewModel.Providers.Single(item =>
            item.Id == "translation.baidu");

        Assert.True(viewModel.CanSaveDraft);
        Assert.False(viewModel.HasReadyProvider);
        Assert.False(viewModel.IsConfigurationReady);
        Assert.False(viewModel.CanSave);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.SaveDraftCommand.CanExecute(null));
    }

    [Fact]
    public async Task SetupWizard_creates_a_valid_desktop_fixed_region_from_a_display()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();
        await viewModel.InitializeAsync(ct);
        viewModel.ProfileName = "Desktop crop";
        CaptureProbeTarget display = viewModel.Targets.Single(item =>
            string.Equals(item.Kind, "Display", StringComparison.OrdinalIgnoreCase));
        viewModel.SetSelectedTargets([display]);
        viewModel.SelectedTarget = display;
        viewModel.UseDesktopFixedRegion = true;

        Assert.True(viewModel.CanUseDesktopFixedRegion);
        Assert.True(viewModel.HasReadyTarget);
        Assert.Equal(1920, viewModel.DesktopRegionWidth);
        Assert.Equal(1080, viewModel.DesktopRegionHeight);

        viewModel.DesktopRegionWidth = 0;

        Assert.False(viewModel.HasReadyTarget);
        Assert.False(viewModel.CanSave);
    }

    [Fact]
    public async Task SetupWizard_explicit_draft_save_preserves_incomplete_state()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();
        await viewModel.InitializeAsync(ct);
        viewModel.ProfileName = "Draft profile";
        viewModel.SelectedProvider = viewModel.Providers.Single(item =>
            item.Id == "translation.baidu");

        await viewModel.SaveDraftCommand.ExecuteAsync(null);

        Assert.NotEqual(Guid.Empty, viewModel.SavedProfileId);
        Assert.True(viewModel.WasSavedAsDraft);
        Assert.True(viewModel.IsDraftSaved);
    }

    [Fact]
    public async Task SetupWizard_view_model_advances_and_clamps_at_last_step()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();
        await viewModel.InitializeAsync(ct);

        // Satisfy each step's gate so Next actually advances instead of being blocked:
        // step 1 needs a name plus the target InitializeAsync already auto-selected, step 2's
        // languages/provider are ready by default (InitializeAsync auto-selects the first ready
        // provider), and step 3 needs at least one geometrically valid region.
        viewModel.ProfileName = "Advance test";
        viewModel.AddRegion("HUD", RegionPriorityLevel.P1);

        for (int step = 0; step < SetupWizardViewModel.StepCount - 1; step++)
        {
            Assert.True(viewModel.CanGoNext);
            viewModel.NextCommand.Execute(null);
        }

        Assert.Equal(SetupWizardViewModel.StepCount - 1, viewModel.CurrentStepIndex);
        Assert.True(viewModel.IsLastStep);
        Assert.False(viewModel.CanGoNext);
        Assert.False(viewModel.NextCommand.CanExecute(null));

        // Executing Next past the last step must clamp rather than overflow.
        viewModel.NextCommand.Execute(null);
        Assert.Equal(SetupWizardViewModel.StepCount - 1, viewModel.CurrentStepIndex);
    }

    [Fact]
    public void SetupWizard_view_model_goes_back_and_clamps_at_first_step()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();
        viewModel.NextCommand.Execute(null);

        viewModel.BackCommand.Execute(null);
        Assert.Equal(0, viewModel.CurrentStepIndex);

        // Executing Back below the first step must clamp rather than underflow.
        viewModel.BackCommand.Execute(null);
        Assert.Equal(0, viewModel.CurrentStepIndex);
    }

    [Fact]
    public async Task SetupWizard_step_change_raises_command_can_execute_changed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();
        await viewModel.InitializeAsync(ct);
        viewModel.ProfileName = "Step change test";
        bool backChanged = false;
        viewModel.BackCommand.CanExecuteChanged += (_, _) => backChanged = true;

        viewModel.NextCommand.Execute(null);

        Assert.True(backChanged);
        Assert.True(viewModel.CanGoBack);
    }

    [Fact]
    public void View_model_construction_rejects_null_dependencies()
    {
        // Constructor guards surface missing dependencies instead of swallowing them.
        Assert.Throws<ArgumentNullException>(() => _ = new ProfileCenterViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new RunningTargetsViewModel(null!, null!));
        Assert.Throws<ArgumentNullException>(() => _ = new HistoryViewModel(null!, null!));
        Assert.Throws<ArgumentNullException>(() => _ = new DiagnosticsViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new GlossaryViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new ServicesModelsViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new SettingsViewModel(null!, null!, null!));
        Assert.Throws<ArgumentNullException>(() => _ = new SetupWizardViewModel(null!, null!, null!, null!, null!, null!));
    }
}
