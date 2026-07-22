using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.App.Presentation.ViewModels;

// View models stay free of Microsoft.UI.* and native engine types (and of Core types) so they remain
// unit-testable and honor the plan rule that no view model depends on UI or engine internals. Data is
// loaded asynchronously via InitializeAsync; collections are observable so the UI updates on load.

public sealed partial class ProfileCenterViewModel : PageViewModelBase
{
    private readonly IProfileService _profileService;

    public ProfileCenterViewModel(IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        _profileService = profileService;
    }

    public ObservableCollection<ProfileCard> Profiles { get; } = [];

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            IReadOnlyList<ProfileCard> profiles =
                await _profileService.GetProfilesAsync(cancellationToken).ConfigureAwait(true);
            Profiles.Clear();
            foreach (ProfileCard profile in profiles)
            {
                Profiles.Add(profile);
            }

            IsEmpty = Profiles.Count == 0;
        });

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _profileService.DeleteAsync(profileId, cancellationToken).ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfileCard> profiles =
            await _profileService.GetProfilesAsync(cancellationToken).ConfigureAwait(true);
        Profiles.Clear();
        foreach (ProfileCard profile in profiles)
        {
            Profiles.Add(profile);
        }

        IsEmpty = Profiles.Count == 0;
    }
}

/// <summary>
/// Maps the 8 engine runtime states to stable UI resource keys and severities. Kept as a
/// standalone pure mapping so tests pin the full state coverage.
/// </summary>
public static class EngineStatusPresenter
{
    public static string ResourceKeyFor(EngineRuntimeStatus status) => status switch
    {
        EngineRuntimeStatus.Stopped => "EngineStatusStopped",
        EngineRuntimeStatus.Locating => "EngineStatusLocating",
        EngineRuntimeStatus.Starting => "EngineStatusStarting",
        EngineRuntimeStatus.Running => "EngineStatusRunning",
        EngineRuntimeStatus.Restarting => "EngineStatusRestarting",
        EngineRuntimeStatus.Stopping => "EngineStatusStopping",
        EngineRuntimeStatus.Faulted => "EngineStatusFaulted",
        EngineRuntimeStatus.ExecutableNotFound => "EngineStatusExecutableNotFound",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static Controls.StatusSeverity SeverityFor(EngineRuntimeStatus status) => status switch
    {
        EngineRuntimeStatus.Running => Controls.StatusSeverity.Success,
        EngineRuntimeStatus.Stopped => Controls.StatusSeverity.Neutral,
        EngineRuntimeStatus.Locating or EngineRuntimeStatus.Starting or
            EngineRuntimeStatus.Stopping => Controls.StatusSeverity.Info,
        EngineRuntimeStatus.Restarting => Controls.StatusSeverity.Warning,
        EngineRuntimeStatus.Faulted or EngineRuntimeStatus.ExecutableNotFound =>
            Controls.StatusSeverity.Critical,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>
    /// Verbatim detail for a transition: the stable error code plus every searched path (for
    /// ExecutableNotFound). Never invents text; empty when the transition carries no failure.
    /// </summary>
    public static string DetailFor(EngineRuntimeStatusChange? change)
    {
        if (change is null)
        {
            return string.Empty;
        }
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(change.ErrorCode))
        {
            parts.Add(change.ErrorCode);
        }
        parts.AddRange(change.SearchedPaths);
        return string.Join(Environment.NewLine, parts);
    }
}

public sealed partial class RunningTargetsViewModel : PageViewModelBase
{
    private readonly IRuntimeControlService _controlService;
    private readonly IProfileService _profileService;
    // Captured at construction (the UI thread in the app, null in tests) so engine events raised
    // on the event-pump thread marshal back before touching observable state.
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public RunningTargetsViewModel(IRuntimeControlService controlService, IProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(controlService);
        ArgumentNullException.ThrowIfNull(profileService);
        _controlService = controlService;
        _profileService = profileService;
        EngineStatus = controlService.Status;
        EngineStatusDetail = EngineStatusPresenter.DetailFor(controlService.LastChange);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync, () => CanStop());
        ToggleOverlayCommand = new AsyncRelayCommand(ToggleOverlayAsync, () => CanStop());
        ManualOcrCommand = new AsyncRelayCommand(
            ManualOcrAsync, () => CanStop() && !IsManualOcrUnavailable);
        _controlService.StatusChanged += OnStatusChanged;
        _controlService.TargetsChanged += OnTargetsChanged;
    }

    public ObservableCollection<ProfileCard> Profiles { get; } = [];

    public ObservableCollection<RunningTarget> Targets { get; } = [];

