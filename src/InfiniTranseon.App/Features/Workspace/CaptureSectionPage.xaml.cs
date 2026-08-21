using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Controls.Dialogs;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Probes;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace InfiniTranseon.App.Features.Workspace;

/// <summary>
/// Workspace · capture &amp; regions (spec 5.4). Target selector, canvas command bar,
/// RegionListPane | RegionCanvas | a three-group inspector (Basic, OCR, Layout &amp; line
/// breaks), and a status bar. Target-level detection settings live in the target row's
/// expandable "Target settings" area, not in the region inspector.
/// </summary>
public sealed partial class CaptureSectionPage : Page
{
    // Resolved per lookup so a UI language change takes effect without restarting; see AppStrings.
    private static ResourceLoader Strings => Localization.AppStrings.Loader;

    private readonly IRuntimeControlService _runtime;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly ICaptureProbe _captureProbe;
    private readonly IStillFrameProbe _stillFrames;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };
    private bool _updatingInspector;
    private bool _previewInFlight;
    private IReadOnlyList<ProviderRow> _cloudOcrProviders = [];
    private readonly IReadOnlyList<LanguageOption> _recognitionLanguages;
    private string? _selectedOcrProviderId;
    private string? _selectedRecognitionLanguage;
    private Guid _requestedProfileId;

    public CaptureSectionPage()
    {
        ViewModel = App.GetService<WorkbenchViewModel>();
        _runtime = App.GetService<IRuntimeControlService>();
        _profiles = App.GetService<IProfileService>();
        _settings = App.GetService<ISettingsService>();
        _captureProbe = App.GetService<ICaptureProbe>();
        _stillFrames = App.GetService<IStillFrameProbe>();
        _dialogs = new DialogService(() => XamlRoot);
        _recognitionLanguages = LanguageCatalog.CreateSourceOptions(
            Strings.GetString("UiLanguageTag"));
        InitializeComponent();
        _previewTimer.Tick += OnPreviewTimerTick;
    }

    public WorkbenchViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _requestedProfileId = e.Parameter switch
        {
            ProfileWorkspaceNavigation route => route.ProfileId,
            Guid profileId => profileId,
            _ => Guid.Empty,
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Guid profileId = _requestedProfileId;
        if (profileId == Guid.Empty)
        {
            IReadOnlyList<ProfileCard> profiles = await _profiles.GetProfilesAsync();
            profileId = profiles.FirstOrDefault()?.ProfileId ?? Guid.Empty;
        }
        if (profileId == Guid.Empty)
        {
            ShowInfo(
                InfoBarSeverity.Informational,
                Strings.GetString("WorkbenchNoProfileTitle"),
                Strings.GetString("WorkbenchNoProfileMessage"));
            return;
        }

        await ViewModel.EnsureLoadedAsync(profileId);
        if (ViewModel.HasError)
        {
            ShowInfo(
                InfoBarSeverity.Error,
                Strings.GetString("WorkbenchLoadErrorTitle"),
                ViewModel.ErrorMessage);
            return;
        }
        try
        {
            IReadOnlyList<ProviderRow> providers = await _settings.GetProvidersAsync();
            _cloudOcrProviders = providers
                .Where(provider => provider.IsSelectable &&
                    provider.IsOcrProvider &&
                    !provider.IsLocalModel)
                .OrderByDescending(provider => provider.IsReady)
                .ThenBy(provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            ShowInfo(
                InfoBarSeverity.Warning,
                Strings.GetString("WorkbenchOcrCatalogErrorTitle"),
                exception.Message);
        }
        TargetSelector.ItemsSource = ViewModel.Targets;
        TargetSelector.SelectedItem = ViewModel.SelectedTarget;
        RefreshTargetSelection();
        RefreshEditorState();
        _previewTimer.Start();
        await RefreshPreviewAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        // The view model outlives the page (one draft per workspace), so the page must detach or its
        // handler would keep running against unloaded XAML on every later section visit.
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Canvas.PreviewSource = null;
    }

    private void OnTargetSelectionChanged(object? sender, EventArgs e)
    {
        if (TargetSelector.SelectedItem is WorkbenchTargetItem target)
        {
            ViewModel.SelectedTarget = target;
        }
        ClearPreview();
        RefreshTargetSelection();
        _ = RefreshPreviewAsync();
    }

    private void RefreshTargetSelection()
    {
        WorkbenchTargetItem? target = ViewModel.SelectedTarget;
        RegionList.Regions = target?.Regions;
        Canvas.Regions = target?.Regions;
        ViewModel.SelectedRegion = target?.Regions.FirstOrDefault();
        RegionList.SelectedRegion = ViewModel.SelectedRegion;
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RefreshTargetSettings();
        RefreshInspector();
    }

    private void RefreshTargetSettings()
    {
        WorkbenchTargetItem? target = ViewModel.SelectedTarget;
        _updatingInspector = true;
        DetectionLongEdgeBox.Value = target?.DetectionLongEdge ?? double.NaN;
        ScanRemainingAreaToggle.IsOn = target?.ScanRemainingArea ?? false;
        RemainingAreaIntervalBox.Value = target?.RemainingAreaIntervalMilliseconds ?? double.NaN;
        RemainingAreaIntervalBox.IsEnabled = target?.ScanRemainingArea ?? false;
        _updatingInspector = false;
    }

    // -- RegionListPane -------------------------------------------------------------------

    private void OnRegionListSelectionChanged(object sender, WorkbenchRegionItem? region)
    {
        ViewModel.SelectedRegion = region;
        Canvas.SelectedRegion = region;
        RefreshInspector();
    }

    private void OnRegionEnabledChanged(object sender, (WorkbenchRegionItem Region, bool Enabled) e)
    {
        ViewModel.SetRegionEnabled(e.Region, e.Enabled);
        RefreshEditorState();
    }

    private void OnAddRegionRequested(object sender, EventArgs e)
    {
        ViewModel.AddRegion();
        SyncSelectionFromViewModel();
        RefreshEditorState();
    }

    private void OnDuplicateRegionRequested(object sender, EventArgs e)
    {
        ViewModel.DuplicateSelectedRegion();
        SyncSelectionFromViewModel();
        RefreshEditorState();
    }

    private void OnDeleteRegionRequested(object sender, EventArgs e)
    {
        ViewModel.DeleteSelectedRegion();
        SyncSelectionFromViewModel();
        RefreshEditorState();
    }

    private void OnMoveRegionUpRequested(object sender, EventArgs e)
    {
        ViewModel.MoveSelectedRegion(-1);
        RefreshEditorState();
    }

    private void OnMoveRegionDownRequested(object sender, EventArgs e)
    {
        ViewModel.MoveSelectedRegion(1);
        RefreshEditorState();
    }

    private void SyncSelectionFromViewModel()
    {
        RegionList.SelectedRegion = ViewModel.SelectedRegion;
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RefreshInspector();
    }

    // -- RegionCanvas -----------------------------------------------------------------------

    private void OnCanvasSelectionChanged(object sender, WorkbenchRegionItem? region)
    {
        ViewModel.SelectedRegion = region;
        RegionList.SelectedRegion = region;
        RefreshInspector();
    }

    private void OnCanvasRegionDragStarted(object sender, WorkbenchRegionItem region) =>
        ViewModel.BeginEdit();

    private void OnCanvasRegionAdded(object sender, RegionDrawStartedEventArgs e)
    {
        ViewModel.AddRegion();
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RegionList.SelectedRegion = ViewModel.SelectedRegion;
    }

    private void OnCanvasRegionChanged(object sender, WorkbenchRegionItem region)
    {
        RefreshInspector();
        RefreshEditorState();
    }

    private void OnCanvasDrawModeExited(object sender, EventArgs e)
    {
        DrawRegionButton.IsChecked = false;
        Canvas.IsDrawMode = false;
        RefreshEditorState();
    }

    private void OnDrawRegionToggled(object sender, RoutedEventArgs e) =>
        Canvas.IsDrawMode = DrawRegionButton.IsChecked.GetValueOrDefault();

    private void OnZoomFitClick(object sender, RoutedEventArgs e) => Canvas.SetZoomFit();

    // -- Inspector --------------------------------------------------------------------------

    private void RefreshInspector()
    {
        WorkbenchRegionItem? region = ViewModel.SelectedRegion;
        _updatingInspector = true;
        try
        {
            bool enabled = region is not null;
            InspectorPane.IsHitTestVisible = enabled;
            InspectorPane.Opacity = enabled ? 1 : 0.55;
            if (region is null)
            {
                return;
            }

            RegionNameBox.Text = region.Name;
            RegionEnabledToggle.IsOn = region.Enabled;
            RegionPriorityBox.SelectedIndex = (int)region.Priority;
            ContextRoleBox.SelectedIndex = (int)region.ContextRole;
            RegionLockToggle.IsOn = region.LockDegradation;
            _selectedOcrProviderId = region.OcrProviderId;
            _selectedRecognitionLanguage = region.RecognitionLanguage;
            RestoreOcrProviderText();
            RestoreRecognitionLanguageText();
            OcrProviderBox.IsEnabled = region.UseCloudOcr;
            DetectOrientationToggle.IsOn = region.DetectOrientation;
            CloudOcrToggle.IsOn = region.UseCloudOcr;
            RecognitionIntervalBox.Value = region.RecognitionIntervalMilliseconds;
            DetectionScaleBox.Value = region.DetectionScale;
            LineBreakModeBox.SelectedItem = region.LineBreakMode;
            CustomSeparatorBox.Text = region.CustomLineSeparator ?? string.Empty;
            LineAlignmentBox.SelectedItem = region.LineAlignment;
            MaximumLinesBox.Value = region.MaximumLines;
            RegionXBox.Value = region.X;
            RegionYBox.Value = region.Y;
            RegionWidthBox.Value = region.Width;
            RegionHeightBox.Value = region.Height;
        }
        finally
        {
            _updatingInspector = false;
        }
        UpdateGeometryText();
    }

    private void OnInspectorChanged(object sender, RoutedEventArgs e) => CommitInspector();

    private void OnInspectorSelectionChanged(object? sender, EventArgs e) => CommitInspector();

    private void OnInspectorNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        CommitInspector();

    private void CommitInspector()
    {
        if (_updatingInspector || ViewModel.SelectedRegion is not { } region)
        {
            return;
        }

        ViewModel.BeginEdit();
        region.Name = RegionNameBox.Text.Trim();
        region.Enabled = RegionEnabledToggle.IsOn;
        region.Priority = (RegionPriorityLevel)Math.Max(0, RegionPriorityBox.SelectedIndex);
        region.ContextRole = (RegionContextRole)Math.Max(0, ContextRoleBox.SelectedIndex);
        region.LockDegradation = RegionLockToggle.IsOn;
        region.OcrProviderId = _selectedOcrProviderId ?? region.OcrProviderId;
        region.RecognitionLanguage = _selectedRecognitionLanguage ?? region.RecognitionLanguage;
        region.DetectOrientation = DetectOrientationToggle.IsOn;
        region.RecognitionIntervalMilliseconds = IntegerValue(
            RecognitionIntervalBox,
            region.RecognitionIntervalMilliseconds);
        region.DetectionScale = double.IsNaN(DetectionScaleBox.Value)
            ? region.DetectionScale
            : DetectionScaleBox.Value;
        region.LineBreakMode = LineBreakModeBox.SelectedItem as string ?? "PreserveLines";
        region.CustomLineSeparator = string.IsNullOrEmpty(CustomSeparatorBox.Text)
            ? null
            : CustomSeparatorBox.Text;
        region.LineAlignment = LineAlignmentBox.SelectedItem as string ?? "Auto";
        region.MaximumLines = IntegerValue(MaximumLinesBox, region.MaximumLines);
        RefreshEditorState();
    }

    private void OnBoundsNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingInspector || ViewModel.SelectedRegion is not { } region ||
            new[]
            {
                RegionXBox.Value,
                RegionYBox.Value,
                RegionWidthBox.Value,
                RegionHeightBox.Value,
            }.Any(double.IsNaN))
        {
            return;
        }

        ViewModel.SetRegionBounds(
            region,
            RegionXBox.Value,
            RegionYBox.Value,
            RegionWidthBox.Value,
            RegionHeightBox.Value,
            createUndoPoint: true);
        RefreshInspector();
        RefreshEditorState();
    }

    private void OnTargetSettingsChanged(object sender, RoutedEventArgs e) => CommitTargetSettings();

    private void OnTargetNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        CommitTargetSettings();

    private void CommitTargetSettings()
    {
        if (_updatingInspector || ViewModel.SelectedTarget is not { } target)
        {
            return;
        }

        ViewModel.BeginEdit();
        target.DetectionLongEdge = IntegerValue(DetectionLongEdgeBox, target.DetectionLongEdge);
        target.ScanRemainingArea = ScanRemainingAreaToggle.IsOn;
        target.RemainingAreaIntervalMilliseconds = IntegerValue(
            RemainingAreaIntervalBox,
            target.RemainingAreaIntervalMilliseconds);
        RemainingAreaIntervalBox.IsEnabled = target.ScanRemainingArea;
        RefreshEditorState();
    }

    private async void OnCloudOcrToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || ViewModel.SelectedRegion is not { } region)
        {
            return;
        }

        if (CloudOcrToggle.IsOn && !region.UseCloudOcr)
        {
            bool allowed = await _dialogs.ConfirmAsync(new ConfirmDialogOptions(
                Strings.GetString("WorkbenchCloudConsentTitle"),
                Strings.GetString("WorkbenchCloudConsentMessage"),
                Strings.GetString("WorkbenchCloudConsentConfirm"),
                Strings.GetString("WorkbenchCloudConsentCancel")));
            if (!allowed)
            {
                _updatingInspector = true;
                CloudOcrToggle.IsOn = false;
                _updatingInspector = false;
                return;
            }
        }

        ViewModel.BeginEdit();
        region.UseCloudOcr = CloudOcrToggle.IsOn;
        region.CloudConsentPolicyRevision = region.UseCloudOcr ? 1 : 0;
        OcrProviderBox.IsEnabled = region.UseCloudOcr;
        if (region.UseCloudOcr && !_cloudOcrProviders.Any(provider =>
                string.Equals(provider.Id, region.OcrProviderId, StringComparison.Ordinal)))
        {
            _selectedOcrProviderId = null;
            OcrProviderBox.Text = string.Empty;
            OcrProviderBox.ItemsSource = _cloudOcrProviders;
            OcrProviderBox.IsSuggestionListOpen = _cloudOcrProviders.Count > 0;
        }
        RefreshEditorState();
    }

    private void OnOcrProviderGotFocus(object sender, RoutedEventArgs e)
    {
        OcrProviderBox.ItemsSource = _cloudOcrProviders;
        OcrProviderBox.IsSuggestionListOpen = true;
    }

    private void OnOcrProviderTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_updatingInspector ||
            args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        sender.ItemsSource = FilterCloudOcrProviders(sender.Text);
    }

    private void OnOcrProviderSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is ProviderRow provider) CommitOcrProvider(provider);
    }

    private void OnOcrProviderQuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ProviderRow? provider = args.ChosenSuggestion as ProviderRow ??
            _cloudOcrProviders.FirstOrDefault(item =>
                string.Equals(item.Id, args.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, args.QueryText?.Trim(), StringComparison.CurrentCultureIgnoreCase));
        IReadOnlyList<ProviderRow> matches = FilterCloudOcrProviders(args.QueryText);
        provider ??= matches.Count == 1 ? matches[0] : null;
        if (provider is null)
        {
            RestoreOcrProviderText();
            return;
        }
        CommitOcrProvider(provider);
    }

    private void OnOcrProviderLostFocus(object sender, RoutedEventArgs e) =>
        RestoreOcrProviderText();

    private IReadOnlyList<ProviderRow> FilterCloudOcrProviders(string? query)
    {
        string normalized = query?.Trim() ?? string.Empty;
        return normalized.Length == 0
            ? _cloudOcrProviders
            : _cloudOcrProviders.Where(provider =>
                provider.Name.Contains(normalized, StringComparison.CurrentCultureIgnoreCase) ||
                provider.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                provider.Kind.Contains(normalized, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
    }

    private void CommitOcrProvider(ProviderRow provider)
    {
        _selectedOcrProviderId = provider.Id;
        bool wasUpdating = _updatingInspector;
        _updatingInspector = true;
        OcrProviderBox.Text = provider.Name;
        _updatingInspector = wasUpdating;
        OcrProviderBox.IsSuggestionListOpen = false;
        CommitInspector();
    }

    private void RestoreOcrProviderText()
    {
        ProviderRow? provider = _cloudOcrProviders.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedOcrProviderId, StringComparison.Ordinal));
        bool wasUpdating = _updatingInspector;
        _updatingInspector = true;
        OcrProviderBox.Text = provider?.Name ?? _selectedOcrProviderId ?? string.Empty;
        _updatingInspector = wasUpdating;
    }

    private void OnRecognitionLanguageGotFocus(object sender, RoutedEventArgs e)
    {
        RecognitionLanguageBox.ItemsSource = _recognitionLanguages;
        RecognitionLanguageBox.IsSuggestionListOpen = true;
    }

    private void OnRecognitionLanguageTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_updatingInspector &&
            args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = LanguageCatalog.Filter(_recognitionLanguages, sender.Text);
        }
    }

    private void OnRecognitionLanguageSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is LanguageOption language) CommitRecognitionLanguage(language);
    }

    private void OnRecognitionLanguageQuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        LanguageOption? language = args.ChosenSuggestion as LanguageOption ??
            _recognitionLanguages.FirstOrDefault(option =>
                string.Equals(option.Code, args.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.DisplayName, args.QueryText?.Trim(),
                    StringComparison.CurrentCultureIgnoreCase));
        IReadOnlyList<LanguageOption> matches = LanguageCatalog.Filter(
            _recognitionLanguages,
            args.QueryText);
        language ??= matches.Count == 1 ? matches[0] : null;
        if (language is null)
        {
            RestoreRecognitionLanguageText();
            return;
        }
        CommitRecognitionLanguage(language);
    }

    private void OnRecognitionLanguageLostFocus(object sender, RoutedEventArgs e) =>
        RestoreRecognitionLanguageText();

    private void CommitRecognitionLanguage(LanguageOption language)
    {
        _selectedRecognitionLanguage = language.Code;
        bool wasUpdating = _updatingInspector;
        _updatingInspector = true;
        RecognitionLanguageBox.Text = language.DisplayName;
        _updatingInspector = wasUpdating;
        RecognitionLanguageBox.IsSuggestionListOpen = false;
        CommitInspector();
    }

    private void RestoreRecognitionLanguageText()
    {
        LanguageOption? language = _recognitionLanguages.FirstOrDefault(option =>
            string.Equals(option.Code, _selectedRecognitionLanguage, StringComparison.OrdinalIgnoreCase));
        bool wasUpdating = _updatingInspector;
        _updatingInspector = true;
        RecognitionLanguageBox.Text = language?.DisplayName ??
            _selectedRecognitionLanguage ?? string.Empty;
        _updatingInspector = wasUpdating;
    }

    // -- Save / undo / redo -------------------------------------------------------------------

    // Ctrl+S only: the visible save command lives on the workspace container's save bar, so that a
    // channel or overlay edit is not stranded in a section that never had a save button.
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAsync();
        if (ViewModel.HasError)
        {
            ShowInfo(
                InfoBarSeverity.Error,
                Strings.GetString("WorkbenchSaveErrorTitle"),
                ViewModel.ErrorMessage);
        }
        else
        {
            ShowInfo(
                InfoBarSeverity.Success,
                Strings.GetString("WorkbenchSaveSuccessTitle"),
                Strings.GetString($"WorkbenchApply{ViewModel.ApplyState}"));
        }
        RefreshEditorState();
    }

    private void Undo()
    {
        ViewModel.Undo();
        RestoreViewModelSelection();
    }

    private void Redo()
    {
        ViewModel.Redo();
        RestoreViewModelSelection();
    }

    private void RestoreViewModelSelection()
    {
        TargetSelector.ItemsSource = ViewModel.Targets;
        TargetSelector.SelectedItem = ViewModel.SelectedTarget;
        RegionList.Regions = ViewModel.SelectedTarget?.Regions;
        Canvas.Regions = ViewModel.SelectedTarget?.Regions;
        RegionList.SelectedRegion = ViewModel.SelectedRegion;
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RefreshTargetSettings();
        RefreshInspector();
        RefreshEditorState();
    }

    // -- Preview ------------------------------------------------------------------------------

    private async void OnPreviewTimerTick(object? sender, object e) => await RefreshPreviewAsync();

    private async void OnRefreshPreviewClick(object sender, RoutedEventArgs e) => await RefreshPreviewAsync();

    private async Task RefreshPreviewAsync()
    {
        if (_previewInFlight || ViewModel.SelectedTarget is not { } target)
        {
            return;
        }

        _previewInFlight = true;
        Guid requestedTargetId = target.TargetId;
        try
        {
            RuntimeThumbnail? thumbnail = await _runtime.RequestThumbnailAsync(requestedTargetId, 960);
            if (ViewModel.SelectedTarget?.TargetId != requestedTargetId)
            {
                return;
            }
            if (thumbnail is null)
            {
                // The engine only produces thumbnails while it is running, so editing regions on a
                // stopped profile used to mean drawing boxes onto an empty canvas. Fall back to an
                // in-process still frame of the same window/monitor. This is a separate source, not a
                // disguised runtime frame: the status line says so, and every refusal is reported.
                await ApplyStillFramePreviewAsync(target, requestedTargetId);
                return;
            }
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(thumbnail.EncodedImage.ToArray());
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            Canvas.CoordinateWidth = thumbnail.PixelWidth;
            Canvas.CoordinateHeight = thumbnail.PixelHeight;
            Canvas.PreviewSource = bitmap;
            PreviewStatusText.Text = string.Format(
                Strings.GetString("WorkbenchPreviewLive"),
                thumbnail.PixelWidth,
                thumbnail.PixelHeight,
                thumbnail.FrameSequence);
            PreviewStatusIcon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 108, 203, 95));
        }
        catch (Exception exception)
        {
            if (ViewModel.SelectedTarget?.TargetId != requestedTargetId)
            {
                return;
            }
            ClearPreview();
            PreviewStatusText.Text = exception.Message;
            PreviewStatusIcon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 185, 0));
        }
        finally
        {
            _previewInFlight = false;
        }
    }

    /// <summary>
    /// Shows a GDI still frame of the live window/monitor the target names, for use while the engine
    /// is stopped. The target row stores a name and a kind, not a native handle, so the live target
    /// list is re-enumerated and matched by the same rules the engine start path uses.
    /// </summary>
    private async Task ApplyStillFramePreviewAsync(WorkbenchTargetItem target, Guid requestedTargetId)
    {
        CaptureProbeTarget? live;
        try
        {
            CaptureProbeResult probe = await _captureProbe.ProbeAsync(
                new CaptureProbeRequest(NameFilter: null),
                CancellationToken.None);
            live = ResolveLiveTarget(probe.Targets, target);
        }
        catch (Exception exception)
        {
            ShowPreviewUnavailable(exception.Message);
            return;
        }

        if (live is null)
        {
            ShowPreviewUnavailable(string.Format(
                Strings.GetString("WorkbenchPreviewTargetNotFound"),
                target.Name));
            return;
        }

        try
        {
            StillFrameProbeResult frame = await _stillFrames.CaptureAsync(
                new StillFrameProbeRequest(live.NativeHandle, live.Kind, 960),
                CancellationToken.None);
            if (ViewModel.SelectedTarget?.TargetId != requestedTargetId)
            {
                return;
            }
            Canvas.CoordinateWidth = frame.PixelWidth;
            Canvas.CoordinateHeight = frame.PixelHeight;
            Canvas.PreviewSource = await CapturePreviewImaging.FromStillFrameAsync(frame);
            PreviewStatusText.Text = string.Format(
                Strings.GetString("WorkbenchPreviewStillFrame"),
                frame.PixelWidth,
                frame.PixelHeight);
            PreviewStatusIcon.Foreground = new SolidColorBrush(Colors.Gray);
        }
        catch (StillFrameUnavailableException unavailable)
        {
            ShowPreviewUnavailable(Strings.GetString(
                unavailable.ErrorCode == StillFrameUnavailableException.TargetRefusedToRenderCode
                    ? "WorkbenchPreviewRefused"
                    : "WorkbenchPreviewTargetGone"));
        }
        catch (Exception exception)
        {
            ShowPreviewUnavailable(exception.Message);
        }
    }

    private static CaptureProbeTarget? ResolveLiveTarget(
        IReadOnlyList<CaptureProbeTarget> candidates,
        WorkbenchTargetItem target)
    {
        bool wantsWindow = string.Equals(target.Kind, "Window", StringComparison.OrdinalIgnoreCase);
        CaptureProbeTarget[] matching = [.. candidates.Where(candidate =>
            candidate.Capturable &&
            candidate.NativeHandle != 0 &&
            (wantsWindow
                ? string.Equals(candidate.Kind, "Window", StringComparison.OrdinalIgnoreCase)
                : !string.Equals(candidate.Kind, "Window", StringComparison.OrdinalIgnoreCase)))];
        return matching.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayName, target.Name, StringComparison.OrdinalIgnoreCase)) ??
            matching.FirstOrDefault(candidate =>
                candidate.DisplayName.Contains(target.Name, StringComparison.OrdinalIgnoreCase)) ??
            (!wantsWindow && matching.Length == 1 ? matching[0] : null);
    }

    private void ShowPreviewUnavailable(string reason)
    {
        ClearPreview();
        PreviewStatusText.Text = reason;
        PreviewStatusIcon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 185, 0));
    }

    private void ClearPreview()
    {
        Canvas.PreviewSource = null;
        Canvas.ResetCoordinateSize();
    }

    // -- Keyboard -------------------------------------------------------------------------------

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        CoreVirtualKeyStates control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool ctrl = control.HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape)
        {
            Canvas.CancelDraw();
            e.Handled = true;
            return;
        }
        if (IsEditorInputFocused())
        {
            if (ctrl && e.Key == VirtualKey.S)
            {
                OnSaveClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            return;
        }
        if (ctrl && e.Key == VirtualKey.S)
        {
            OnSaveClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == VirtualKey.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == VirtualKey.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Delete)
        {
            OnDeleteRegionRequested(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        double step = ctrl ? 0.01 : 0.001;
        (double dx, double dy) = e.Key switch
        {
            VirtualKey.Left => (-step, 0d),
            VirtualKey.Right => (step, 0d),
            VirtualKey.Up => (0d, -step),
            VirtualKey.Down => (0d, step),
            _ => (0d, 0d),
        };
        if (dx == 0 && dy == 0)
        {
            return;
        }
        ViewModel.BeginEdit();
        Canvas.NudgeSelectedRegion(region, dx, dy);
        RefreshInspector();
        RefreshEditorState();
        e.Handled = true;
    }

    private bool IsEditorInputFocused()
    {
        DependencyObject? element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is TextBox or NumberBox or ComboBox or Controls.SelectBox or PasswordBox or RichEditBox)
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchViewModel.IsDirty) or nameof(WorkbenchViewModel.ApplyState))
        {
            RefreshEditorState();
        }
    }

    private void RefreshEditorState()
    {
        DirtyStatusText.Text = ViewModel.IsDirty
            ? Strings.GetString("WorkbenchUnsavedStatus")
            : Strings.GetString("WorkbenchSavedStatus");
        UpdateGeometryText();
    }

    private void UpdateGeometryText()
    {
        WorkbenchRegionItem? region = ViewModel.SelectedRegion;
        GeometryStatusText.Text = region is null
            ? string.Empty
            : FormattableString.Invariant(
                $"x {region.X:0.000}  y {region.Y:0.000}  w {region.Width:0.000}  h {region.Height:0.000}");
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        PageInfoBar.Severity = severity;
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    private static int IntegerValue(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : checked((int)Math.Round(box.Value));
}
