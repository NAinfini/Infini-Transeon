using InfiniTranseon.App.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using InfiniTranseon.Contracts.Runtime;
using System.Runtime.InteropServices;
using Windows.Graphics;
using InfiniTranseon.App.State;
using InfiniTranseon.App.Tray;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Core.Diagnostics;

namespace InfiniTranseon.App.Shell;

public sealed partial class AppShell : Window
{
    private const double DefaultWindowWidthEpx = 1280;
    private const double DefaultWindowHeightEpx = 800;
    private const double MinimumWindowWidthEpx = 960;
    private const double MinimumWindowHeightEpx = 600;
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private readonly IAppUpdateService _updateService;
    private readonly IRuntimeControlService _runtime;
    private readonly AppNavigationState _navigation;
    private readonly ISettingsService _settingsService;
    private readonly TrayHost _tray;
    private readonly Controls.Dialogs.DialogService _dialogs;
    private readonly nint _windowHandle;
    private NavigationViewItem? _lastSelectedNavItem;
    private bool _windowSizeInitialized;
    private bool _isApplyingMinimumSize;
    private Guid _currentProfileId;
    private bool _suppressNavigationSelection;
    private bool _allowExit;
    private bool _closePromptActive;
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["home"] = typeof(Features.Home.HomePage),
        ["workspace-home"] = typeof(Features.Home.HomePage),
        ["overview"] = typeof(Features.Workspace.ProfileWorkspacePage),
        ["capture"] = typeof(Features.Workspace.ProfileWorkspacePage),
        ["channels"] = typeof(Features.Workspace.ProfileWorkspacePage),
        ["overlay"] = typeof(Features.Workspace.ProfileWorkspacePage),
        ["language"] = typeof(Features.Workspace.ProfileWorkspacePage),
        ["history"] = typeof(Features.Workspace.ProfileWorkspacePage),
        ["activity"] = typeof(Features.Activity.ActivityPage),
        ["providers"] = typeof(Features.Settings.ServicesModelsPage),
        ["settings"] = typeof(Features.Settings.SettingsPage),
    };

    public AppShell()
    {
        _updateService = App.GetService<IAppUpdateService>();
        _runtime = App.GetService<IRuntimeControlService>();
        _navigation = App.GetService<AppNavigationState>();
        _settingsService = App.GetService<ISettingsService>();
        NavigationMap.EnsureConsistent(Pages.Keys);
        InitializeComponent();
        _dialogs = new Controls.Dialogs.DialogService(() => ContentFrame.XamlRoot);
        _lastSelectedNavItem = GlobalHomeItem;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        TitleBarArea.Loaded += OnTitleBarLoaded;
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;
        ContentFrame.Navigated += OnContentFrameNavigated;
        ContentFrame.Navigate(typeof(Features.Home.HomePage));
        _updateService.Changed += OnUpdateChanged;
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _runtime.TargetsChanged += OnRuntimeTargetsChanged;
        _navigation.NavigationRequested += OnNavigationRequested;
        _tray = new TrayHost(
            _runtime,
            App.GetService<AppStatusLog>(),
            DispatcherQueue);
        _tray.OpenRequested += OnTrayOpenRequested;
        _tray.ExitRequested += OnTrayExitRequested;
        Closed += OnClosed;
        RefreshUpdateNotice();
        RefreshRuntimeStatus();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _updateService.Changed -= OnUpdateChanged;
        _runtime.StatusChanged -= OnRuntimeStatusChanged;
        _runtime.TargetsChanged -= OnRuntimeTargetsChanged;
        _navigation.NavigationRequested -= OnNavigationRequested;
        ContentFrame.Navigated -= OnContentFrameNavigated;
        TitleBarArea.Loaded -= OnTitleBarLoaded;
        AppWindow.Changed -= OnAppWindowChanged;
        AppWindow.Closing -= OnAppWindowClosing;
        _tray.OpenRequested -= OnTrayOpenRequested;
        _tray.ExitRequested -= OnTrayExitRequested;
        _tray.Dispose();
    }

    private async void OnAppWindowClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowExit)
        {
            return;
        }

        args.Cancel = true;
        if (_closePromptActive)
        {
            return;
        }

        _closePromptActive = true;
        try
        {
            ApplicationSettings settings = await _settingsService.GetSettingsAsync();
            if (!settings.CloseToTray)
            {
                RequestExit();
                return;
            }

            if (!settings.CloseToTrayConfirmed)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = ContentFrame.XamlRoot,
                    Title = Strings.GetString("CloseToTrayTitle"),
                    Content = Strings.GetString("CloseToTrayBody"),
                    PrimaryButtonText = Strings.GetString("CloseToTrayKeepRunning"),
                    SecondaryButtonText = Strings.GetString("CloseToTrayExit"),
                    CloseButtonText = Strings.GetString("CloseToTrayCancel"),
                    DefaultButton = ContentDialogButton.Primary,
                };
                ContentDialogResult result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Secondary)
                {
                    // The prompt is asked once. Choosing "exit" is the answer to the same question the
                    // "keep running" branch persists, so it must be recorded too — otherwise the
                    // one-time prompt would reappear on every future session.
                    await _settingsService.UpdateAsync(settings with
                    {
                        CloseToTray = false,
                        CloseToTrayConfirmed = true,
                    });
                    RequestExit();
                    return;
                }
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }

                await _settingsService.UpdateAsync(settings with
                {
                    CloseToTray = true,
                    CloseToTrayConfirmed = true,
                });
            }

            AppWindow.Hide();
        }
        catch (Exception exception)
        {
            App.GetService<AppStatusLog>().Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "app.lifecycle",
                "app.closeToTray.failed",
                "status.app.closeToTray.failed",
                StatusEventSeverity.Error,
                new Dictionary<string, object?>
                {
                    ["error"] = exception.Message,
                }));
        }
        finally
        {
            _closePromptActive = false;
        }
    }

    private void OnTrayOpenRequested(object? sender, EventArgs e)
    {
        AppWindow.Show();
        Activate();
    }

    /// <summary>
    /// Applies a deep link (protocol or command line) to the running shell: raise the window, route to
    /// the requested workspace section, and start the profile when the link asked for it. A malformed
    /// link is reported as an error event rather than being treated as a plain launch.
    /// </summary>
    internal async void ApplyActivationRoute(Deployment.AppActivationParseResult activation)
    {
        if (activation.Status == Deployment.AppActivationParseStatus.None)
        {
            return;
        }

        AppWindow.Show();
        Activate();
        if (activation.Status == Deployment.AppActivationParseStatus.Invalid ||
            activation.Route is not Deployment.AppActivationRoute route)
        {
            App.RecordActivationEvent(
                "app.activation.rejected",
                StatusEventSeverity.Error,
                activation.ErrorCode ?? "activation.unknown",
                activation.ErrorDetail ?? string.Empty);
            return;
        }

        _navigation.NavigateToProfile(route.ProfileId, route.Section);
        if (!route.StartRequested)
        {
            return;
        }

        try
        {
            await _runtime.StartAsync(route.ProfileId);
        }
        catch (Exception exception)
        {
            App.RecordActivationEvent(
                "app.activation.startFailed",
                StatusEventSeverity.Error,
                "activation.start.failed",
                exception.Message);
        }
    }

    private void OnTrayExitRequested(object? sender, EventArgs e) => RequestExit();

    private void RequestExit()
    {
        _allowExit = true;
        Close();
    }

    private void OnTitleBarLoaded(object sender, RoutedEventArgs e)
    {
        if (_windowSizeInitialized)
        {
            return;
        }

        _windowSizeInitialized = true;
        AppWindow.Resize(new SizeInt32(
            ScaleToPixels(DefaultWindowWidthEpx),
            ScaleToPixels(DefaultWindowHeightEpx)));
    }

    private void OnAppWindowChanged(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _isApplyingMinimumSize)
        {
            return;
        }

        int minimumWidth = ScaleToPixels(MinimumWindowWidthEpx);
        int minimumHeight = ScaleToPixels(MinimumWindowHeightEpx);
        int width = Math.Max(sender.Size.Width, minimumWidth);
        int height = Math.Max(sender.Size.Height, minimumHeight);
        if (width == sender.Size.Width && height == sender.Size.Height)
        {
            return;
        }

        _isApplyingMinimumSize = true;
        try
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
        finally
        {
            _isApplyingMinimumSize = false;
        }
    }

    private int ScaleToPixels(double effectivePixels)
    {
        uint dpi = GetDpiForWindow(_windowHandle);
        double scale = dpi == 0 ? 1 : dpi / 96d;
        return checked((int)Math.Round(effectivePixels * scale, MidpointRounding.AwayFromZero));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    private void OnUpdateChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshUpdateNotice);

    private void OnRuntimeStatusChanged(object? sender, EngineRuntimeStatusChange args) =>
        DispatcherQueue.TryEnqueue(RefreshRuntimeStatus);

    private void OnRuntimeTargetsChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshRuntimeStatus);

    private void RefreshRuntimeStatus()
    {
        RunningTarget? target = _runtime.GetRunningTargets().FirstOrDefault();
        bool active = _runtime.Status == EngineRuntimeStatus.Running;
        string status = active
            ? _runtime.IsPaused
                ? Strings.GetString("RuntimeStatusPaused")
                : Strings.GetString("RuntimeStatusRunning")
            : _runtime.Status is EngineRuntimeStatus.Locating or
                EngineRuntimeStatus.Starting or
                EngineRuntimeStatus.Restarting or
                EngineRuntimeStatus.Stopping
                ? Strings.GetString("RuntimeStatusTransitioning")
                : _runtime.Status is EngineRuntimeStatus.Faulted or
                    EngineRuntimeStatus.ExecutableNotFound
                    ? Strings.GetString("RuntimeStatusNeedsAttention")
                    : Strings.GetString("RuntimeStatusStopped");
        RuntimeStatusText.Text = target is null
            ? status
            : $"{target.ProfileName} · {status}";
        RuntimeStatusPill.Background = (Brush)Application.Current.Resources[
            active ? "AccentDefault" : "SurfaceSunken"];
        RuntimeStatusGlyph.Foreground = (Brush)Application.Current.Resources[
            active ? "AccentText" : "TextFillColorTertiaryBrush"];
        RuntimeStatusGlyph.Glyph = _runtime.Status switch
        {
            EngineRuntimeStatus.Running when _runtime.IsPaused => "",
            EngineRuntimeStatus.Running => "",
            EngineRuntimeStatus.Locating or EngineRuntimeStatus.Starting or
                EngineRuntimeStatus.Restarting or EngineRuntimeStatus.Stopping => "",
            EngineRuntimeStatus.Faulted or EngineRuntimeStatus.ExecutableNotFound => "",
            _ => "",
        };
    }

    private void RefreshUpdateNotice()
    {
        AppUpdateSnapshot update = _updateService.Snapshot;
        UpdateNotice.IsOpen = update.Status == AppUpdateStatus.Available;
        UpdateNotice.Message = string.Format(
            Strings.GetString(
                update.InstallerIsAuthenticodeSigned
                    ? "UpdateAvailableNotice/Message"
                    : "UpdateAvailableUnsignedNotice"),
            update.AvailableVersion ?? string.Empty);
        UpdateNotice.Severity = update.InstallerIsAuthenticodeSigned
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Warning;
    }

    private void OnReviewUpdateClick(object sender, RoutedEventArgs e)
    {
        NavigationViewItem? settingsItem = Nav.FooterMenuItems
            .OfType<NavigationViewItem>()
            .SingleOrDefault(item => item.Tag as string == "settings");
        if (settingsItem is not null)
            Nav.SelectedItem = settingsItem;
    }

    private void OnRuntimeStatusClick(object sender, RoutedEventArgs e) =>
        _navigation.Navigate(GlobalDestination.Home);

    /// <summary>
    /// Gate for leaving the profile workspace with unsaved edits. Section switches keep the shared
    /// draft and pass through; anything that leaves the workspace, or opens a different profile, asks
    /// first and — on confirmation — actually discards the edits so no stale draft can be saved later.
    /// </summary>
    private async Task<bool> AllowLeaveWorkspaceAsync(Guid targetProfileId)
    {
        if (_currentProfileId == Guid.Empty || targetProfileId == _currentProfileId)
        {
            return true;
        }

        Presentation.ViewModels.WorkbenchViewModel workbench =
            App.GetService<Presentation.ViewModels.WorkbenchViewModel>();
        if (!workbench.IsDirty)
        {
            return true;
        }

        bool discard = await _dialogs.ConfirmAsync(new Controls.Dialogs.ConfirmDialogOptions(
            Strings.GetString("WorkbenchUnsavedTitle"),
            Strings.GetString("WorkbenchUnsavedMessage"),
            Strings.GetString("WorkbenchDiscard"),
            Strings.GetString("WorkbenchKeepEditing"),
            IsDestructive: true));
        if (!discard)
        {
            return false;
        }

        await workbench.DiscardChangesAsync();
        return true;
    }

    private void RestoreNavigationSelection()
    {
        _suppressNavigationSelection = true;
        try
        {
            Nav.SelectedItem = _lastSelectedNavItem;
        }
        finally
        {
            _suppressNavigationSelection = false;
        }
    }

    private void OnNavigationRequested(object? sender, AppNavigationRequest request) =>
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!await AllowLeaveWorkspaceAsync(request.IsWorkspace ? request.ProfileId : Guid.Empty))
            {
                return;
            }

            if (request.IsWorkspace)
            {
                _currentProfileId = request.ProfileId;
                SetWorkspaceNavigation(true);
                WorkspaceSection section = request.WorkspaceSection ?? WorkspaceSection.Overview;
                ContentFrame.Navigate(
                    typeof(Features.Workspace.ProfileWorkspacePage),
                    new ProfileWorkspaceNavigation(request.ProfileId, section),
                    new EntranceNavigationTransitionInfo());
                _suppressNavigationSelection = true;
                try
                {
                    Nav.SelectedItem = WorkspaceItemFor(section);
                }
                finally
                {
                    _suppressNavigationSelection = false;
                }
                return;
            }

            string tag = request.GlobalDestination switch
            {
                GlobalDestination.Home => "home",
                GlobalDestination.Activity => "activity",
                GlobalDestination.Providers => "providers",
                GlobalDestination.Settings => "settings",
                _ => "home",
            };
            _currentProfileId = Guid.Empty;
            SetWorkspaceNavigation(false);
            NavigationViewItem? item = Nav.MenuItems
                .Concat(Nav.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .SingleOrDefault(candidate => candidate.Tag as string == tag);
            if (item is not null && !ReferenceEquals(Nav.SelectedItem, item))
            {
                Nav.SelectedItem = item;
                return;
            }

            // The pane already points at the destination, and assigning the same item raises no
            // SelectionChanged — the frame would keep showing the page that asked to leave. That is
            // what stranded the setup wizard on screen after "save draft and exit": the profile was
            // written and nothing moved.
            if (Pages.TryGetValue(tag, out Type? pageType) &&
                ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            }
        });

    // NavigationView owns the back affordance: enabling the button also enables its built-in Alt+Left
    // and mouse back-button gestures, so the shell only has to keep IsBackEnabled in sync and restore
    // the pane selection for the entry the frame returned to.
    private void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
        if (e.NavigationMode != NavigationMode.Back)
        {
            return;
        }

        _suppressNavigationSelection = true;
        try
        {
            if (e.Parameter is ProfileWorkspaceNavigation workspace)
            {
                _currentProfileId = workspace.ProfileId;
                SetWorkspaceNavigation(true);
                Nav.SelectedItem = WorkspaceItemFor(workspace.Section);
                return;
            }

            _currentProfileId = Guid.Empty;
            SetWorkspaceNavigation(false);
            string tag = e.SourcePageType switch
            {
                Type page when page == typeof(Features.Activity.ActivityPage) => "activity",
                Type page when page == typeof(Features.Settings.ServicesModelsPage) => "providers",
                Type page when page == typeof(Features.Settings.SettingsPage) => "settings",
                _ => "home",
            };
            NavigationViewItem? item = Nav.MenuItems
                .Concat(Nav.FooterMenuItems)
                .OfType<NavigationViewItem>()
                .FirstOrDefault(candidate => candidate.Tag as string == tag);
            if (item is not null)
            {
                Nav.SelectedItem = item;
            }
        }
        finally
        {
            _suppressNavigationSelection = false;
        }
    }

    private static WorkspaceSection? WorkspaceSectionFor(string tag) => tag switch
    {
        "overview" => WorkspaceSection.Overview,
        "capture" => WorkspaceSection.Capture,
        "channels" => WorkspaceSection.Channels,
        "overlay" => WorkspaceSection.Overlay,
        "language" => WorkspaceSection.Language,
        "history" => WorkspaceSection.History,
        _ => null,
    };

    private NavigationViewItem WorkspaceItemFor(WorkspaceSection section) => section switch
    {
        WorkspaceSection.Overview => WorkspaceOverviewItem,
        WorkspaceSection.Capture => WorkspaceCaptureItem,
        WorkspaceSection.Channels => WorkspaceChannelsItem,
        WorkspaceSection.Overlay => WorkspaceOverlayItem,
        WorkspaceSection.Language => WorkspaceLanguageItem,
        WorkspaceSection.History => WorkspaceHistoryItem,
        _ => WorkspaceOverviewItem,
    };

    private void SetWorkspaceNavigation(bool isWorkspace)
    {
        GlobalHomeItem.Visibility = isWorkspace ? Visibility.Collapsed : Visibility.Visible;
        Visibility workspaceVisibility = isWorkspace ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceHomeItem.Visibility = workspaceVisibility;
        WorkspaceOverviewItem.Visibility = workspaceVisibility;
        WorkspaceCaptureItem.Visibility = workspaceVisibility;
        WorkspaceChannelsItem.Visibility = workspaceVisibility;
        WorkspaceOverlayItem.Visibility = workspaceVisibility;
        WorkspaceLanguageItem.Visibility = workspaceVisibility;
        WorkspaceHistoryItem.Visibility = workspaceVisibility;
    }

    private async void OnNavSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag } selected)
        {
            return;
        }

        if (_suppressNavigationSelection)
        {
            _lastSelectedNavItem = selected;
            return;
        }

        // A section of the profile already open keeps the shared draft; everything else leaves it.
        bool staysInWorkspace = _currentProfileId != Guid.Empty && WorkspaceSectionFor(tag) is not null;
        if (!await AllowLeaveWorkspaceAsync(staysInWorkspace ? _currentProfileId : Guid.Empty))
        {
            RestoreNavigationSelection();
            return;
        }

        _lastSelectedNavItem = selected;
        if (tag == "workspace-home")
        {
            _navigation.Navigate(GlobalDestination.Home);
            return;
        }

        WorkspaceSection? section = WorkspaceSectionFor(tag);
        if (section is not null && _currentProfileId != Guid.Empty)
        {
            ContentFrame.Navigate(
                typeof(Features.Workspace.ProfileWorkspacePage),
                new ProfileWorkspaceNavigation(_currentProfileId, section.Value),
                new EntranceNavigationTransitionInfo());
            return;
        }

        if (Pages.TryGetValue(tag, out var pageType)
            && ContentFrame.CurrentSourcePageType != pageType)
        {
            _currentProfileId = Guid.Empty;
            SetWorkspaceNavigation(false);
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }
    }
}