    [ObservableProperty]
    public partial ProfileCard? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial EngineRuntimeStatus EngineStatus { get; private set; }

    // Stable error code + searched paths of the latest transition, verbatim.
    [ObservableProperty]
    public partial string EngineStatusDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial bool IsOverlayVisible { get; private set; } = true;

    // Set the first time the backend reports the manual-OCR operation as unsupported; the button
    // stays disabled with the honest protocol reason instead of silently no-oping.
    [ObservableProperty]
    public partial bool IsManualOcrUnavailable { get; private set; }

    public IAsyncRelayCommand StartCommand { get; }

    public IAsyncRelayCommand StopCommand { get; }

    public IAsyncRelayCommand TogglePauseCommand { get; }

    public IAsyncRelayCommand ToggleOverlayCommand { get; }

    public IAsyncRelayCommand ManualOcrCommand { get; }

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            IReadOnlyList<ProfileCard> profiles =
                await _profileService.GetProfilesAsync(cancellationToken).ConfigureAwait(true);
            Profiles.Clear();
            foreach (ProfileCard profile in profiles)
            {
                Profiles.Add(profile);
            }
            SelectedProfile ??= Profiles.FirstOrDefault();
            RefreshFromService();
        });

    partial void OnSelectedProfileChanged(ProfileCard? value) => NotifyCommands();

    private bool CanStart() =>
        SelectedProfile is not null && EngineStatus is EngineRuntimeStatus.Stopped
            or EngineRuntimeStatus.Faulted or EngineRuntimeStatus.ExecutableNotFound;

    private bool CanStop() => EngineStatus is EngineRuntimeStatus.Running
        or EngineRuntimeStatus.Restarting;

    private Task StartAsync() => RunGuardedAsync(async () =>
    {
        ProfileCard profile = SelectedProfile
            ?? throw new InvalidOperationException("engine.start.noProfileSelected");
        await _controlService.StartAsync(profile.ProfileId).ConfigureAwait(true);
        RefreshFromService();
    });

    private Task StopAsync() => RunGuardedAsync(async () =>
    {
        await _controlService.StopAsync().ConfigureAwait(true);
        RefreshFromService();
    });

    private Task TogglePauseAsync() => RunGuardedAsync(async () =>
    {
        await _controlService.SetPausedAsync(!_controlService.IsPaused).ConfigureAwait(true);
        IsPaused = _controlService.IsPaused;
    });

    private Task ToggleOverlayAsync() => RunGuardedAsync(async () =>
    {
        await _controlService.SetOverlayVisibleAsync(!_controlService.IsOverlayVisible)
            .ConfigureAwait(true);
        IsOverlayVisible = _controlService.IsOverlayVisible;
    });

    private Task ManualOcrAsync() => RunGuardedAsync(async () =>
    {
        try
        {
            await _controlService.RequestManualOcrAsync().ConfigureAwait(true);
        }
        catch (EngineRuntimeUnsupportedOperationException)
        {
            IsManualOcrUnavailable = true;
            ManualOcrCommand.NotifyCanExecuteChanged();
        }
    });

    private void OnStatusChanged(object? sender, EngineRuntimeStatusChange change) =>
        Dispatch(() =>
        {
            EngineStatus = change.Status;
            EngineStatusDetail = EngineStatusPresenter.DetailFor(change);
            NotifyCommands();
        });

    private void OnTargetsChanged(object? sender, EventArgs args) =>
        Dispatch(RefreshTargets);

    private void RefreshFromService()
    {
        EngineStatus = _controlService.Status;
        EngineStatusDetail = EngineStatusPresenter.DetailFor(_controlService.LastChange);
        IsPaused = _controlService.IsPaused;
        IsOverlayVisible = _controlService.IsOverlayVisible;
        RefreshTargets();
        NotifyCommands();
    }

    private void RefreshTargets()
    {
        Targets.Clear();
        foreach (RunningTarget target in _controlService.GetRunningTargets())
        {
            Targets.Add(target);
        }
        IsEmpty = Targets.Count == 0;
    }

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        TogglePauseCommand.NotifyCanExecuteChanged();
        ToggleOverlayCommand.NotifyCanExecuteChanged();
        ManualOcrCommand.NotifyCanExecuteChanged();
    }

    private void Dispatch(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}

public sealed partial class HistoryViewModel : PageViewModelBase
{
    private readonly IHistoryService _historyService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    public partial bool IsHistoryDisabled { get; private set; }

    public HistoryViewModel(IHistoryService historyService, ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(settingsService);
        _historyService = historyService;
        _settingsService = settingsService;
    }

