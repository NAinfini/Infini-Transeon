using System.Collections.ObjectModel;
using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Controls.Dialogs;
using InfiniTranseon.App.Features.SetupWizard;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InfiniTranseon.App.Features.Home;

public sealed partial class HomePage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private readonly AppNavigationState _navigation;
    private readonly RuntimeEventHub _runtimeEvents;
    private readonly ISettingsService _settingsService;
    private readonly DialogService _dialogs;
    private ApplicationSettings? _settings;
    private string _operationError = string.Empty;
    private bool _subscribed;

    public HomePage()
    {
        ProfilesViewModel = App.GetService<ProfileCenterViewModel>();
        RuntimeViewModel = App.GetService<RunningTargetsViewModel>();
        _navigation = App.GetService<AppNavigationState>();
        _runtimeEvents = App.GetService<RuntimeEventHub>();
        _settingsService = App.GetService<ISettingsService>();
        _dialogs = new DialogService(() => XamlRoot);
        InitializeComponent();
    }

    public ProfileCenterViewModel ProfilesViewModel { get; }
    public RunningTargetsViewModel RuntimeViewModel { get; }
    public ObservableCollection<HomeActivityItem> RecentActivity { get; } = [];
    public string ErrorMessage => string.IsNullOrEmpty(_operationError)
        ? ProfilesViewModel.ErrorMessage
        : _operationError;

    public string HomeTitle => Strings.GetString("HomeTitle");
    public string HomeSubtitle => Strings.GetString("HomeSubtitle");
    public string EmptyTitle => Strings.GetString("HomeEmptyTitle");
    public string EmptyBody => Strings.GetString("HomeEmptyBody");
    public Visibility RunningPanelVisibility => RuntimeViewModel.Targets.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility ActivityVisibility => RecentActivity.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;
    public string RuntimeStatusText => Strings.GetString(
        EngineStatusPresenter.ResourceKeyFor(RuntimeViewModel.EngineStatus));
    public StatusSeverity RuntimeStatusSeverity =>
        EngineStatusPresenter.SeverityFor(RuntimeViewModel.EngineStatus);
    public string RunningTargetSummary => RuntimeViewModel.Targets.FirstOrDefault() is { } target
        ? $"{target.ProfileName} · {target.WindowTitle}"
        : string.Empty;
    public string RunningTargetMetrics => RuntimeViewModel.Targets.FirstOrDefault() is { } target
        ? string.Format(
            Strings.GetString("HomeRunningMetrics"),
            target.ActiveRegions,
            target.LatencyP95)
        : string.Empty;
    public string PauseLabel => Strings.GetString(
        RuntimeViewModel.IsPaused ? "ResumeAllLabel" : "PauseAllLabel");
    public string OverlayLabel => Strings.GetString(
        RuntimeViewModel.IsOverlayVisible ? "HideOverlayLabel" : "ShowOverlayLabel");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        await Task.WhenAll(
            ProfilesViewModel.InitializeAsync(),
            RuntimeViewModel.InitializeAsync());
        try
        {
            _settings = await _settingsService.GetSettingsAsync();
            ApplyPinnedProfiles();
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
        }
        LoadRecentActivity();
        Bindings.Update();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _runtimeEvents.DiagnosticRaised += OnDiagnosticRaised;
        RuntimeViewModel.PropertyChanged += OnRuntimePropertyChanged;
        RuntimeViewModel.Targets.CollectionChanged += OnRuntimeCollectionChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _runtimeEvents.DiagnosticRaised -= OnDiagnosticRaised;
        RuntimeViewModel.PropertyChanged -= OnRuntimePropertyChanged;
        RuntimeViewModel.Targets.CollectionChanged -= OnRuntimeCollectionChanged;
        _subscribed = false;
    }

    private void OnRuntimePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Bindings.Update);

    private void OnRuntimeCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Bindings.Update);

    private void OnDiagnosticRaised(object? sender, RuntimeDiagnosticRaised diagnostic) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            InsertDiagnostic(diagnostic);
            Bindings.Update();
        });

    private void LoadRecentActivity()
    {
        RecentActivity.Clear();
        foreach (RuntimeDiagnosticRaised diagnostic in _runtimeEvents
            .Snapshot()
            .Where(item => item.Kind == RuntimeHubEventKind.DiagnosticRaised)
            .Select(item => (RuntimeDiagnosticRaised)item.Payload)
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(3)
            .Reverse())
        {
            InsertDiagnostic(diagnostic);
        }
    }

    private void InsertDiagnostic(RuntimeDiagnosticRaised diagnostic)
    {
        RecentActivity.Insert(0, new HomeActivityItem(
            diagnostic.OccurredAtUtc.ToLocalTime(),
            diagnostic.ErrorCode,
            diagnostic.MessageKey,
            diagnostic.Severity switch
            {
                RuntimeDiagnosticSeverity.Error => StatusSeverity.Critical,
                RuntimeDiagnosticSeverity.Warning => StatusSeverity.Warning,
                _ => StatusSeverity.Info,
            }));
        while (RecentActivity.Count > 3)
        {
            RecentActivity.RemoveAt(RecentActivity.Count - 1);
        }
    }

    private void OnNewProfileClick(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SetupWizardPage));

    private void OnOpenWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid profileId } && profileId != Guid.Empty)
        {
            _navigation.NavigateToProfile(profileId);
        }
    }

    private async void OnStartProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId } || profileId == Guid.Empty)
        {
            return;
        }

        RuntimeViewModel.SelectedProfile = RuntimeViewModel.Profiles.FirstOrDefault(
            profile => profile.ProfileId == profileId);
        if (!RuntimeViewModel.StartCommand.CanExecute(null))
        {
            // The engine refused the start. Saying why beats a button that appears to do nothing.
            _operationError = string.Format(
                Strings.GetString("HomeStartUnavailable"),
                Strings.GetString(EngineStatusPresenter.ResourceKeyFor(RuntimeViewModel.EngineStatus)));
            Bindings.Update();
            return;
        }

        _operationError = string.Empty;
        await RuntimeViewModel.StartCommand.ExecuteAsync(null);
        Bindings.Update();
    }

    private async void OnExportProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId } || profileId == Guid.Empty)
        {
            return;
        }

        ProfileCard? profile = ProfilesViewModel.Profiles
            .FirstOrDefault(candidate => candidate.ProfileId == profileId);
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(Strings.GetString("HomeProfileFileType"), [".itrprofile"]);
        picker.SuggestedFileName = profile?.Name ?? "profile";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await using Stream stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await App.GetService<IProfileService>().ExportAsync(profileId, stream);
            _operationError = string.Empty;
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
        }
        Bindings.Update();
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId } || profileId == Guid.Empty)
        {
            return;
        }

        ProfileCard? profile = ProfilesViewModel.Profiles
            .FirstOrDefault(candidate => candidate.ProfileId == profileId);
        bool confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogOptions(
            Strings.GetString("HomeDeleteProfileTitle"),
            string.Format(Strings.GetString("HomeDeleteProfileBody"), profile?.Name ?? string.Empty),
            Strings.GetString("HomeDeleteProfileConfirm"),
            Strings.GetString("HomeDeleteProfileCancel"),
            IsDestructive: true));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await ProfilesViewModel.DeleteAsync(profileId);
            await ProfilesViewModel.InitializeAsync();
            await RemovePinnedProfileAsync(profileId);
            ApplyPinnedProfiles();
            _operationError = string.Empty;
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
        }
        Bindings.Update();
    }

    // A deleted profile must not linger in the pinned list, or it would keep occupying a sort slot
    // that nothing can ever fill again.
    private async Task RemovePinnedProfileAsync(Guid profileId)
    {
        if (_settings is null || !_settings.EffectivePinnedProfileIds.Contains(profileId))
        {
            return;
        }

        _settings = _settings with
        {
            PinnedProfileIds = [.. _settings.EffectivePinnedProfileIds.Where(id => id != profileId)],
        };
        await _settingsService.UpdateAsync(_settings);
    }

    private void OnViewActivityClick(object sender, RoutedEventArgs e) =>
        _navigation.Navigate(GlobalDestination.Activity);

    private async void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId } || profileId == Guid.Empty)
        {
            return;
        }

        if (_settings is null)
        {
            // Settings failed to load earlier; pinning cannot be persisted. Say so instead of
            // absorbing the click.
            _operationError = Strings.GetString("HomePinUnavailable");
            Bindings.Update();
            return;
        }

        try
        {
            var pinned = _settings.EffectivePinnedProfileIds.ToHashSet();
            if (!pinned.Add(profileId))
            {
                pinned.Remove(profileId);
            }
            _settings = _settings with
            {
                PinnedProfileIds = [.. pinned.Order()],
            };
            await _settingsService.UpdateAsync(_settings);
            _operationError = string.Empty;
            ApplyPinnedProfiles();
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
        }
        Bindings.Update();
    }

    private void ApplyPinnedProfiles()
    {
        IReadOnlySet<Guid> pinned = (_settings?.EffectivePinnedProfileIds ?? [])
            .ToHashSet();
        ProfileCard[] ordered = ProfilesViewModel.Profiles
            .Select(profile => profile with { IsPinned = pinned.Contains(profile.ProfileId) })
            .OrderByDescending(profile => profile.IsPinned)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        // Reconciled in place rather than cleared and refilled: a full reset tears down every card
        // container, so pinning one profile would throw away the scroll position and keyboard focus
        // of a list the user is still reading.
        ObservableCollection<ProfileCard> cards = ProfilesViewModel.Profiles;
        for (int index = 0; index < ordered.Length; index++)
        {
            ProfileCard desired = ordered[index];
            int current = IndexOfProfile(cards, desired.ProfileId, index);
            if (current < 0)
            {
                cards.Insert(index, desired);
                continue;
            }

            if (current != index)
            {
                cards.Move(current, index);
            }

            if (cards[index] != desired)
            {
                cards[index] = desired;
            }
        }

        while (cards.Count > ordered.Length)
        {
            cards.RemoveAt(cards.Count - 1);
        }
    }

    private static int IndexOfProfile(
        IReadOnlyList<ProfileCard> cards,
        Guid profileId,
        int startIndex)
    {
        for (int index = startIndex; index < cards.Count; index++)
        {
            if (cards[index].ProfileId == profileId)
            {
                return index;
            }
        }

        return -1;
    }

    private async void OnImportProfileClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".itrprofile");
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await using Stream stream = await file.OpenStreamForReadAsync();
            await App.GetService<IProfileService>().ImportAsync(stream);
            await ProfilesViewModel.InitializeAsync();
            ApplyPinnedProfiles();
            _operationError = string.Empty;
        }
        catch (Exception exception)
        {
            _operationError = exception.Message;
        }
        Bindings.Update();
    }
}
