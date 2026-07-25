using System.ComponentModel;
using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Deployment;
using InfiniTranseon.App.Features.Settings;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Probes;
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

namespace InfiniTranseon.App.Features.SetupWizard;

/// <summary>
/// New profile setup wizard (design spec 5.2): four gated steps — capture target, language &amp;
/// service, translation regions, test &amp; save — driven by <see cref="SetupWizardViewModel"/>.
/// Step 3 reuses <see cref="RegionCanvas"/> verbatim; every real test (translation, OCR) goes
/// through the same probes the workspace uses, and every honest gap (no pixel preview before the
/// profile is running, no live overlay renderer) is surfaced as visible, labeled text instead of
/// being faked.
/// </summary>
public sealed partial class SetupWizardPage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    private readonly IReadOnlyList<LanguageOption> _sourceLanguages =
        LanguageCatalog.CreateSourceOptions();
    private readonly IReadOnlyList<LanguageOption> _targetLanguages =
        LanguageCatalog.CreateTargetOptions();
    private readonly AppNavigationState _navigation;
    private readonly IRuntimeControlService _runtime;
    private readonly IStillFrameProbe _stillFrames;
    /// <summary>Last in-process frame of the selected target: shown in step 1, drawn on in step 3,
    /// and cropped for the step 3 OCR test so all three agree on the same pixels.</summary>
    private StillFrameProbeResult? _stillFrame;
    private Guid _editProfileId;
    private bool _isSynchronizingLanguageText;
    private bool _isSynchronizingTargetSelection;
    private bool _updatingInspector;

    public SetupWizardPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<SetupWizardViewModel>();
        _navigation = App.GetService<AppNavigationState>();
        _runtime = App.GetService<IRuntimeControlService>();
        _stillFrames = App.GetService<IStillFrameProbe>();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Canvas.Regions = ViewModel.Regions;
    }

    public SetupWizardViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _editProfileId = e.Parameter is Guid profileId ? profileId : Guid.Empty;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_editProfileId == Guid.Empty)
        {
            await ViewModel.InitializeAsync();
        }
        else
        {
            await ViewModel.LoadForEditAsync(_editProfileId);
        }

        ShowBorderlessCaptureState();
        SynchronizeLanguageBoxes();
        SynchronizeTargetSelection();
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RefreshInspector();
        RefreshStepBar();
        await RefreshTargetPreviewAsync();
    }

    /// <summary>
    /// States the real borderless-capture verdict on the page where the user picks a capture target.
    /// The verdict was already recorded in the status log at startup, but nothing on screen said it,
    /// so a user whose captures carry the OS capture border had no way to learn why.
    /// </summary>
    private void ShowBorderlessCaptureState()
    {
        BorderlessCaptureAuthorizationStatus? status = Program.BorderlessCaptureAuthorization;
        if (status is null or BorderlessCaptureAuthorizationStatus.Allowed)
        {
            BorderlessCaptureInfoBar.IsOpen = false;
            return;
        }

        BorderlessCaptureInfoBar.Title = Strings.GetString("SetupBorderlessCaptureTitle");
        BorderlessCaptureInfoBar.Message = Strings.GetString(status switch
        {
            BorderlessCaptureAuthorizationStatus.DeniedByUser =>
                "SetupBorderlessCaptureDeniedByUser",
            BorderlessCaptureAuthorizationStatus.DeniedBySystem =>
                "SetupBorderlessCaptureDeniedBySystem",
            _ => "SetupBorderlessCaptureNoIdentity",
        });
        BorderlessCaptureInfoBar.IsOpen = true;
    }

    // -- Step bar / gating ----------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SetupWizardViewModel.CurrentStepIndex):
                RefreshStepBar();
                _ = RefreshTargetPreviewAsync();
                break;
            case nameof(SetupWizardViewModel.CurrentStepGateReason):
            case nameof(SetupWizardViewModel.IsStep2Reachable):
            case nameof(SetupWizardViewModel.IsStep3Reachable):
            case nameof(SetupWizardViewModel.IsStep4Reachable):
                RefreshStepBar();
                break;
        }
    }

    private void OnStepButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tagText } &&
            int.TryParse(tagText, out int stepIndex))
        {
            ViewModel.GoToStep(stepIndex);
        }
    }

    private void OnReadinessItemClick(object sender, RoutedEventArgs e) =>
        OnStepButtonClick(sender, e);

    /// <summary>Every disabled step in the bar carries a tooltip explaining why (design system
    /// 9.4: "禁用: 必附原因" — disabled without an explanation is a defect), and the compact stepper
    /// plus the Next button's caption stay in sync with the same reason.</summary>
    private void RefreshStepBar()
    {
        SetStepTooltip(Step1Button, ViewModel.GetStepBlockingReason(0));
        SetStepTooltip(Step2Button, ViewModel.GetStepBlockingReason(1));
        SetStepTooltip(Step3Button, ViewModel.GetStepBlockingReason(2));
        SetStepTooltip(Step4Button, ViewModel.GetStepBlockingReason(3));

        string caption = ViewModel.CurrentStepIndex switch
        {
            // Property-style resw names are addressed with '/' at runtime; the dotted spelling that
            // works in x:Uid markup is not a resource path and threw NAMED_RESOURCE_NOT_FOUND here,
            // which took the wizard down as soon as it loaded.
            0 => Strings.GetString("SetupStep1Caption/Text"),
            1 => Strings.GetString("SetupStep2Caption/Text"),
            2 => Strings.GetString("SetupStep3Caption/Text"),
            _ => Strings.GetString("SetupStep4Caption/Text"),
        };
        StepBarCompact.Text = string.Format(
            Strings.GetString("SetupCompactStepperFormat"),
            ViewModel.CurrentStepNumber,
            SetupWizardViewModel.StepCount,
            caption);

        string reasonText = GateReasonText(ViewModel.CurrentStepGateReason);
        NextBlockedReasonText.Text = reasonText;
        NextBlockedReasonText.Visibility = string.IsNullOrEmpty(reasonText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void SetStepTooltip(Button button, SetupStepGateReason reason)
    {
        string text = GateReasonText(reason);
        ToolTipService.SetToolTip(button, string.IsNullOrEmpty(text) ? null : text);
    }

    private static string GateReasonText(SetupStepGateReason reason) => reason switch
    {
        SetupStepGateReason.NeedsProfileName => Strings.GetString("SetupGateNeedsProfileName"),
        SetupStepGateReason.NeedsCaptureTarget => Strings.GetString("SetupGateNeedsCaptureTarget"),
        SetupStepGateReason.NeedsLanguages => Strings.GetString("SetupGateNeedsLanguages"),
        SetupStepGateReason.NeedsProvider => Strings.GetString("SetupGateNeedsProvider"),
        SetupStepGateReason.NeedsRegions => Strings.GetString("SetupGateNeedsRegions"),
        _ => string.Empty,
    };

    // -- Step 1: capture target -------------------------------------------------------------

    private void OnCaptureTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingTargetSelection || sender is not ListView list)
        {
            return;
        }

        ViewModel.SetSelectedTargets(list.SelectedItems.OfType<CaptureProbeTarget>());
        _ = RefreshTargetPreviewAsync();
    }

    private void SynchronizeTargetSelection()
    {
        _isSynchronizingTargetSelection = true;
        try
        {
            CaptureTargetList.SelectedItems.Clear();
            foreach (CaptureProbeTarget target in ViewModel.SelectedTargets)
            {
                CaptureTargetList.SelectedItems.Add(target);
            }
            CaptureTargetList.SelectedItem = ViewModel.SelectedTarget;
        }
        finally
        {
            _isSynchronizingTargetSelection = false;
        }
    }

    /// <summary>
    /// Shows a real frame of the selected target. The engine's own thumbnail is preferred when a
    /// profile is being edited while its engine runs; otherwise <see cref="IStillFrameProbe"/>
    /// grabs one in-process, which is what makes step 1 and step 3 usable for a brand-new profile.
    /// Nothing here fabricates a picture: when neither source can produce a frame the placeholder
    /// states the actual reason.
    /// </summary>
    private async Task RefreshTargetPreviewAsync()
    {
        if (ViewModel.SelectedTarget is not { } target)
        {
            ClearTargetPreview(Strings.GetString("SetupTargetPreviewNoTarget"));
            return;
        }
        if (!ViewModel.IsStep1 && !ViewModel.IsStep3)
        {
            return;
        }

        try
        {
            RuntimeThumbnail? thumbnail =
                await _runtime.RequestThumbnailAsync(target.TargetId.Value, 1600);
            if (thumbnail is not null)
            {
                ApplyTargetPreview(await DecodeThumbnailAsync(thumbnail));
                return;
            }
        }
        catch (Exception)
        {
            // The engine path is optional here; fall through to the in-process probe, whose own
            // failure is reported to the user below.
        }

        try
        {
            _stillFrame = await _stillFrames.CaptureAsync(
                new StillFrameProbeRequest(target.NativeHandle, target.Kind, 1600),
                CancellationToken.None);
            ApplyTargetPreview(await CreateBitmapAsync(_stillFrame));
        }
        catch (StillFrameUnavailableException unavailable)
        {
            ClearTargetPreview(Strings.GetString(
                unavailable.ErrorCode == StillFrameUnavailableException.TargetRefusedToRenderCode
                    ? "SetupTargetPreviewRefused"
                    : "SetupTargetPreviewGone"));
        }
        catch (Exception exception)
        {
            ClearTargetPreview(exception.Message);
        }
    }

    private void ApplyTargetPreview(ImageSource source)
    {
        TargetPreviewImage.Source = source;
        TargetPreviewImage.Visibility = Visibility.Visible;
        TargetPreviewPlaceholder.Visibility = Visibility.Collapsed;
        // Step 3 draws regions over the same frame, so the user is no longer drawing boxes blind.
        Canvas.PreviewSource = source;
    }

    private void ClearTargetPreview(string reason)
    {
        _stillFrame = null;
        TargetPreviewImage.Source = null;
        TargetPreviewImage.Visibility = Visibility.Collapsed;
        TargetPreviewPlaceholder.Text = reason;
        TargetPreviewPlaceholder.Visibility = Visibility.Visible;
        Canvas.PreviewSource = null;
    }

    private static async Task<ImageSource> DecodeThumbnailAsync(RuntimeThumbnail thumbnail)
    {
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
        return bitmap;
    }

    /// <summary>Wraps the probe's raw BGRA in a decodable image. The round trip through the PNG
    /// encoder keeps this on the same WinRT imaging path the rest of the page uses, rather than
    /// depending on the buffer interop extensions that modern .NET no longer ships.</summary>
    private static async Task<ImageSource> CreateBitmapAsync(StillFrameProbeResult frame)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder =
            await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)frame.PixelWidth,
            (uint)frame.PixelHeight,
            96,
            96,
            frame.BgraPixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    // -- Step 2: language & service ----------------------------------------------------------

    private void OnLanguageBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox box)
        {
            return;
        }

        box.ItemsSource = ReferenceEquals(box, SourceLanguageBox)
            ? _sourceLanguages
            : _targetLanguages;
        box.IsSuggestionListOpen = true;
    }

    private void OnSourceLanguageTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_isSynchronizingLanguageText &&
            args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = LanguageCatalog.Filter(_sourceLanguages, sender.Text);
        }
    }

    private void OnTargetLanguageTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_isSynchronizingLanguageText &&
            args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = LanguageCatalog.Filter(_targetLanguages, sender.Text);
        }
    }

    private void OnSourceLanguageSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is LanguageOption option)
        {
            CommitLanguage(sender, option, isSource: true);
        }
    }

    private void OnTargetLanguageSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is LanguageOption option)
        {
            CommitLanguage(sender, option, isSource: false);
        }
    }

    private void OnSourceLanguageQuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args) =>
        CommitQuery(sender, args, _sourceLanguages, isSource: true);

    private void OnTargetLanguageQuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args) =>
        CommitQuery(sender, args, _targetLanguages, isSource: false);

    private void OnSourceLanguageLostFocus(object sender, RoutedEventArgs e) =>
        RestoreLanguageText(SourceLanguageBox, _sourceLanguages, ViewModel.SourceLanguage);

    private void OnTargetLanguageLostFocus(object sender, RoutedEventArgs e) =>
        RestoreLanguageText(TargetLanguageBox, _targetLanguages, ViewModel.TargetLanguage);

    private void CommitQuery(
        AutoSuggestBox box,
        AutoSuggestBoxQuerySubmittedEventArgs args,
        IReadOnlyList<LanguageOption> options,
        bool isSource)
    {
        LanguageOption? selected = args.ChosenSuggestion as LanguageOption;
        if (selected is null)
        {
            IReadOnlyList<LanguageOption> matches = LanguageCatalog.Filter(options, args.QueryText);
            selected = options.FirstOrDefault(option =>
                           string.Equals(option.Code, args.QueryText?.Trim(),
                               StringComparison.OrdinalIgnoreCase))
                ?? (matches.Count == 1 ? matches[0] : null);
        }

        if (selected is not null)
        {
            CommitLanguage(box, selected, isSource);
            return;
        }

        RestoreLanguageText(
            box,
            options,
            isSource ? ViewModel.SourceLanguage : ViewModel.TargetLanguage);
    }

    private void CommitLanguage(AutoSuggestBox box, LanguageOption option, bool isSource)
    {
        if (isSource)
        {
            ViewModel.SourceLanguage = option.Code;
        }
        else
        {
            ViewModel.TargetLanguage = option.Code;
        }

        SetLanguageText(box, option.DisplayName);
        box.IsSuggestionListOpen = false;
    }

    private void SynchronizeLanguageBoxes()
    {
        RestoreLanguageText(SourceLanguageBox, _sourceLanguages, ViewModel.SourceLanguage);
        RestoreLanguageText(TargetLanguageBox, _targetLanguages, ViewModel.TargetLanguage);
    }

    private void RestoreLanguageText(
        AutoSuggestBox box,
        IReadOnlyList<LanguageOption> options,
        string code)
    {
        LanguageOption option = LanguageCatalog.ResolveOrCreate(options, code);
        SetLanguageText(box, option.DisplayName);
    }

    private void SetLanguageText(AutoSuggestBox box, string text)
    {
        _isSynchronizingLanguageText = true;
        try
        {
            box.Text = text;
        }
        finally
        {
            _isSynchronizingLanguageText = false;
        }
    }

    private async void OnConfigureProviderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProvider is null)
        {
            return;
        }

        bool saved = await ProviderCredentialDialog.ShowAsync(
            XamlRoot,
            ViewModel.SelectedProvider,
            App.GetService<ISecretReferenceService>());
        if (saved)
        {
            await ViewModel.InitializeAsync();
        }
    }

    // -- Step 3: regions (reuses RegionCanvas verbatim) --------------------------------------

    private void OnAddRegionClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RegionNameBox.Text))
        {
            return;
        }

        ViewModel.AddRegion(RegionNameBox.Text, PriorityFromIndex(RegionPrioritySelector.SelectedIndex));
        RegionNameBox.Text = string.Empty;
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RegionList.SelectedItem = ViewModel.SelectedRegion;
        RefreshInspector();
    }

    private void OnRemoveRegionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WorkbenchRegionItem region })
        {
            ViewModel.RemoveRegion(region);
            Canvas.SelectedRegion = ViewModel.SelectedRegion;
            RegionList.SelectedItem = ViewModel.SelectedRegion;
            RefreshInspector();
        }
    }

    private void OnRegionListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegionList.SelectedItem is WorkbenchRegionItem region)
        {
            ViewModel.SelectedRegion = region;
            Canvas.SelectedRegion = region;
            RefreshInspector();
        }
    }

    private void OnDrawRegionToggled(object sender, RoutedEventArgs e) =>
        Canvas.IsDrawMode = DrawRegionButton.IsChecked.GetValueOrDefault();

    private void OnCanvasSelectionChanged(object sender, WorkbenchRegionItem? region)
    {
        ViewModel.SelectedRegion = region;
        RegionList.SelectedItem = region;
        RefreshInspector();
    }

    private void OnCanvasRegionAdded(object sender, RegionDrawStartedEventArgs e)
    {
        // The canvas requires the host to synchronously create and select the new region before
        // this handler returns, so it can apply the drawn bounds to SelectedRegion afterwards.
        ViewModel.AddRegion($"Region {ViewModel.Regions.Count + 1}", RegionPriorityLevel.P1);
        Canvas.SelectedRegion = ViewModel.SelectedRegion;
        RegionList.SelectedItem = ViewModel.SelectedRegion;
        RefreshInspector();
    }

    private void OnCanvasRegionChanged(object sender, WorkbenchRegionItem region) => RefreshInspector();

    private void OnCanvasDrawModeExited(object sender, EventArgs e)
    {
        DrawRegionButton.IsChecked = false;
        Canvas.IsDrawMode = false;
    }

    private void RefreshInspector()
    {
        WorkbenchRegionItem? region = ViewModel.SelectedRegion;
        _updatingInspector = true;
        try
        {
            bool enabled = region is not null;
            InspectorNameBox.IsEnabled = enabled;
            InspectorPriorityBox.IsEnabled = enabled;
            InspectorContextRoleBox.IsEnabled = enabled;
            InspectorNameBox.Text = region?.Name ?? string.Empty;
            InspectorPriorityBox.SelectedIndex = region is null ? -1 : (int)region.Priority;
            InspectorContextRoleBox.SelectedIndex = region is null ? -1 : (int)region.ContextRole;
        }
        finally
        {
            _updatingInspector = false;
        }
    }

    private void OnInspectorChanged(object sender, RoutedEventArgs e) => CommitInspector();

    private void OnInspectorSelectionChanged(object sender, SelectionChangedEventArgs e) => CommitInspector();

    private void CommitInspector()
    {
        if (_updatingInspector || ViewModel.SelectedRegion is not { } region)
        {
            return;
        }

        region.Name = InspectorNameBox.Text.Trim();
        if (InspectorPriorityBox.SelectedIndex >= 0)
        {
            region.Priority = (RegionPriorityLevel)InspectorPriorityBox.SelectedIndex;
        }
        if (InspectorContextRoleBox.SelectedIndex >= 0)
        {
            region.ContextRole = (RegionContextRole)InspectorContextRoleBox.SelectedIndex;
        }
    }

    private static RegionPriorityLevel PriorityFromIndex(int index) => index switch
    {
        1 => RegionPriorityLevel.P1,
        2 => RegionPriorityLevel.P2,
        3 => RegionPriorityLevel.P3,
        _ => RegionPriorityLevel.P0,
    };

    /// <summary>Step 3 "试跑 OCR" (design spec 5.2): a real end-to-end test — real thumbnail (when
    /// the engine happens to be running for this target), real WinRT crop, real
    /// <see cref="IOcrProbe"/> call. When no thumbnail is available (the common case for a
    /// brand-new profile — see <see cref="RefreshTargetPreviewAsync"/>) this reports that honestly
    /// instead of fabricating recognized text.</summary>
    private async void OnTestOcrClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRegion is not { } region || ViewModel.SelectedTarget is not { } target)
        {
            return;
        }

        OcrInfoBar.IsOpen = false;
        OcrResultLabel.Visibility = Visibility.Collapsed;
        OcrResultText.Visibility = Visibility.Collapsed;

        RuntimeThumbnail? thumbnail = null;
        try
        {
            thumbnail = await _runtime.RequestThumbnailAsync(target.TargetId.Value, 1600);
        }
        catch (Exception exception)
        {
            ShowOcrInfo(InfoBarSeverity.Error, exception.Message);
            return;
        }

        // Before this, a not-yet-started profile had no frame at all and the button could never
        // succeed. The still frame taken for the region canvas is the same pixels the user drew on.
        if (thumbnail is null && _stillFrame is null)
        {
            await RefreshTargetPreviewAsync();
        }
        if (thumbnail is null && _stillFrame is null)
        {
            ShowOcrInfo(InfoBarSeverity.Informational, Strings.GetString("SetupOcrTestUnavailable"));
            return;
        }

        try
        {
            (byte[] Bytes, int Width, int Height) crop = thumbnail is not null
                ? await CropRegionAsync(thumbnail, region)
                : await CropRegionAsync(_stillFrame!, region);
            (string? Text, TimeSpan Latency, string? Error, string? EngineLanguageTag) result =
                await ViewModel.TestOcrAsync(region, crop.Width, crop.Height, crop.Bytes);
            if (result.Error is not null)
            {
                ShowOcrInfo(InfoBarSeverity.Error, result.Error);
                return;
            }

            OcrResultLabel.Visibility = Visibility.Visible;
            OcrResultText.Visibility = Visibility.Visible;
            OcrResultText.Text = result.Text;
            // Reading Japanese with an English model does not fail — it returns confident nonsense.
            // With "auto" the engine follows Windows' display languages, not the game's, so the user
            // has to be told which model produced the text above.
            if (ViewModel.IsOcrLanguageMismatch(result.EngineLanguageTag))
            {
                ShowOcrInfo(
                    InfoBarSeverity.Warning,
                    string.Format(
                        Strings.GetString("SetupOcrAutoLanguage"),
                        result.EngineLanguageTag));
            }
        }
        catch (Exception exception)
        {
            ShowOcrInfo(InfoBarSeverity.Error, exception.Message);
        }
    }

    private void ShowOcrInfo(InfoBarSeverity severity, string message)
    {
        OcrInfoBar.Severity = severity;
        OcrInfoBar.Message = message;
        OcrInfoBar.IsOpen = true;
    }

    /// <summary>Crops the region's normalized bounds out of a real thumbnail using WinRT imaging
    /// (WinUI-only work that must stay out of the view model — see
    /// <see cref="ViewModelArchitectureTests"/>) and re-encodes the crop as PNG for
    /// <see cref="IOcrProbe"/>.</summary>
    private static async Task<(byte[] Bytes, int Width, int Height)> CropRegionAsync(
        RuntimeThumbnail thumbnail,
        WorkbenchRegionItem region)
    {
        using var sourceStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(sourceStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(thumbnail.EncodedImage.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        sourceStream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(sourceStream);
        uint cropX = (uint)Math.Clamp(region.X * decoder.PixelWidth, 0, decoder.PixelWidth - 1);
        uint cropY = (uint)Math.Clamp(region.Y * decoder.PixelHeight, 0, decoder.PixelHeight - 1);
        uint cropWidth = (uint)Math.Clamp(region.Width * decoder.PixelWidth, 1, decoder.PixelWidth - cropX);
        uint cropHeight = (uint)Math.Clamp(region.Height * decoder.PixelHeight, 1, decoder.PixelHeight - cropY);

        var transform = new BitmapTransform
        {
            Bounds = new BitmapBounds { X = cropX, Y = cropY, Width = cropWidth, Height = cropHeight },
        };
        PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] pixels = pixelData.DetachPixelData();

        using var targetStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, targetStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            cropWidth,
            cropHeight,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        var bytes = new byte[targetStream.Size];
        targetStream.Seek(0);
        using var reader = new DataReader(targetStream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)targetStream.Size);
        reader.ReadBytes(bytes);
        return (bytes, (int)cropWidth, (int)cropHeight);
    }

    /// <summary>Crops the region out of an in-process still frame. The pixels are already BGRA, so
    /// this only slices rows and re-encodes as PNG for <see cref="IOcrProbe"/>.</summary>
    private static async Task<(byte[] Bytes, int Width, int Height)> CropRegionAsync(
        StillFrameProbeResult frame,
        WorkbenchRegionItem region)
    {
        int cropX = (int)Math.Clamp(region.X * frame.PixelWidth, 0, frame.PixelWidth - 1);
        int cropY = (int)Math.Clamp(region.Y * frame.PixelHeight, 0, frame.PixelHeight - 1);
        int cropWidth = (int)Math.Clamp(
            region.Width * frame.PixelWidth, 1, frame.PixelWidth - cropX);
        int cropHeight = (int)Math.Clamp(
            region.Height * frame.PixelHeight, 1, frame.PixelHeight - cropY);

        var pixels = new byte[checked(cropWidth * cropHeight * 4)];
        int sourceStride = frame.PixelWidth * 4;
        int targetStride = cropWidth * 4;
        for (int row = 0; row < cropHeight; row++)
        {
            Array.Copy(
                frame.BgraPixels,
                ((cropY + row) * sourceStride) + (cropX * 4),
                pixels,
                row * targetStride,
                targetStride);
        }

        using var targetStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder =
            await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, targetStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)cropWidth,
            (uint)cropHeight,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        var bytes = new byte[targetStream.Size];
        targetStream.Seek(0);
        using var reader = new DataReader(targetStream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)targetStream.Size);
        reader.ReadBytes(bytes);
        return (bytes, cropWidth, cropHeight);
    }

    // -- Step 4: review & save ----------------------------------------------------------------

    private async void OnSaveOnlyClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveCommand.ExecuteAsync(null);
        if (ViewModel.SavedProfileId != Guid.Empty && string.IsNullOrEmpty(ViewModel.ErrorMessage))
        {
            _navigation.NavigateToProfile(ViewModel.SavedProfileId);
        }
    }

    private async void OnSaveAndStartTestClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveCommand.ExecuteAsync(null);
        if (ViewModel.SavedProfileId == Guid.Empty || !string.IsNullOrEmpty(ViewModel.ErrorMessage))
        {
            return;
        }

        try
        {
            await _runtime.StartAsync(ViewModel.SavedProfileId);
            _navigation.Navigate(GlobalDestination.Home);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = exception.Message;
        }
    }

    private async void OnSaveDraftExitClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveDraftCommand.ExecuteAsync(null);
        if (string.IsNullOrEmpty(ViewModel.ErrorMessage))
        {
            _navigation.Navigate(GlobalDestination.Home);
        }
    }

    // -- Keyboard (design system 9.5: Esc semantics must be consistent) -----------------------

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Canvas.CancelDraw();
            e.Handled = true;
            return;
        }

        if (!ViewModel.IsStep3 || IsEditorInputFocused() || ViewModel.SelectedRegion is not { } region)
        {
            return;
        }

        CoreVirtualKeyStates control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool ctrl = control.HasFlag(CoreVirtualKeyStates.Down);
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

        Canvas.NudgeSelectedRegion(region, dx, dy);
        RefreshInspector();
        e.Handled = true;
    }

    private bool IsEditorInputFocused()
    {
        DependencyObject? element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is TextBox or NumberBox or ComboBox or PasswordBox or RichEditBox or AutoSuggestBox)
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    // -- x:Bind function-binding helpers (no IValueConverter type exists in this codebase; every
    // other page derives Visibility from bool properties directly or via a page-local function
    // binding, e.g. GlossaryPage.TableVisibility) --------------------------------------------

    private Visibility TextVisibility(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    private bool HasText(string? value) => !string.IsNullOrEmpty(value);

    private Visibility InverseVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    private string FormatLatency(TimeSpan value) => string.Format(
        Strings.GetString("SetupTestTranslationLatencyFormat"),
        (int)value.TotalMilliseconds);

    /// <summary>Turns the probe's machine code into a sentence, keeping the code and — when the
    /// probe threw rather than returned — the exception text visible for diagnosis.</summary>
    private string DescribeTranslationTestError(string? errorCode, string? detail)
    {
        if (string.IsNullOrEmpty(errorCode))
        {
            return string.Empty;
        }
        string message = ProbeErrorPresenter.Describe(errorCode, Strings.GetString);
        return string.IsNullOrEmpty(detail) ? message : $"{message}\n{detail}";
    }
}