    public ObservableCollection<HistoryEvent> Events { get; } = [];

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            ApplicationSettings settings =
                await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(true);
            IsHistoryDisabled = settings.HistoryRetention == HistoryRetention.Off;

            IReadOnlyList<HistoryEvent> events =
                await _historyService.GetEventsAsync(cancellationToken).ConfigureAwait(true);
            Events.Clear();
            foreach (HistoryEvent item in events)
            {
                Events.Add(item);
            }

            IsEmpty = Events.Count == 0 && !IsHistoryDisabled;
        });
}

public sealed partial class DiagnosticsViewModel : PageViewModelBase
{
    private readonly IDiagnosticsService _diagnosticsService;

    public DiagnosticsViewModel(IDiagnosticsService diagnosticsService)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsService);
        _diagnosticsService = diagnosticsService;
    }

    public ObservableCollection<DiagnosticEvent> Events { get; } = [];

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            IReadOnlyList<DiagnosticEvent> events =
                await _diagnosticsService.GetEventsAsync(cancellationToken).ConfigureAwait(true);
            Events.Clear();
            foreach (DiagnosticEvent item in events)
            {
                Events.Add(item);
            }

            IsEmpty = Events.Count == 0;
        });
}

public sealed partial class GlossaryViewModel : PageViewModelBase
{
    private readonly IGlossaryService _glossaryService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoActiveProfile))]
    public partial bool HasActiveProfile { get; private set; }

    [ObservableProperty]
    public partial string ActiveProfileName { get; private set; } = string.Empty;

    public bool NoActiveProfile => !HasActiveProfile;

    public GlossaryViewModel(IGlossaryService glossaryService)
    {
        ArgumentNullException.ThrowIfNull(glossaryService);
        _glossaryService = glossaryService;
    }

    public ObservableCollection<GlossaryEntry> Entries { get; } = [];

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(() => ReloadAsync(cancellationToken));

    public Task AddOrUpdateAsync(
        GlossaryEntry entry,
        string? replacingSourceTerm,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _glossaryService.AddOrUpdateAsync(entry, replacingSourceTerm, cancellationToken)
                .ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    public Task RemoveAsync(string sourceTerm, CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _glossaryService.RemoveAsync(sourceTerm, cancellationToken).ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        GlossarySnapshot snapshot =
            await _glossaryService.GetEntriesAsync(cancellationToken).ConfigureAwait(true);
        HasActiveProfile = snapshot.HasActiveProfile;
        ActiveProfileName = snapshot.ActiveProfileName;
        Entries.Clear();
        foreach (GlossaryEntry entry in snapshot.Entries)
        {
            Entries.Add(entry);
        }

        IsEmpty = HasActiveProfile && Entries.Count == 0;
    }
}

public sealed partial class ServicesModelsViewModel : PageViewModelBase
{
    private readonly ISettingsService _settingsService;

    public ServicesModelsViewModel(ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
    }

    public ObservableCollection<ProviderRow> Providers { get; } = [];

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            IReadOnlyList<ProviderRow> providers =
                await _settingsService.GetProvidersAsync(cancellationToken).ConfigureAwait(true);
            Providers.Clear();
            foreach (ProviderRow provider in providers)
            {
                Providers.Add(provider);
            }

            IsEmpty = Providers.Count == 0;
        });
}

public sealed partial class SettingsViewModel : PageViewModelBase
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
    }

    [ObservableProperty]
    public partial ApplicationSettings Settings { get; set; } = new(
        UiThemePreference.System, StrictOffline: false, HistoryRetention.Days30, "en-US");

    public int MaxTargets { get; }

    public int MaxRegionsPerTarget { get; }

    public int MaxTranslationChannelsPerRegion { get; }

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
            Settings = await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(true));

    public Task UpdateThemeAsync(UiThemePreference theme, CancellationToken cancellationToken = default) =>
        ApplyAsync(Settings with { Theme = theme }, cancellationToken);

    public Task UpdateLanguageAsync(string uiLanguage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uiLanguage);
        return ApplyAsync(Settings with { UiLanguage = uiLanguage }, cancellationToken);
    }

    public Task UpdateStrictOfflineAsync(bool strictOffline, CancellationToken cancellationToken = default) =>
        ApplyAsync(Settings with { StrictOffline = strictOffline }, cancellationToken);

    public Task UpdateHistoryRetentionAsync(
        HistoryRetention retention,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(Settings with { HistoryRetention = retention }, cancellationToken);

    private Task ApplyAsync(ApplicationSettings updated, CancellationToken cancellationToken) =>
        RunGuardedAsync(async () =>
        {
            await _settingsService.UpdateAsync(updated, cancellationToken).ConfigureAwait(true);
            Settings = updated;
        });
}
