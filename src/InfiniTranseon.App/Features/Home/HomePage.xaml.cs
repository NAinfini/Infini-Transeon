using System.Collections.ObjectModel;
using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Controls.Dialogs;
using InfiniTranseon.App.Features.SetupWizard;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InfiniTranseon.App.Features.Home;

public sealed partial class HomePage : Page
{
    // Resolved per lookup so a UI language change takes effect without restarting; see AppStrings.
    private static ResourceLoader Strings => Localization.AppStrings.Loader;
    private readonly AppNavigationState _navigation;
    private readonly RuntimeEventHub _runtimeEvents;
    private readonly ISettingsService _settingsService;
    private readonly DialogService _dialogs;
    private readonly IStillFrameProbe _stillFrames;
    private readonly IRuntimeControlService _runtime;
    // One frame per profile for as long as this page lives. Re-grabbing on every scroll would call
    // PrintWindow at the user's scroll rate, and the frame does not go stale fast enough to justify it.
    private readonly Dictionary<Guid, ImageSource> _thumbnails = [];
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
        _stillFrames = App.GetService<IStillFrameProbe>();
        _runtime = App.GetService<IRuntimeControlService>();
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
    public Visibility RunningPanelVisibility => RuntimeViewModel.CanControlRuntime
        ? Visibility.Visible
        : Visibility.Collapsed;
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
        DispatcherQueue.TryEnqueue(() =>
        {
            Bindings.Update();
            RefreshProfileStartButtons();
        });

    /// <summary>
    /// One engine serves one profile at a time, so while it is running no card can start anything.
    /// The button says so by being disabled instead of accepting the click and then explaining that
    /// it did nothing.
    /// </summary>
    private bool CanStartAnyProfile => RuntimeViewModel.EngineStatus
        is EngineRuntimeStatus.Stopped
        or EngineRuntimeStatus.Faulted
        or EngineRuntimeStatus.ExecutableNotFound;

    private void RefreshProfileStartButtons()
    {
        for (int index = 0; index < ProfilesViewModel.Profiles.Count; index++)
        {
            if (ProfileRepeater.TryGetElement(index) is FrameworkElement element &&
                element.FindName("StartProfileButton") is Button start)
            {
                start.IsEnabled = CanStartAnyProfile;
            }
        }
    }

    /// <summary>
    /// Fills in the card as it is realized: the engine's own frame when it is capturing this target,
    /// otherwise one grabbed in-process. Neither is available for a target this machine is not
    /// showing, and the card then keeps the target-kind icon rather than a stale or invented picture.
    /// </summary>
    private async void OnProfileCardPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element ||
            ProfilesViewModel.Profiles.ElementAtOrDefault(args.Index) is not { } card)
        {
            return;
        }

        if (element.FindName("StartProfileButton") is Button start)
        {
            start.IsEnabled = CanStartAnyProfile;
        }

        if (element.FindName("ThumbnailImage") is not Image image ||
            element.FindName("ThumbnailFallbackIcon") is not FontIcon icon)
        {
            return;
        }

        void Show(ImageSource source)
        {
            image.Source = source;
            image.Visibility = Visibility.Visible;
            icon.Visibility = Visibility.Collapsed;
        }

        image.Source = null;
        image.Visibility = Visibility.Collapsed;
        icon.Visibility = Visibility.Visible;
        if (_thumbnails.TryGetValue(card.ProfileId, out ImageSource? cached))
        {
            Show(cached);
            return;
        }

        if (card.TargetNativeHandle == 0 || card.TargetProbeKind.Length == 0)
        {
            return;
        }

        ImageSource? frame = await LoadThumbnailAsync(card);
        if (frame is null)
        {
            return;
        }

        _thumbnails[card.ProfileId] = frame;
        // The card can be scrolled out and reused for another profile while the frame is decoding.
        if (ProfilesViewModel.Profiles.ElementAtOrDefault(sender.GetElementIndex(element))?.ProfileId
            == card.ProfileId)
        {
            Show(frame);
        }
    }

    private async Task<ImageSource?> LoadThumbnailAsync(ProfileCard card)
    {
        if (card.TargetProbeId != Guid.Empty)
        {
            try
            {
                if (await _runtime.RequestThumbnailAsync(card.TargetProbeId, 480) is { } thumbnail)
                {
                    return await CapturePreviewImaging.FromThumbnailAsync(thumbnail);
                }
            }
            catch (Exception)
            {
                // Only the running profile's target has an engine frame; every other card falls
                // through to the in-process probe, whose own failure leaves the icon in place.
            }
        }

        try
        {
            StillFrameProbeResult frame = await _stillFrames.CaptureAsync(
                new StillFrameProbeRequest(card.TargetNativeHandle, card.TargetProbeKind, 480),
                CancellationToken.None);
            return await CapturePreviewImaging.FromStillFrameAsync(frame);
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    // Home names the subsystem and says what happened in the user's language. The engine's own
    // sentence and its error code are diagnostic vocabulary and stay on the activity page; a player
    // who sees this card while a game is running needs to know whether to act, not which method threw.
    private void InsertDiagnostic(RuntimeDiagnosticRaised diagnostic)
    {
        RecentActivity.Insert(0, new HomeActivityItem(
            diagnostic.OccurredAtUtc.ToLocalTime(),
            Strings.GetString(RuntimeDiagnosticPresenter.CategoryResourceKeyFor(diagnostic.ErrorCode)),
            Strings.GetString(RuntimeDiagnosticPresenter.ResourceKeyFor(diagnostic.ErrorCode)),
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
