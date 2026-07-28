using System.Diagnostics;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.Theme;
using InfiniTranseon.App.Hotkeys;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.System;
using Windows.UI.Core;

namespace InfiniTranseon.App.Features.Settings;

public sealed partial class SettingsPage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private bool _loading;
    private bool _updatingHotkeyScope;
    private IReadOnlyList<ProfileTargetDirectoryEntry> _targetDirectory = [];
    private bool _targetDirectoryAvailable;
    private string? _pendingSection;

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
        // Selecting the first section here rather than in XAML: SelectionChanged fires while the
        // markup is still being parsed, so ShowSection would dereference panels that the parser has
        // not created yet and take the whole page down with a NullReferenceException.
        SettingsSectionList.SelectedIndex = 0;
    }

    public SettingsViewModel ViewModel { get; }
    public string SettingsTitle => Resource("SettingsTitle.Text");
    public string SettingsSubtitle => Resource("SettingsSubtitle.Text");

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingSection = e.Parameter as string;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            await ViewModel.InitializeAsync();
            ApplicationSettings settings = ViewModel.Settings;
            ThemeSelector.SelectedIndex = settings.Theme switch
            {
                UiThemePreference.Light => 1,
                UiThemePreference.Dark => 2,
                _ => 0,
            };
            LanguageSelector.SelectedIndex = string.Equals(settings.UiLanguage, "zh-CN", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            OfflineToggle.IsOn = settings.StrictOffline;
            ReducedMotionToggle.IsOn = settings.ReducedMotion;
            CloseToTrayToggle.IsOn = settings.CloseToTray;
            RetentionSelector.SelectedIndex = settings.HistoryRetention switch
            {
                HistoryRetention.Off => 0,
                HistoryRetention.Days90 => 2,
                _ => 1,
            };
            PerformanceSelector.SelectedIndex = settings.PerformancePreset switch
            {
                AppPerformancePreset.Eco => 0,
                AppPerformancePreset.Performance => 2,
                _ => 1,
            };
            OcrBackendSelector.SelectedIndex = settings.OcrBackend switch
            {
                AppOcrBackend.Windows => 1,
                AppOcrBackend.Local => 2,
                _ => 0,
            };
            await LoadTargetDirectoryAsync();
            RefreshUpdateUi();
            RefreshHotkeyRows();

            if (_pendingSection is not null)
            {
                ListViewItem? match = SettingsSectionList.Items
                    .OfType<ListViewItem>()
                    .FirstOrDefault(item => Equals(item.Tag, _pendingSection));
                if (match is not null)
                {
                    ShowSection(_pendingSection);
                    SettingsSectionList.SelectedItem = match;
                }
                _pendingSection = null;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        var (preference, mode) = ThemeSelector.SelectedIndex switch
        {
            1 => (UiThemePreference.Light, ThemeMode.Light),
            2 => (UiThemePreference.Dark, ThemeMode.Dark),
            _ => (UiThemePreference.System, ThemeMode.System),
        };

        if (XamlRoot?.Content is FrameworkElement root)
        {
            ThemeService.Instance.Apply(mode, root);
        }

        if (!_loading)
        {
            await ViewModel.UpdateThemeAsync(preference);
        }
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        string language = LanguageSelector.SelectedIndex == 1 ? "zh-CN" : "en-US";
        await ViewModel.UpdateLanguageAsync(language);
        // Persisting alone changed nothing before: no code ever applied the stored language, so the
        // setting looked accepted while the UI stayed in its launch language. The override takes
        // effect for resources resolved from here on; already-built pages keep their current strings,
        // which is why the restart notice is shown rather than implied.
        App.ApplyUiLanguage(language);
        LanguageRestartBar.IsOpen = true;
    }

    private async void OnOfflineToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            await ViewModel.UpdateStrictOfflineAsync(OfflineToggle.IsOn);
        }
    }

    private async void OnRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        HistoryRetention retention = RetentionSelector.SelectedIndex switch
        {
            0 => HistoryRetention.Off,
            2 => HistoryRetention.Days90,
            _ => HistoryRetention.Days30,
        };
        await ViewModel.UpdateHistoryRetentionAsync(retention);
    }

    private async void OnOcrBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppOcrBackend backend = OcrBackendSelector.SelectedIndex switch
        {
            1 => AppOcrBackend.Windows,
            2 => AppOcrBackend.Local,
            _ => AppOcrBackend.Automatic,
        };
        await ViewModel.UpdateOcrBackendAsync(backend);
    }

    private async void OnReducedMotionToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            await ViewModel.UpdateReducedMotionAsync(ReducedMotionToggle.IsOn);
        }
    }

    private async void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            await ViewModel.UpdateCloseToTrayAsync(
                CloseToTrayToggle.IsOn,
                confirmed: true);
        }
    }

    private void OnSettingsSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsSearchBox.Text.Length == 0 &&
            e.AddedItems.Count > 0 &&
            e.AddedItems[0] is FrameworkElement { Tag: string tag })
        {
            ShowSection(tag);
        }
    }

    private void OnSettingsSearchChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            return;
        string query = sender.Text.Trim();
        if (query.Length == 0)
        {
            SettingsSectionList.Visibility = Visibility.Visible;
            SettingsSearchEmptyState.Visibility = Visibility.Collapsed;
            SetSettingRowsVisible(true);
            string selectedTag = SettingsSectionList.SelectedItem is FrameworkElement { Tag: string tag }
                ? tag
                : "appearance";
            ShowSection(selectedTag);
            return;
        }

        SettingsSectionList.Visibility = Visibility.Collapsed;
        bool theme = SetMatch(ThemeSettingRow, query, "ThemeSettingLabel.Text", "ThemeSettingHint.Text");
        bool language = SetMatch(LanguageSettingRow, query, "LanguageSettingLabel.Text", "LanguageSettingHint.Text");
        bool motion = SetMatch(ReducedMotionSettingRow, query, "ReducedMotionLabel.Text", "ReducedMotionHint.Text");
        bool tray = SetMatch(CloseToTraySettingRow, query, "CloseToTraySettingLabel.Text", "CloseToTraySettingHint.Text");
        bool offline = SetMatch(OfflineSettingRow, query, "OfflineModeLabel.Text", "OfflineModeHint.Text");
        bool history = SetMatch(HistorySettingRow, query, "HistoryRetentionLabel.Text", "HistoryRetentionHint.Text");
        bool performance = SetMatch(PerformanceSettingRow, query, "PerformancePresetLabel.Text", "PerformancePresetHint.Text");
        bool hotkeys = Matches(query, "HotkeysHeader.Text") ||
            ViewModel.Hotkeys.Any(row =>
                SettingsViewModel.MatchesSearch(query, row.ActionText, row.Gesture, row.ScopeText));
        bool about = SetMatch(AboutSettingRow, query, "AboutHeader.Text", "UpdatePolicyHint.Text");

        AppearancePanel.Visibility = theme || language || motion || tray
            ? Visibility.Visible
            : Visibility.Collapsed;
        HotkeysPanel.Visibility = hotkeys ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPanel.Visibility = offline || history ? Visibility.Visible : Visibility.Collapsed;
        PerformancePanel.Visibility = performance ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchEmptyState.Visibility =
            theme || language || motion || tray || offline || history || performance || hotkeys || about
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void ShowSection(string tag)
    {
        AppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPanel.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        PerformancePanel.Visibility = tag == "performance" ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPanel.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSettingRowsVisible(bool visible)
    {
        Visibility value = visible ? Visibility.Visible : Visibility.Collapsed;
        ThemeSettingRow.Visibility = value;
        LanguageSettingRow.Visibility = value;
        ReducedMotionSettingRow.Visibility = value;
        CloseToTraySettingRow.Visibility = value;
        OfflineSettingRow.Visibility = value;
        HistorySettingRow.Visibility = value;
        PerformanceSettingRow.Visibility = value;
        AboutSettingRow.Visibility = value;
    }

    private static bool SetMatch(FrameworkElement element, string query, params string[] keys)
    {
        bool match = Matches(query, keys);
        element.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        return match;
    }

    private static bool Matches(string query, params string[] keys) =>
        SettingsViewModel.MatchesSearch(query, keys.Select(Resource).ToArray());

    private static string Resource(string key) => Strings.GetString(key.Replace('.', '/'));

    private async void OnPerformanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppPerformancePreset preset = PerformanceSelector.SelectedIndex switch
        {
            0 => AppPerformancePreset.Eco,
            2 => AppPerformancePreset.Performance,
            _ => AppPerformancePreset.Balanced,
        };
        await ViewModel.UpdatePerformancePresetAsync(preset);
    }

    private async void OnHotkeyEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_loading ||
            sender is not ToggleSwitch { Tag: HotkeyEditorRow row } toggle)
        {
            return;
        }

        bool previous = row.Enabled;
        row.Enabled = toggle.IsOn;
        await SaveHotkeysAsync(row, () => row.Enabled = previous);
    }

    private void OnHotkeyScopeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { Tag: HotkeyEditorRow row } selector)
        {
            return;
        }
        _updatingHotkeyScope = true;
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), row.Scope.ToString(), StringComparison.Ordinal));
        _updatingHotkeyScope = false;
    }

    private async void OnHotkeyScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _updatingHotkeyScope ||
            sender is not ComboBox { Tag: HotkeyEditorRow row, SelectedItem: ComboBoxItem item } ||
            !Enum.TryParse(item.Tag?.ToString(), out AppHotkeyScope scope) ||
            row.IsScopeFixed || row.Scope == scope)
        {
            return;
        }
        AppHotkeyScope previous = row.Scope;
        row.Scope = scope;
        await SaveHotkeysAsync(row, () => row.Scope = previous);
    }

    private async void OnSelectSpecificTargetsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HotkeyEditorRow row } ||
            !row.CanChooseSpecificTargets)
        {
            return;
        }

        var selected = row.SpecificTargets.ToHashSet();
        var boxes = new List<(CheckBox Box, AppHotkeyTargetReference Target)>();
        var content = new StackPanel { Spacing = 8 };
        foreach (IGrouping<(Guid ProfileId, string ProfileName), ProfileTargetDirectoryEntry> profile in
            _targetDirectory.GroupBy(target => (target.ProfileId, target.ProfileName)))
        {
            content.Children.Add(new TextBlock { Text = profile.Key.ProfileName, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
            foreach (ProfileTargetDirectoryEntry target in profile)
            {
                var reference = new AppHotkeyTargetReference(target.ProfileId, target.ProfileTargetId);
                var box = new CheckBox { Content = target.TargetName, IsChecked = selected.Contains(reference) };
                AutomationProperties.SetName(box, $"{target.ProfileName}: {target.TargetName}");
                boxes.Add((box, reference));
                content.Children.Add(box);
            }
        }

        HashSet<AppHotkeyTargetReference> current = _targetDirectory
            .Select(target => new AppHotkeyTargetReference(target.ProfileId, target.ProfileTargetId))
            .ToHashSet();
        foreach (AppHotkeyTargetReference missing in selected.Where(target => !current.Contains(target)))
        {
            var box = new CheckBox
            {
                Content = string.Format(Strings.GetString("HotkeyMissingTarget"), missing.ProfileId, missing.ProfileTargetId),
                IsChecked = true,
            };
            AutomationProperties.SetName(box, Strings.GetString("HotkeyMissingTargetAutomationName"));
            boxes.Add((box, missing));
            content.Children.Add(box);
        }

        if (boxes.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = Strings.GetString(_targetDirectoryAvailable
                    ? "HotkeyNoTargets"
                    : "HotkeyTargetDirectoryUnavailable"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var dialog = new ContentDialog
        {
            Title = Strings.GetString("HotkeySpecificTargetsDialogTitle"),
            Content = new ScrollViewer { Content = content, MaxHeight = 420 },
            PrimaryButtonText = Strings.GetString("HotkeySpecificTargetsSave"),
            CloseButtonText = Strings.GetString("HotkeyCaptureCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        IReadOnlyList<AppHotkeyTargetReference> previous = row.SpecificTargets;
        row.SpecificTargets = boxes.Where(item => item.Box.IsChecked == true)
            .Select(item => item.Target).ToArray();
        await SaveHotkeysAsync(row, () => row.SpecificTargets = previous);
    }

    private async void OnEditHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HotkeyEditorRow row })
        {
            return;
        }

        ParsedHotkeyGesture? captured = null;
        var captureBox = new TextBox
        {
            IsReadOnly = true,
            Text = row.Gesture,
            Header = Strings.GetString("HotkeyCaptureFieldHeader"),
            PlaceholderText = Strings.GetString("HotkeyCapturePlaceholder"),
            MinWidth = 360,
        };
        var help = new TextBlock
        {
            Text = Strings.GetString("HotkeyCaptureHelp"),
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(captureBox);
        panel.Children.Add(help);
        var dialog = new ContentDialog
        {
            Title = Strings.GetString($"HotkeyAction_{row.Action}"),
            Content = panel,
            PrimaryButtonText = Strings.GetString("HotkeyCaptureSave"),
            CloseButtonText = Strings.GetString("HotkeyCaptureCancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };
        captureBox.KeyDown += (_, args) =>
        {
            args.Handled = true;
            if (HotkeyGesture.IsModifierKey(args.Key))
            {
                return;
            }

            AppHotkeyModifiers modifiers = ReadHotkeyModifiers();
            if (modifiers == AppHotkeyModifiers.None)
            {
                help.Text = Strings.GetString("HotkeyCaptureModifierRequired");
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }

            captured = new ParsedHotkeyGesture(modifiers, args.Key);
            captureBox.Text = captured.Value.DisplayText;
            help.Text = Strings.GetString("HotkeyCaptureReady");
            dialog.IsPrimaryButtonEnabled = true;
        };
        dialog.Opened += (_, _) => captureBox.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            captured is null)
        {
            return;
        }

        string previous = row.Gesture;
        row.Gesture = captured.Value.DisplayText;
        await SaveHotkeysAsync(row, () => row.Gesture = previous);
    }

    private async void OnRestoreHotkeysClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<AppHotkeyBinding> previous =
            ViewModel.Hotkeys.Select(row => row.ToBinding()).ToArray();
        ViewModel.RestoreDefaultHotkeys();
        ApplyHotkeyLabels();
        await SaveHotkeysAsync(
            changedRow: null,
            rollback: () =>
            {
                ViewModel.Hotkeys.Clear();
                foreach (AppHotkeyBinding binding in previous)
                {
                    ViewModel.Hotkeys.Add(new HotkeyEditorRow(binding));
                }
                ApplyHotkeyLabels();
            });
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        SetUpdateBusy(true);
        try
        {
            await ViewModel.CheckForUpdatesAsync();
        }
        finally
        {
            SetUpdateBusy(false);
            RefreshUpdateUi();
        }
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.GetString("UpdateDownloadDialogTitle"),
            Content = string.Format(
                Strings.GetString(
                    ViewModel.UpdateSnapshot.InstallerIsAuthenticodeSigned
                        ? "UpdateDownloadDialogBody"
                        : "UpdateDownloadDialogBodyUnsigned"),
                ViewModel.UpdateSnapshot.AvailableVersion),
            PrimaryButtonText = Strings.GetString("UpdateDownloadDialogConfirm"),
            CloseButtonText = Strings.GetString("UpdateDownloadDialogCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        bool approved = await dialog.ShowAsync() == ContentDialogResult.Primary;
        if (!approved) return;

        SetUpdateBusy(true);
        try
        {
            await ViewModel.DownloadUpdateAsync(userApproved: true);
        }
        finally
        {
            SetUpdateBusy(false);
            RefreshUpdateUi();
        }
    }

    private async void OnOpenInstallerClick(object sender, RoutedEventArgs e)
    {
        string? installerPath = ViewModel.UpdateSnapshot.InstallerPath;
        try
        {
            if (string.IsNullOrWhiteSpace(installerPath) ||
                !System.IO.File.Exists(installerPath))
                throw new FileNotFoundException(
                    Strings.GetString("UpdateInstallerMissing"),
                    installerPath);
            if (!ViewModel.UpdateSnapshot.InstallerIsAuthenticodeSigned)
            {
                var warning = new ContentDialog
                {
                    Title = Strings.GetString("UpdateOpenUnsignedDialogTitle"),
                    Content = Strings.GetString("UpdateOpenUnsignedDialogBody"),
                    PrimaryButtonText = Strings.GetString("UpdateOpenUnsignedDialogConfirm"),
                    CloseButtonText = Strings.GetString("UpdateDownloadDialogCancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                if (await warning.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.GetString("UpdateInstallerOpenFailed"),
                Content = exception.Message,
                CloseButtonText = Strings.GetString("UpdateDialogClose"),
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        UpdateProgress.IsActive = busy;
        UpdateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = !busy;
        DownloadUpdateButton.IsEnabled = !busy;
        OpenInstallerButton.IsEnabled = !busy;
    }

    private void RefreshUpdateUi()
    {
        AppUpdateSnapshot update = ViewModel.UpdateSnapshot;
        VersionText.Text = string.Format(
            Strings.GetString("UpdateCurrentVersion"),
            update.CurrentVersion);
        DownloadUpdateButton.Visibility =
            ViewModel.CanDownloadUpdate ? Visibility.Visible : Visibility.Collapsed;
        OpenInstallerButton.Visibility =
            ViewModel.CanOpenInstaller ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.Visibility =
            ViewModel.CanOpenInstaller ? Visibility.Collapsed : Visibility.Visible;

        (string messageKey, InfoBarSeverity severity, bool isOpen) = update.Status switch
        {
            AppUpdateStatus.UpToDate =>
                ("UpdateStatusUpToDate", InfoBarSeverity.Success, true),
            AppUpdateStatus.Available =>
                (update.InstallerIsAuthenticodeSigned
                    ? "UpdateStatusAvailable"
                    : "UpdateStatusAvailableUnsigned",
                    update.InstallerIsAuthenticodeSigned
                        ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Warning,
                    true),
            AppUpdateStatus.ReadyToInstall =>
                (update.InstallerIsAuthenticodeSigned
                    ? "UpdateStatusReady"
                    : "UpdateStatusReadyUnsigned",
                    update.InstallerIsAuthenticodeSigned
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Warning,
                    true),
            AppUpdateStatus.DisabledByOfflineMode =>
                ("UpdateStatusOffline", InfoBarSeverity.Warning, true),
            AppUpdateStatus.Failed =>
                ("UpdateStatusFailed", InfoBarSeverity.Error, true),
            _ => ("UpdateStatusIdle", InfoBarSeverity.Informational, false),
        };
        UpdateStatusBar.IsOpen = isOpen;
        UpdateStatusBar.Severity = severity;
        UpdateStatusBar.Message = string.Format(
            Strings.GetString(messageKey),
            update.AvailableVersion ?? string.Empty);
    }

    private async Task SaveHotkeysAsync(
        HotkeyEditorRow? changedRow,
        Action rollback)
    {
        await ViewModel.SaveHotkeyRowsAsync();
        if (ViewModel.HasError)
        {
            rollback();
            HotkeyStatusBar.Title = Strings.GetString("HotkeySaveFailedTitle");
            HotkeyStatusBar.Message = ViewModel.ErrorMessage;
            HotkeyStatusBar.Severity = InfoBarSeverity.Error;
            HotkeyStatusBar.IsOpen = true;
            return;
        }

        try
        {
            GlobalHotkeyService service = App.GlobalHotkeys ??
                throw new InvalidOperationException(
                    App.HotkeyInitializationError ??
                    Strings.GetString("HotkeyServiceUnavailable"));
            service.Apply(ViewModel.Settings.EffectiveHotkeys);
            RefreshHotkeyRows();
        }
        catch (Exception exception)
        {
            HotkeyStatusBar.Title = Strings.GetString("HotkeyRegistrationFailedTitle");
            HotkeyStatusBar.Message = exception.Message;
            HotkeyStatusBar.Severity = InfoBarSeverity.Error;
            HotkeyStatusBar.IsOpen = true;
        }
    }

    private void RefreshHotkeyRows()
    {
        ApplyHotkeyLabels();
        IReadOnlyDictionary<AppHotkeyAction, string>? statuses =
            App.GlobalHotkeys?.StatusCodes;
        bool hasProblem = App.GlobalHotkeys is null;
        foreach (HotkeyEditorRow row in ViewModel.Hotkeys)
        {
            int staleTargets = row.SpecificTargets.Count(target => !_targetDirectory.Any(entry =>
                entry.ProfileId == target.ProfileId && entry.ProfileTargetId == target.ProfileTargetId));
            string code = statuses is not null &&
                statuses.TryGetValue(row.Action, out string? current)
                    ? current
                    : row.Enabled ? "unavailable" : "disabled";
            if (row.IsComingSoon)
                code = "comingSoon";
            else if (row.Scope == AppHotkeyScope.SpecificTargetGroup && row.SpecificTargets.Count == 0)
                code = "selectionRequired";
            else if (row.Scope == AppHotkeyScope.SpecificTargetGroup && staleTargets > 0)
                code = "staleTargets";
            else if (row.Scope == AppHotkeyScope.SpecificTargetGroup && !_targetDirectoryAvailable)
                code = "targetDirectoryUnavailable";
            row.StatusText = Strings.GetString($"HotkeyStatus_{code}");
            hasProblem |= code is "conflict" or "invalid" or "unavailable" or "comingSoon" or
                "selectionRequired" or "staleTargets" or "targetDirectoryUnavailable";
        }

        HotkeyStatusBar.IsOpen = hasProblem;
        HotkeyStatusBar.Severity = hasProblem
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success;
        HotkeyStatusBar.Title = hasProblem
            ? Strings.GetString("HotkeyAttentionTitle")
            : Strings.GetString("HotkeyReadyTitle");
        HotkeyStatusBar.Message = hasProblem
            ? App.HotkeyInitializationError ??
                Strings.GetString("HotkeyAttentionMessage")
            : Strings.GetString("HotkeyReadyMessage");
    }

    private void ApplyHotkeyLabels()
    {
        foreach (HotkeyEditorRow row in ViewModel.Hotkeys)
        {
            row.ActionText = Strings.GetString($"HotkeyAction_{row.Action}");
            row.ScopeText = Strings.GetString($"HotkeyScope_{row.Scope}");
        }
    }

    private async Task LoadTargetDirectoryAsync()
    {
        _targetDirectory = [];
        _targetDirectoryAvailable = false;
        if (App.GetService<IProfileService>() is not IProfileTargetDirectory directory)
            return;
        try
        {
            _targetDirectory = await directory.GetTargetsAsync();
            _targetDirectoryAvailable = true;
        }
        catch (Exception)
        {
            // The UI presents the unavailable-directory state rather than fabricating target choices.
        }
    }

    private static AppHotkeyModifiers ReadHotkeyModifiers()
    {
        AppHotkeyModifiers modifiers = AppHotkeyModifiers.None;
        if (IsKeyDown(VirtualKey.Control)) modifiers |= AppHotkeyModifiers.Control;
        if (IsKeyDown(VirtualKey.Menu)) modifiers |= AppHotkeyModifiers.Alt;
        if (IsKeyDown(VirtualKey.Shift)) modifiers |= AppHotkeyModifiers.Shift;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
            modifiers |= AppHotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);
}
