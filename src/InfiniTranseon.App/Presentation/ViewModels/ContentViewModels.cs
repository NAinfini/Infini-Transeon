using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.App.Presentation.Services;

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

// Pure, WinUI-free grouping for the history date headers (spec 5.8: Today / Yesterday / specific
// dates). Kept as a standalone static class so the grouping boundary logic is unit-testable without a
// view model or any UI host; label text formatting stays in the page (localized, culture-aware).
public static class HistoryDateGrouping
{
    public static IReadOnlyList<HistoryDateGroup> Group(
        IReadOnlyList<HistoryEvent> events,
        DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(events);
        DateOnly today = DateOnly.FromDateTime(nowLocal.LocalDateTime);
        DateOnly yesterday = today.AddDays(-1);
        return events
            .GroupBy(item => DateOnly.FromDateTime(item.CapturedAtUtc.ToLocalTime().DateTime))
            .OrderByDescending(group => group.Key)
            .Select(group => new HistoryDateGroup(
                group.Key == today ? HistoryDateGroupKind.Today :
                group.Key == yesterday ? HistoryDateGroupKind.Yesterday :
                HistoryDateGroupKind.Earlier,
                group.Key,
                group.OrderByDescending(item => item.CapturedAtUtc).ToArray()))
            .ToArray();
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
    public ObservableCollection<HistoryDateGroup> GroupedEvents { get; } = [];
    private readonly List<HistoryEvent> _allEvents = [];

    public void SelectProfile(Guid? profileId) => _historyService.SelectProfile(profileId);

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            ApplicationSettings settings =
                await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(true);
            IsHistoryDisabled = settings.HistoryRetention == HistoryRetention.Off;

            IReadOnlyList<HistoryEvent> events =
                await _historyService.GetEventsAsync(cancellationToken).ConfigureAwait(true);
            _allEvents.Clear();
            _allEvents.AddRange(events);
            ApplyFilter(string.Empty, days: 0);
        });

    public void ApplyFilter(string? query, int days)
    {
        string normalized = query?.Trim() ?? string.Empty;
        DateTimeOffset? cutoff = days <= 0 ? null : DateTimeOffset.Now.AddDays(-days);
        IEnumerable<HistoryEvent> filtered = _allEvents.Where(item =>
            (normalized.Length == 0 ||
                item.SourceText.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Region.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Channels.Any(channel =>
                    channel.Text.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                    channel.Provider.Contains(normalized, StringComparison.OrdinalIgnoreCase))) &&
            (cutoff is null ||
                !DateTimeOffset.TryParse(item.Timestamp, out DateTimeOffset timestamp) ||
                timestamp >= cutoff));
        Events.Clear();
        foreach (HistoryEvent item in filtered) Events.Add(item);
        IsEmpty = Events.Count == 0 && !IsHistoryDisabled;

        GroupedEvents.Clear();
        foreach (HistoryDateGroup group in HistoryDateGrouping.Group(Events, DateTimeOffset.Now))
        {
            GroupedEvents.Add(group);
        }
    }
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
    public ObservableCollection<StylePromptVersion> StylePromptVersions { get; } = [];
    private readonly List<GlossaryEntry> _allEntries = [];

    [ObservableProperty]
    public partial StylePromptVersion? SelectedStylePromptVersion { get; set; }

    [ObservableProperty]
    public partial int ActiveStylePromptVersion { get; private set; }

    public void SelectProfile(Guid profileId) => _glossaryService.SelectProfile(profileId);

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

    public Task ImportAsync(
        IReadOnlyList<GlossaryEntry> entries,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _glossaryService.ImportAsync(entries, cancellationToken).ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    public Task SaveStylePromptVersionAsync(
        string name,
        string template,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _glossaryService
                .SaveStylePromptVersionAsync(name, template, cancellationToken)
                .ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    public Task ActivateStylePromptVersionAsync(
        int version,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _glossaryService
                .ActivateStylePromptVersionAsync(version, cancellationToken)
                .ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    public void ApplyFilter(string? query)
    {
        string normalized = query?.Trim() ?? string.Empty;
        IEnumerable<GlossaryEntry> filtered = _allEntries.Where(entry =>
            normalized.Length == 0 ||
            entry.SourceTerm.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            entry.TargetTerm.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            entry.Notes.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        Entries.Clear();
        foreach (GlossaryEntry entry in filtered) Entries.Add(entry);
        IsEmpty = HasActiveProfile && Entries.Count == 0;
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        GlossarySnapshot snapshot =
            await _glossaryService.GetEntriesAsync(cancellationToken).ConfigureAwait(true);
        HasActiveProfile = snapshot.HasActiveProfile;
        ActiveProfileName = snapshot.ActiveProfileName;
        _allEntries.Clear();
        _allEntries.AddRange(snapshot.Entries);
        StylePromptVersions.Clear();
        foreach (StylePromptVersion version in snapshot.StylePromptVersions)
        {
            StylePromptVersions.Add(version);
        }
        ActiveStylePromptVersion = snapshot.ActiveStylePromptVersion;
        SelectedStylePromptVersion = StylePromptVersions.FirstOrDefault(version =>
            version.Version == ActiveStylePromptVersion) ??
            StylePromptVersions.FirstOrDefault();
        ApplyFilter(string.Empty);
    }
}

public sealed partial class ServicesModelsViewModel : PageViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly LocalModelManagementService? _localModels;
    private CancellationTokenSource? _modelOperationCancellation;

    public ServicesModelsViewModel(ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
    }

    public ServicesModelsViewModel(
        ISettingsService settingsService,
        LocalModelManagementService localModels)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localModels);
        _settingsService = settingsService;
        _localModels = localModels;
    }

    public ObservableCollection<ProviderRow> Providers { get; } = [];

    // The grouped sections a provider card renders under (spec 5.9). Order matches the page's
    // visual grouping: cloud translation, cloud OCR, local models, then custom REST adapters, with
    // a trailing Other bucket so a row that matches none of the known shapes is still shown instead
    // of silently dropped.
    public enum ProviderGroup
    {
        CloudTranslation,
        CloudOcr,
        LocalModel,
        Custom,
        Other,
    }

    // Pure and WinUI-free so the page code-behind and unit tests share one classification rule.
    // Custom and local-model rows are identified by their own flags before capability; a built-in
    // row's Kind carries "OCR" (see BuiltInProviderSpecs) whenever its capability is not translation,
    // which is what distinguishes Cloud OCR from the defensive Other fallback.
    public static ProviderGroup ClassifyGroup(ProviderRow provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.IsCustom)
        {
            return ProviderGroup.Custom;
        }
        if (provider.IsLocalModel)
        {
            return ProviderGroup.LocalModel;
        }
        if (provider.IsTranslationProvider)
        {
            return ProviderGroup.CloudTranslation;
        }
        if (provider.Kind.Contains("OCR", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderGroup.CloudOcr;
        }
        return ProviderGroup.Other;
    }

    [ObservableProperty]
    public partial bool IsModelOperationInProgress { get; private set; }

    [ObservableProperty]
    public partial double ModelOperationPercent { get; private set; }

    [ObservableProperty]
    public partial string ModelOperationStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ModelOperationSucceeded { get; private set; }

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(() => ReloadAsync(cancellationToken));

    public Task ImportRestAdapterAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _settingsService
                .ImportRestAdapterAsync(source, cancellationToken)
                .ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    public Task RemoveCustomProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _settingsService
                .RemoveCustomProviderAsync(providerId, cancellationToken)
                .ConfigureAwait(true);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        });

    public Task InstallLocalModelAsync(
        ProviderRow provider,
        bool userApproved,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            LocalModelManagementService models = _localModels ??
                throw new InvalidOperationException(
                    "Local model management is unavailable in this composition.");
            string modelId = provider.ModelId ??
                throw new ArgumentException(
                    "The selected row does not identify a model.",
                    nameof(provider));
            string version = provider.ModelVersion ??
                throw new ArgumentException(
                    "The selected row does not identify a model version.",
                    nameof(provider));
            ApplicationSettings settings =
                await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(true);
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (Interlocked.CompareExchange(
                    ref _modelOperationCancellation,
                    operationCancellation,
                    comparand: null) is not null)
                throw new InvalidOperationException("A local model operation is already running.");
            IsModelOperationInProgress = true;
            ModelOperationSucceeded = false;
            ModelOperationPercent = 0;
            ModelOperationStatus = provider.Name;
            try
            {
                var progress = new Progress<LocalModelOperationProgress>(value =>
                {
                    ModelOperationPercent = value.TotalBytes <= 0
                        ? 0
                        : Math.Clamp(
                            value.BytesReceived * 100d / value.TotalBytes,
                            0,
                            100);
                    ModelOperationStatus = value.RelativePath;
                });
                await models.InstallAsync(
                    modelId,
                    version,
                    userApproved,
                    settings.StrictOffline,
                    progress,
                    operationCancellation.Token).ConfigureAwait(true);
                await ReloadAsync(operationCancellation.Token).ConfigureAwait(true);
                ModelOperationSucceeded = true;
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                ModelOperationStatus = string.Empty;
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _modelOperationCancellation,
                    null,
                    operationCancellation);
                IsModelOperationInProgress = false;
            }
        });

    public Task RemoveLocalModelAsync(
        ProviderRow provider,
        bool userConfirmed,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            LocalModelManagementService models = _localModels ??
                throw new InvalidOperationException(
                    "Local model management is unavailable in this composition.");
            string modelId = provider.ModelId ??
                throw new ArgumentException(
                    "The selected row does not identify a model.",
                    nameof(provider));
            string version = provider.ModelVersion ??
                throw new ArgumentException(
                    "The selected row does not identify a model version.",
                    nameof(provider));
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (Interlocked.CompareExchange(
                    ref _modelOperationCancellation,
                    operationCancellation,
                    comparand: null) is not null)
                throw new InvalidOperationException("A local model operation is already running.");
            IsModelOperationInProgress = true;
            ModelOperationSucceeded = false;
            ModelOperationPercent = 0;
            ModelOperationStatus = provider.Name;
            try
            {
                await models.RemoveAsync(
                    modelId,
                    version,
                    userConfirmed,
                    operationCancellation.Token).ConfigureAwait(true);
                await ReloadAsync(operationCancellation.Token).ConfigureAwait(true);
                ModelOperationSucceeded = true;
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                ModelOperationStatus = string.Empty;
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _modelOperationCancellation,
                    null,
                    operationCancellation);
                IsModelOperationInProgress = false;
            }
        });

    public void CancelModelOperation()
    {
        CancellationTokenSource? cancellation = Volatile.Read(ref _modelOperationCancellation);
        if (cancellation is null)
            return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed between the field read and cancellation request.
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderRow> providers =
            await _settingsService.GetProvidersAsync(cancellationToken).ConfigureAwait(true);
        Providers.Clear();
        foreach (ProviderRow provider in providers)
        {
            Providers.Add(provider);
        }

        IsEmpty = Providers.Count == 0;
    }
}

