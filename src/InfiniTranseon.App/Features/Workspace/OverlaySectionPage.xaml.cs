using System.Linq;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.Workspace;

/// <summary>
/// Workspace · overlay style (spec 5.6). A 360px control column (placement, background
/// treatment, colors, opacity/blur, font size, outline, automatic contrast) next to a preview
/// column. The real <c>IOverlayPreviewRenderer</c> always throws
/// <c>EngineRuntimeUnsupportedOperationException</c> by design (protocol v1 cannot return
/// pixels for the layered overlay window — see Core/Probes/OverlayPreviewRenderer.cs), so this
/// page renders a static, explicitly non-live mock of the slot layout instead, switchable across
/// the six result states.
/// </summary>
public sealed partial class OverlaySectionPage : Page
{
    private enum PreviewState
    {
        Waiting,
        Streaming,
        Success,
        Fallback,
        Timeout,
        Failed,
    }

    private sealed record PreviewStateChoice(PreviewState Value, string Display)
    {
        public override string ToString() => Display;
    }

    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    private readonly IProfileService _profiles;
    private Guid _requestedProfileId;
    private bool _updating;

    public OverlaySectionPage()
    {
        ViewModel = App.GetService<WorkbenchViewModel>();
        _profiles = App.GetService<IProfileService>();
        // Compiled XAML attaches an element's handlers before the parser assigns its properties, so
        // the sliders raise ValueChanged from their own Minimum= line. The workbench view model is a
        // singleton and already holds a selected region whenever another section was open first, so
        // the commit path ran against markup that was still being built and killed the page.
        _updating = true;
        try
        {
            InitializeComponent();
        }
        finally
        {
            _updating = false;
        }
        PreviewStateChoice[] previewStates =
        [
            new(PreviewState.Waiting, Strings.GetString("OverlayPreviewStateWaiting")),
            new(PreviewState.Streaming, Strings.GetString("OverlayPreviewStateStreaming")),
            new(PreviewState.Success, Strings.GetString("OverlayPreviewStateSuccess")),
            new(PreviewState.Fallback, Strings.GetString("OverlayPreviewStateFallback")),
            new(PreviewState.Timeout, Strings.GetString("OverlayPreviewStateTimeout")),
            new(PreviewState.Failed, Strings.GetString("OverlayPreviewStateFailed")),
        ];
        PreviewStateBox.ItemsSource = previewStates;
        PreviewStateBox.SelectedItem = previewStates[2];
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
            ShowInfo(InfoBarSeverity.Error, Strings.GetString("WorkbenchLoadErrorTitle"), ViewModel.ErrorMessage);
            return;
        }

