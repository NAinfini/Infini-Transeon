using InfiniTranseon.App.Composition;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
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

        // Protocol v1 carries no manual-OCR message: the affordance disables itself honestly.
        Assert.True(viewModel.IsManualOcrUnavailable);
        Assert.False(viewModel.ManualOcrCommand.CanExecute(null));
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
    public void SetupWizard_view_model_starts_on_first_step()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();

        Assert.Equal(0, viewModel.CurrentStepIndex);
        Assert.Equal(1, viewModel.CurrentStepNumber);
        Assert.True(viewModel.IsStep1);
        Assert.False(viewModel.CanGoBack);
        Assert.True(viewModel.CanGoNext);
        Assert.False(viewModel.IsLastStep);
        Assert.False(viewModel.BackCommand.CanExecute(null));
        Assert.True(viewModel.NextCommand.CanExecute(null));
    }

    [Fact]
    public void SetupWizard_view_model_advances_and_clamps_at_last_step()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();

        for (int step = 0; step < SetupWizardViewModel.StepCount - 1; step++)
        {
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
    public void SetupWizard_step_change_raises_command_can_execute_changed()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<SetupWizardViewModel>();
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
        Assert.Throws<ArgumentNullException>(() => _ = new SettingsViewModel(null!, null!));
        Assert.Throws<ArgumentNullException>(() => _ = new SetupWizardViewModel(null!, null!, null!, null!));
    }
}
