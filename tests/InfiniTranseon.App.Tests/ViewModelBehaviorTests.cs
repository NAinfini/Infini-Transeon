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
    public void ProfileCenter_view_model_is_populated_from_fakes()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<ProfileCenterViewModel>();

        Assert.NotEmpty(viewModel.Profiles);
    }

    [Fact]
    public void RunningTargets_view_model_populates_and_commands_execute()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<RunningTargetsViewModel>();

        Assert.NotEmpty(viewModel.Targets);
        Assert.True(viewModel.PauseAllCommand.CanExecute(null));
        Assert.True(viewModel.ToggleOverlayCommand.CanExecute(null));
        Assert.True(viewModel.ManualOcrCommand.CanExecute(null));

        // Fake control intents are deterministic no-ops; executing must not throw.
        viewModel.PauseAllCommand.Execute(null);
        viewModel.ToggleOverlayCommand.Execute(null);
        viewModel.ManualOcrCommand.Execute(null);
    }

    [Fact]
    public void History_view_model_is_populated_with_channels()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<HistoryViewModel>();

        Assert.NotEmpty(viewModel.Events);
        Assert.All(viewModel.Events, evt => Assert.NotEmpty(evt.Channels));
    }

    [Fact]
    public void Diagnostics_view_model_is_populated_from_fakes()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<DiagnosticsViewModel>();

        Assert.NotEmpty(viewModel.Events);
    }

    [Fact]
    public void Glossary_view_model_is_populated_from_fakes()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<GlossaryViewModel>();

        Assert.NotEmpty(viewModel.Entries);
    }

    [Fact]
    public void ServicesModels_view_model_is_populated_from_fakes()
    {
        using ServiceProvider provider = Build();
        var viewModel = provider.GetRequiredService<ServicesModelsViewModel>();

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
    public void Settings_view_model_update_theme_persists_through_service_and_raises_change()
    {
        using ServiceProvider provider = Build();
        var settingsService = provider.GetRequiredService<ISettingsService>();
        var viewModel = provider.GetRequiredService<SettingsViewModel>();

        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.UpdateTheme(UiThemePreference.Dark);

        Assert.Equal(UiThemePreference.Dark, viewModel.Settings.Theme);
        Assert.Equal(UiThemePreference.Dark, settingsService.GetSettings().Theme);
        Assert.Contains(nameof(SettingsViewModel.Settings), changed);
    }

    [Fact]
    public void SetupWizard_view_model_starts_on_first_step()
    {
        SetupWizardViewModel viewModel = new();

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
        SetupWizardViewModel viewModel = new();

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
        SetupWizardViewModel viewModel = new();
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
        SetupWizardViewModel viewModel = new();
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
        Assert.Throws<ArgumentNullException>(() => _ = new RunningTargetsViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new HistoryViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new DiagnosticsViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new GlossaryViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new ServicesModelsViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new SettingsViewModel(null!, null!));
    }
}
