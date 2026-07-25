using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace InfiniTranseon.App.Controls;

/// <summary>Raised when a draw-mode pointer press starts a brand-new region.</summary>
public sealed class RegionDrawStartedEventArgs(double normalizedX, double normalizedY) : EventArgs
{
    public double NormalizedX { get; } = normalizedX;
    public double NormalizedY { get; } = normalizedY;
}

/// <summary>
/// The region editing surface: a fixed 1600x900 normalized coordinate space rendered inside a
/// <see cref="Viewbox"/> over a preview image, with pointer draw/drag/resize and bounds clamping.
/// Extracted from the former OpticalWorkbenchPage code-behind (~1100 lines) so the capture section
/// page (and, later, the profile setup wizard) can reuse the exact same canvas mechanics.
/// </summary>
public sealed partial class RegionCanvas : UserControl
{
    private sealed record RegionHandle(WorkbenchRegionItem Region, bool Resize);

    private sealed record DragState(
        WorkbenchRegionItem Region,
        bool Resize,
        Point Start,
        double X,
        double Y,
        double Width,
        double Height,
        UIElement Capture);

    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions),
        typeof(ObservableCollection<WorkbenchRegionItem>),
        typeof(RegionCanvas),
        new PropertyMetadata(null, OnRegionsChanged));

    public static readonly DependencyProperty SelectedRegionProperty = DependencyProperty.Register(
        nameof(SelectedRegion),
        typeof(WorkbenchRegionItem),
        typeof(RegionCanvas),
        new PropertyMetadata(null, OnSelectedRegionChanged));

    public static readonly DependencyProperty IsDrawModeProperty = DependencyProperty.Register(
        nameof(IsDrawMode),
        typeof(bool),
        typeof(RegionCanvas),
        new PropertyMetadata(false));

    public static readonly DependencyProperty PreviewSourceProperty = DependencyProperty.Register(
        nameof(PreviewSource),
        typeof(ImageSource),
        typeof(RegionCanvas),
        new PropertyMetadata(null, OnPreviewSourceChanged));

    public static readonly DependencyProperty CoordinateWidthProperty = DependencyProperty.Register(
        nameof(CoordinateWidth),
        typeof(double),
        typeof(RegionCanvas),
        new PropertyMetadata(1600d, OnCoordinateSizeChanged));

    public static readonly DependencyProperty CoordinateHeightProperty = DependencyProperty.Register(
        nameof(CoordinateHeight),
        typeof(double),
        typeof(RegionCanvas),
        new PropertyMetadata(900d, OnCoordinateSizeChanged));

    public static readonly DependencyProperty AccessibleNameProperty = DependencyProperty.Register(
        nameof(AccessibleName),
        typeof(string),
        typeof(RegionCanvas),
        new PropertyMetadata(string.Empty));

    private DragState? _drag;
    private Point? _drawStart;

    public RegionCanvas()
    {
        InitializeComponent();
    }

    /// <summary>The live target region collection. Assigned once per target switch — never
    /// null-then-reassigned — so selection and scroll position survive in-place edits.</summary>
    public ObservableCollection<WorkbenchRegionItem>? Regions
    {
        get => (ObservableCollection<WorkbenchRegionItem>?)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public WorkbenchRegionItem? SelectedRegion
    {
        get => (WorkbenchRegionItem?)GetValue(SelectedRegionProperty);
        set => SetValue(SelectedRegionProperty, value);
    }

    public bool IsDrawMode
    {
        get => (bool)GetValue(IsDrawModeProperty);
        set => SetValue(IsDrawModeProperty, value);
    }

    public ImageSource? PreviewSource
    {
        get => (ImageSource?)GetValue(PreviewSourceProperty);
        set => SetValue(PreviewSourceProperty, value);
    }

    public double CoordinateWidth
    {
        get => (double)GetValue(CoordinateWidthProperty);
        set => SetValue(CoordinateWidthProperty, value);
    }

    public double CoordinateHeight
    {
        get => (double)GetValue(CoordinateHeightProperty);
        set => SetValue(CoordinateHeightProperty, value);
    }

    public string AccessibleName
    {
        get => (string)GetValue(AccessibleNameProperty);
        set => SetValue(AccessibleNameProperty, value);
    }

    /// <summary>Raised whenever the pointer selects a region frame or its resize handle.</summary>
    public event EventHandler<WorkbenchRegionItem?>? SelectionChanged;

    /// <summary>
    /// Raised when a drag or resize gesture begins on an existing region, before any mutation.
    /// The host should push one undo point here (mirrors the original page's
    /// <c>MarkEditorChanged(createUndoPoint: true)</c> on pointer-press).
    /// </summary>
    public event EventHandler<WorkbenchRegionItem>? RegionDragStarted;

    /// <summary>
    /// Raised when a draw-mode pointer press starts a brand-new region. The host is expected to
    /// synchronously create and select the region (target/undo bookkeeping lives in the view
    /// model), then set <see cref="SelectedRegion"/> before the handler returns so the canvas can
    /// continue tracking the drag.
    /// </summary>
    public event EventHandler<RegionDrawStartedEventArgs>? RegionAdded;

    /// <summary>Raised after a region's bounds were mutated (drag, resize, draw, or keyboard
    /// nudge). The canvas has already clamped and applied the new bounds; the host only needs to
    /// mark the profile dirty and refresh any bound inspector fields.</summary>
    public event EventHandler<WorkbenchRegionItem>? RegionChanged;

    /// <summary>Raised when draw mode ends (pointer released, or <see cref="CancelDraw"/>), so the
    /// host can un-check its Draw toggle button.</summary>
    public event EventHandler? DrawModeExited;

    /// <summary>Nudges the given region by a normalized delta, clamping and raising
    /// <see cref="RegionChanged"/>. Used for arrow-key nudge, which — like the original page —
    /// works regardless of which control literally has keyboard focus.</summary>
    public void NudgeSelectedRegion(WorkbenchRegionItem region, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(region);
        ApplyBounds(region, region.X + dx, region.Y + dy, region.Width, region.Height);
    }

    /// <summary>Cancels an in-progress draw or drag gesture (Esc). Safe to call at any time.</summary>
    public void CancelDraw()
    {
        if (_drawStart is null && _drag is null)
        {
            return;
        }

        _drawStart = null;
        _drag = null;
        DrawModeExited?.Invoke(this, EventArgs.Empty);
    }

    private static void OnRegionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (RegionCanvas)sender;
        if (args.OldValue is ObservableCollection<WorkbenchRegionItem> oldCollection)
        {
            oldCollection.CollectionChanged -= canvas.OnRegionCollectionChanged;
            foreach (WorkbenchRegionItem region in oldCollection)
            {
                region.PropertyChanged -= canvas.OnRegionPropertyChanged;
            }
        }
        if (args.NewValue is ObservableCollection<WorkbenchRegionItem> newCollection)
        {
            newCollection.CollectionChanged += canvas.OnRegionCollectionChanged;
            foreach (WorkbenchRegionItem region in newCollection)
            {
                region.PropertyChanged += canvas.OnRegionPropertyChanged;
            }
        }
        canvas.RenderRegions();
    }

    private void OnRegionCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (WorkbenchRegionItem region in e.OldItems.OfType<WorkbenchRegionItem>())
            {
                region.PropertyChanged -= OnRegionPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (WorkbenchRegionItem region in e.NewItems.OfType<WorkbenchRegionItem>())
            {
                region.PropertyChanged += OnRegionPropertyChanged;
            }
        }
        RenderRegions();
    }

    private void OnRegionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WorkbenchRegionItem region)
        {
            return;
        }
        if (e.PropertyName is nameof(WorkbenchRegionItem.X) or nameof(WorkbenchRegionItem.Y) or
            nameof(WorkbenchRegionItem.Width) or nameof(WorkbenchRegionItem.Height))
        {
            UpdateRegionVisual(region);
        }
        else if (e.PropertyName is nameof(WorkbenchRegionItem.Name) or
                 nameof(WorkbenchRegionItem.Priority) or nameof(WorkbenchRegionItem.Enabled))
        {
            RenderRegions();
        }
    }

    private static void OnSelectedRegionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((RegionCanvas)sender).RenderRegions();

    private static void OnPreviewSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (RegionCanvas)sender;
        canvas.PreviewImage.Source = canvas.PreviewSource;
        canvas.CanvasPlaceholder.Visibility = canvas.PreviewSource is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void OnCoordinateSizeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (RegionCanvas)sender;
        canvas.PreviewCoordinateSpace.Width = canvas.CoordinateWidth;
        canvas.PreviewCoordinateSpace.Height = canvas.CoordinateHeight;
        canvas.RenderRegions();
    }

    /// <summary>Resets the coordinate space back to the default 1600x900 placeholder size.</summary>
    public void ResetCoordinateSize()
    {
        CoordinateWidth = 1600;
        CoordinateHeight = 900;
    }

    private static (double X, double Y, double Width, double Height) ClampBounds(
        double x,
        double y,
        double width,
        double height)
    {
        const double minimum = 0.01;
        width = Math.Clamp(width, minimum, 1);
        height = Math.Clamp(height, minimum, 1);
        x = Math.Clamp(x, 0, 1 - width);
        y = Math.Clamp(y, 0, 1 - height);
        return (x, y, width, height);
    }

    private void ApplyBounds(WorkbenchRegionItem region, double x, double y, double width, double height)
    {
        (double cx, double cy, double cw, double ch) = ClampBounds(x, y, width, height);
        region.X = cx;
        region.Y = cy;
        region.Width = cw;
        region.Height = ch;
        UpdateRegionVisual(region);
        RegionChanged?.Invoke(this, region);
    }

    private void RenderRegions()
    {
        RegionCanvasInternal.Children.Clear();
        ObservableCollection<WorkbenchRegionItem>? regions = Regions;
        if (regions is null)
        {
            return;
        }

        double width = PreviewCoordinateSpace.Width;
        double height = PreviewCoordinateSpace.Height;
        foreach (WorkbenchRegionItem region in regions)
        {
            var frame = new Grid
            {
                Width = region.Width * width,
                Height = region.Height * height,
                Tag = new RegionHandle(region, Resize: false),
                Background = new SolidColorBrush(
                    region.Enabled
                        ? ColorHelper.FromArgb(36, 96, 205, 255)
                        : ColorHelper.FromArgb(28, 128, 128, 128)),
                BorderBrush = new SolidColorBrush(
                    ReferenceEquals(region, SelectedRegion)
                        ? Colors.White
                        : region.Enabled
                            ? ColorHelper.FromArgb(255, 96, 205, 255)
                            : Colors.Gray),
                BorderThickness = new Thickness(
                    ReferenceEquals(region, SelectedRegion) ? 4 : 2),
            };
            Canvas.SetLeft(frame, region.X * width);
            Canvas.SetTop(frame, region.Y * height);
            var labelText = new TextBlock
            {
                Text = $"{region.Name} · {region.Priority}",
                Foreground = new SolidColorBrush(Colors.White),
            };
            var label = new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(190, 20, 20, 20)),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = labelText,
            };
            frame.Children.Add(label);
            var resize = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = frame.BorderBrush,
                BorderThickness = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -11, -11),
                Tag = new RegionHandle(region, Resize: true),
            };
            frame.Children.Add(resize);
            frame.PointerPressed += OnRegionPointerPressed;
            frame.PointerMoved += OnRegionPointerMoved;
            frame.PointerReleased += OnRegionPointerReleased;
            frame.PointerCanceled += OnRegionPointerReleased;
            resize.PointerPressed += OnRegionPointerPressed;
            resize.PointerMoved += OnRegionPointerMoved;
            resize.PointerReleased += OnRegionPointerReleased;
            resize.PointerCanceled += OnRegionPointerReleased;
            RegionCanvasInternal.Children.Add(frame);
        }
    }

    private void UpdateRegionVisual(WorkbenchRegionItem region)
    {
        Grid? frame = RegionCanvasInternal.Children
            .OfType<Grid>()
            .FirstOrDefault(item =>
                item.Tag is RegionHandle handle &&
                ReferenceEquals(handle.Region, region));
        if (frame is null)
        {
            return;
        }

        frame.Width = region.Width * PreviewCoordinateSpace.Width;
        frame.Height = region.Height * PreviewCoordinateSpace.Height;
        Canvas.SetLeft(frame, region.X * PreviewCoordinateSpace.Width);
        Canvas.SetTop(frame, region.Y * PreviewCoordinateSpace.Height);
    }

    private void OnRegionPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement capture || (sender as FrameworkElement)?.Tag is not RegionHandle handle)
        {
            return;
        }

        e.Handled = true;
        SelectedRegion = handle.Region;
        SelectionChanged?.Invoke(this, handle.Region);
        Point point = e.GetCurrentPoint(RegionCanvasInternal).Position;
        RegionDragStarted?.Invoke(this, handle.Region);
        _drag = new DragState(
            handle.Region,
            handle.Resize,
            point,
            handle.Region.X,
            handle.Region.Y,
            handle.Region.Width,
            handle.Region.Height,
            capture);
        capture.CapturePointer(e.Pointer);
    }

    private void OnRegionPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        Point point = e.GetCurrentPoint(RegionCanvasInternal).Position;
        double dx = (point.X - drag.Start.X) / PreviewCoordinateSpace.Width;
        double dy = (point.Y - drag.Start.Y) / PreviewCoordinateSpace.Height;
        if (drag.Resize)
        {
            ApplyBounds(drag.Region, drag.X, drag.Y, drag.Width + dx, drag.Height + dy);
        }
        else
        {
            ApplyBounds(drag.Region, drag.X + dx, drag.Y + dy, drag.Width, drag.Height);
        }
    }

    private void OnRegionPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        drag.Capture.ReleasePointerCapture(e.Pointer);
        _drag = null;
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsDrawMode)
        {
            return;
        }

        Point point = e.GetCurrentPoint(RegionCanvasInternal).Position;
        _drawStart = point;
        double normalizedX = point.X / PreviewCoordinateSpace.Width;
        double normalizedY = point.Y / PreviewCoordinateSpace.Height;
        RegionAdded?.Invoke(this, new RegionDrawStartedEventArgs(normalizedX, normalizedY));
        if (SelectedRegion is { } region)
        {
            ApplyBounds(region, normalizedX, normalizedY, 0.01, 0.01);
        }
        RegionCanvasInternal.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drawStart is not Point start || SelectedRegion is not { } region)
        {
            return;
        }

        Point current = e.GetCurrentPoint(RegionCanvasInternal).Position;
        double left = Math.Min(start.X, current.X);
        double top = Math.Min(start.Y, current.Y);
        double width = Math.Abs(current.X - start.X);
        double height = Math.Abs(current.Y - start.Y);
        ApplyBounds(
            region,
            left / PreviewCoordinateSpace.Width,
            top / PreviewCoordinateSpace.Height,
            Math.Max(0.01, width / PreviewCoordinateSpace.Width),
            Math.Max(0.01, height / PreviewCoordinateSpace.Height));
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drawStart is null)
        {
            return;
        }

        _drawStart = null;
        RegionCanvasInternal.ReleasePointerCapture(e.Pointer);
        DrawModeExited?.Invoke(this, EventArgs.Empty);
    }

    public void SetZoomFit()
    {
        PreviewViewbox.Stretch = Stretch.Uniform;
        PreviewViewbox.HorizontalAlignment = HorizontalAlignment.Stretch;
        PreviewViewbox.VerticalAlignment = VerticalAlignment.Stretch;
    }
}
