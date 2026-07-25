using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Controls.Dialogs;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
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
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    private readonly IRuntimeControlService _runtime;
    private readonly IProfileService _profiles;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };
    private bool _updatingInspector;
    private bool _previewInFlight;
    private Guid _requestedProfileId;

    public CaptureSectionPage()
    {
        ViewModel = App.GetService<WorkbenchViewModel>();
        _runtime = App.GetService<IRuntimeControlService>();
        _profiles = App.GetService<IProfileService>();
        _dialogs = new DialogService(() => XamlRoot);
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

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
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
        ViewModel.MarkEditorChanged(createUndoPoint: true);

    private void OnCanvasRegionAdded(object sender, RegionDrawStartedEventArgs e)
    {
        ViewModel.AddRegion();
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RegionList.SelectedRegion = ViewModel.SelectedRegion;
    }

    private void OnCanvasRegionChanged(object sender, WorkbenchRegionItem region)
    {
        ViewModel.MarkEditorChanged(createUndoPoint: false);
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
            OcrProviderBox.Text = region.OcrProviderId;
            RecognitionLanguageBox.Text = region.RecognitionLanguage;
            DetectOrientationToggle.IsOn = region.DetectOrientation;
            CloudOcrToggle.IsOn = region.UseCloudOcr;
            RecognitionIntervalBox.Value = region.RecognitionIntervalMilliseconds;
            DetectionScaleBox.Value = region.DetectionScale;
            SelectCombo(LineBreakModeBox, region.LineBreakMode);
            CustomSeparatorBox.Text = region.CustomLineSeparator ?? string.Empty;
            SelectCombo(LineAlignmentBox, region.LineAlignment);
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

    private void OnInspectorSelectionChanged(object sender, SelectionChangedEventArgs e) => CommitInspector();

    private void OnInspectorNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        CommitInspector();

    private void CommitInspector()
    {
        if (_updatingInspector || ViewModel.SelectedRegion is not { } region)
        {
            return;
        }

        ViewModel.MarkEditorChanged(createUndoPoint: true);
        region.Name = RegionNameBox.Text.Trim();
        region.Enabled = RegionEnabledToggle.IsOn;
        region.Priority = (RegionPriorityLevel)Math.Max(0, RegionPriorityBox.SelectedIndex);
        region.ContextRole = (RegionContextRole)Math.Max(0, ContextRoleBox.SelectedIndex);
        region.LockDegradation = RegionLockToggle.IsOn;
        region.OcrProviderId = OcrProviderBox.Text.Trim();
        region.RecognitionLanguage = RecognitionLanguageBox.Text.Trim();
        region.DetectOrientation = DetectOrientationToggle.IsOn;
        region.RecognitionIntervalMilliseconds = IntegerValue(
            RecognitionIntervalBox,
            region.RecognitionIntervalMilliseconds);
        region.DetectionScale = double.IsNaN(DetectionScaleBox.Value)
            ? region.DetectionScale
            : DetectionScaleBox.Value;
        region.LineBreakMode = SelectedText(LineBreakModeBox, "PreserveLines");
        region.CustomLineSeparator = string.IsNullOrEmpty(CustomSeparatorBox.Text)
            ? null
            : CustomSeparatorBox.Text;
        region.LineAlignment = SelectedText(LineAlignmentBox, "Auto");
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

        ViewModel.MarkEditorChanged(createUndoPoint: true);
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

        ViewModel.MarkEditorChanged(createUndoPoint: true);
        region.UseCloudOcr = CloudOcrToggle.IsOn;
        region.CloudConsentPolicyRevision = region.UseCloudOcr ? 1 : 0;
        RefreshEditorState();
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

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Undo();
        RestoreViewModelSelection();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
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
                ClearPreview();
                PreviewStatusText.Text = Strings.GetString("WorkbenchPreviewRuntimeStopped");
                PreviewStatusIcon.Foreground = new SolidColorBrush(Colors.Gray);
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
            OnUndoClick(UndoButton, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == VirtualKey.Y)
        {
            OnRedoClick(RedoButton, new RoutedEventArgs());
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
        ViewModel.MarkEditorChanged(createUndoPoint: true);
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
            if (element is TextBox or NumberBox or ComboBox or PasswordBox or RichEditBox)
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
        UndoButton.IsEnabled = ViewModel.CanUndo;
        RedoButton.IsEnabled = ViewModel.CanRedo;
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

    private static void SelectCombo(ComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString() ?? item.Content?.ToString(),
                value,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectedText(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ??
        fallback;

    private static int IntegerValue(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : checked((int)Math.Round(box.Value));
}
