using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Presentation.ViewModels;

// View models stay free of Microsoft.UI.* and native engine types so they remain unit-testable
// and honor the plan rule that no view model depends on native engine types.

public sealed partial class ProfileCenterViewModel : ObservableObject
{
    public ProfileCenterViewModel(IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        Profiles = profileService.GetProfiles();
    }

    public IReadOnlyList<ProfileCard> Profiles { get; }
}

public sealed partial class RunningTargetsViewModel : ObservableObject
{
    private readonly IRuntimeControlService _controlService;

    public RunningTargetsViewModel(IRuntimeControlService controlService)
    {
        ArgumentNullException.ThrowIfNull(controlService);
        _controlService = controlService;
        Targets = controlService.GetRunningTargets();
        PauseAllCommand = new RelayCommand(_controlService.PauseAll);
        ToggleOverlayCommand = new RelayCommand(_controlService.ToggleOverlay);
        ManualOcrCommand = new RelayCommand(_controlService.RequestManualOcr);
    }

    public IReadOnlyList<RunningTarget> Targets { get; }

    public IRelayCommand PauseAllCommand { get; }

    public IRelayCommand ToggleOverlayCommand { get; }

    public IRelayCommand ManualOcrCommand { get; }
}

public sealed partial class HistoryViewModel : ObservableObject
{
    public HistoryViewModel(IHistoryService historyService)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        Events = historyService.GetEvents();
    }

    public IReadOnlyList<HistoryEvent> Events { get; }
}

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    public DiagnosticsViewModel(IDiagnosticsService diagnosticsService)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsService);
        Events = diagnosticsService.GetEvents();
    }

    public IReadOnlyList<DiagnosticEvent> Events { get; }
}

public sealed partial class GlossaryViewModel : ObservableObject
{
    public GlossaryViewModel(IGlossaryService glossaryService)
    {
        ArgumentNullException.ThrowIfNull(glossaryService);
        Entries = glossaryService.GetEntries();
    }

    public IReadOnlyList<GlossaryEntry> Entries { get; }
}

public sealed partial class ServicesModelsViewModel : ObservableObject
{
    public ServicesModelsViewModel(ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        Providers = settingsService.GetProviders();
    }

    public IReadOnlyList<ProviderRow> Providers { get; }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    public SettingsViewModel(
        ISettingsService settingsService,
        IRuntimeCapabilitiesService capabilitiesService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(capabilitiesService);
        _settingsService = settingsService;
        RuntimeCapabilities capabilities = capabilitiesService.Capabilities;
        MaxTargets = capabilities.MaxTargets;
        MaxRegionsPerTarget = capabilities.MaxRegionsPerTarget;
        MaxTranslationChannelsPerRegion = capabilities.MaxTranslationChannelsPerRegion;
        Settings = settingsService.GetSettings();
    }

    [ObservableProperty]
    public partial ApplicationSettings Settings { get; set; }

    public int MaxTargets { get; }

    public int MaxRegionsPerTarget { get; }

    public int MaxTranslationChannelsPerRegion { get; }

    public void UpdateTheme(UiThemePreference theme)
    {
        Settings = Settings with { Theme = theme };
        _settingsService.Update(Settings);
    }
}