public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IAppUpdateService _updateService;

    public SettingsViewModel(
        ISettingsService settingsService,
        RuntimeCapabilitiesService capabilitiesService,
        IAppUpdateService updateService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(capabilitiesService);
        ArgumentNullException.ThrowIfNull(updateService);
        _settingsService = settingsService;
        _updateService = updateService;
        RuntimeCapabilities capabilities = capabilitiesService.Capabilities;
        MaxTargets = capabilities.MaxTargets;
        MaxRegionsPerTarget = capabilities.MaxRegionsPerTarget;
        MaxTranslationChannelsPerRegion = capabilities.MaxTranslationChannelsPerRegion;
    }

    [ObservableProperty]
    public partial ApplicationSettings Settings { get; set; } = new(
        UiThemePreference.System, StrictOffline: false, HistoryRetention.Days30, "en-US");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    [NotifyPropertyChangedFor(nameof(CanOpenInstaller))]
    public partial AppUpdateSnapshot UpdateSnapshot { get; private set; } =
        new(AppUpdateStatus.Idle, "0.0.0");

    public bool CanCheckForUpdates => UpdateSnapshot.Status is not
        (AppUpdateStatus.Checking or AppUpdateStatus.Downloading);

    public bool CanDownloadUpdate =>
        !string.IsNullOrWhiteSpace(UpdateSnapshot.AvailableVersion) &&
        UpdateSnapshot.Status is AppUpdateStatus.Available or AppUpdateStatus.Failed;

    public bool CanOpenInstaller =>
        UpdateSnapshot.Status == AppUpdateStatus.ReadyToInstall &&
        !string.IsNullOrWhiteSpace(UpdateSnapshot.InstallerPath);

    public int MaxTargets { get; }

    public int MaxRegionsPerTarget { get; }

    public int MaxTranslationChannelsPerRegion { get; }

    public ObservableCollection<HotkeyEditorRow> Hotkeys { get; } = [];

    // Pure and WinUI-free predicate backing the in-page settings search (spec 5.10): matches a
    // query against a setting row's label/description text so the page code-behind and unit tests
    // share the same rule instead of the page re-implementing it against live XAML elements.
    public static bool MatchesSearch(string query, params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return string.IsNullOrWhiteSpace(query) ||
            fields.Any(field => field.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            Settings = await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(true);
            Hotkeys.Clear();
            foreach (AppHotkeyBinding binding in Settings.EffectiveHotkeys)
            {
                Hotkeys.Add(new HotkeyEditorRow(binding));
            }
            UpdateSnapshot = _updateService.Snapshot;
        });

    public Task CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _updateService.CheckAsync(
                explicitUserAction: true,
                mainUiVisible: true,
                cancellationToken).ConfigureAwait(true);
            UpdateSnapshot = _updateService.Snapshot;
        });

    public Task DownloadUpdateAsync(
        bool userApproved,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            await _updateService.DownloadInstallerAsync(
                userApproved,
                cancellationToken).ConfigureAwait(true);
            UpdateSnapshot = _updateService.Snapshot;
        });

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

    public Task UpdatePerformancePresetAsync(
        AppPerformancePreset preset,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(Settings with { PerformancePreset = preset }, cancellationToken);

    public Task UpdateReducedMotionAsync(
        bool reducedMotion,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(Settings with { ReducedMotion = reducedMotion }, cancellationToken);

    public Task UpdateCloseToTrayAsync(
        bool closeToTray,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(
            Settings with
            {
                CloseToTray = closeToTray,
                CloseToTrayConfirmed = confirmed,
            },
            cancellationToken);

    public Task UpdateHotkeysAsync(
        IReadOnlyList<AppHotkeyBinding> hotkeys,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(hotkeys);
            if (hotkeys.Select(binding => binding.Action).Distinct().Count() != hotkeys.Count)
            {
                throw new ArgumentException("Hotkey actions must be unique.", nameof(hotkeys));
            }
            AppHotkeyBinding[] enabled = hotkeys.Where(binding => binding.Enabled).ToArray();
            if (enabled.Any(binding => !HotkeyGesture.TryParse(binding.Gesture, out _)) ||
                enabled.GroupBy(
                        binding => binding.Gesture.Replace(" ", string.Empty),
                        StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
            {
                throw new ArgumentException(
                    "Enabled hotkeys must be valid and unique.",
                    nameof(hotkeys));
            }
            ApplicationSettings updated = Settings with { Hotkeys = hotkeys.ToArray() };
            await _settingsService.UpdateAsync(updated, cancellationToken).ConfigureAwait(true);
            Settings = updated;
        });

    public Task SaveHotkeyRowsAsync(CancellationToken cancellationToken = default) =>
        UpdateHotkeysAsync(
            Hotkeys.Select(row => row.ToBinding()).ToArray(),
            cancellationToken);

    public void RestoreDefaultHotkeys()
    {
        Hotkeys.Clear();
        foreach (AppHotkeyBinding binding in HotkeyDefaults.Create())
        {
            Hotkeys.Add(new HotkeyEditorRow(binding));
        }
    }

    private Task ApplyAsync(ApplicationSettings updated, CancellationToken cancellationToken) =>
        RunGuardedAsync(async () =>
        {
            await _settingsService.UpdateAsync(updated, cancellationToken).ConfigureAwait(true);
            Settings = updated;
        });
}