        TargetSelector.ItemsSource = ViewModel.Targets;
        TargetSelector.SelectedItem = ViewModel.SelectedTarget;
        RefreshRegionSelector();
    }

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetSelector.SelectedItem is WorkbenchTargetItem target)
        {
            ViewModel.SelectedTarget = target;
        }
        RefreshRegionSelector();
    }

    private void RefreshRegionSelector()
    {
        WorkbenchTargetItem? target = ViewModel.SelectedTarget;
        RegionSelector.ItemsSource = target?.Regions;
        ViewModel.SelectedRegion = target?.Regions.FirstOrDefault();
        RegionSelector.SelectedItem = ViewModel.SelectedRegion;
        RefreshEditor();
    }

    private void OnRegionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegionSelector.SelectedItem is WorkbenchRegionItem region)
        {
            ViewModel.SelectedRegion = region;
        }
        RefreshEditor();
    }

    private void RefreshEditor()
    {
        WorkbenchRegionItem? region = ViewModel.SelectedRegion;
        bool hasRegion = region is not null;
        ControlColumn.IsHitTestVisible = hasRegion;
        ControlColumn.Opacity = hasRegion ? 1 : 0.55;
        if (region is null)
        {
            return;
        }

        _updating = true;
        try
        {
            PlacementButtons.SelectedIndex = region.OverlayMode switch
            {
                "Replace" => 0,
                "TranslucentOrBlurred" => 1,
                "Offset" => 2,
                "FloatingPanel" => 3,
                "NoCover" => 4,
                _ => 0,
            };
            SelectCombo(BackgroundModeBox, region.OverlayBackgroundMode);
            BackgroundColorBox.Text = region.BackgroundColor ?? string.Empty;
            TextColorBox.Text = region.TextColor ?? string.Empty;
            OpacitySlider.Value = region.OverlayOpacity;
            BlurRadiusSlider.Value = region.BlurRadius;
            FontSizeSlider.Value = region.PreferredFontSize;
            OutlineToggle.IsOn = region.OutlineWidth > 0;
            OutlineColorBox.Text = region.OutlineColor ?? string.Empty;
            AutomaticContrastToggle.IsOn = string.Equals(
                region.OverlayBackgroundMode,
                "AutomaticContrastBlur",
                StringComparison.Ordinal);
        }
        finally
        {
            _updating = false;
        }
        RenderPreview();
    }

    private void OnPlacementChanged(object sender, SelectionChangedEventArgs e) => CommitAndRender(() =>
    {
        if (ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        region.OverlayMode = PlacementButtons.SelectedIndex switch
        {
            0 => "Replace",
            1 => "TranslucentOrBlurred",
            2 => "Offset",
            3 => "FloatingPanel",
            4 => "NoCover",
            _ => region.OverlayMode,
        };
    });

    private void OnBackgroundModeChanged(object sender, SelectionChangedEventArgs e) => CommitAndRender(() =>
    {
        if (ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        region.OverlayBackgroundMode = SelectedText(BackgroundModeBox, region.OverlayBackgroundMode);
        _updating = true;
        AutomaticContrastToggle.IsOn = string.Equals(
            region.OverlayBackgroundMode,
            "AutomaticContrastBlur",
            StringComparison.Ordinal);
        _updating = false;
    });

    private void OnAutomaticContrastToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        _updating = true;
        SelectCombo(
            BackgroundModeBox,
            AutomaticContrastToggle.IsOn ? "AutomaticContrastBlur" : "Translucent");
        _updating = false;
        CommitAndRender(() => region.OverlayBackgroundMode = SelectedText(BackgroundModeBox, region.OverlayBackgroundMode));
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e) => CommitAndRender(() =>
    {
        if (ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        region.BackgroundColor = EmptyToNull(BackgroundColorBox.Text);
        region.TextColor = EmptyToNull(TextColorBox.Text);
        region.OutlineColor = EmptyToNull(OutlineColorBox.Text);
        region.OutlineWidth = OutlineToggle.IsOn ? 1 : 0;
    });

    private void OnSliderChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => CommitAndRender(() =>
    {
        if (ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        region.OverlayOpacity = OpacitySlider.Value;
        region.BlurRadius = BlurRadiusSlider.Value;
        region.PreferredFontSize = FontSizeSlider.Value;
    });

    private void CommitAndRender(Action mutate)
    {
        if (_updating || ViewModel.SelectedRegion is not { } region)
        {
            return;
        }
        ViewModel.MarkEditorChanged(createUndoPoint: true);
        mutate();
        RenderPreview();
    }

    private void OnPreviewStateChanged(object sender, SelectionChangedEventArgs e) => RenderPreview();

    private void RenderPreview()
    {
        if (ViewModel.SelectedRegion is not { } region)
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewPanel.Visibility = Visibility.Visible;

        PreviewPanel.Background = new SolidColorBrush(TryParseColor(region.BackgroundColor) ??
            Windows.UI.Color.FromArgb(
                (byte)Math.Round(region.OverlayOpacity * 255),
                20, 20, 24));
        PreviewText.Foreground = new SolidColorBrush(TryParseColor(region.TextColor) ?? Microsoft.UI.Colors.White);
        PreviewText.FontSize = region.PreferredFontSize;
        PreviewPanel.CornerRadius = new CornerRadius(Math.Min(region.BlurRadius / 2, 16));

        PreviewText.Text = PreviewStateBox.SelectedItem is PreviewStateChoice selected
            ? selected.Display
            : string.Empty;
    }

    private static Windows.UI.Color? TryParseColor(string? argbHex)
    {
        if (string.IsNullOrWhiteSpace(argbHex))
        {
            return null;
        }
        string hex = argbHex.TrimStart('#');
        if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint value))
        {
            return null;
        }
        return Windows.UI.Color.FromArgb(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
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

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
