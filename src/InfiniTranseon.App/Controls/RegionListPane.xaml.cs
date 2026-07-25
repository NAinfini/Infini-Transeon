using System.Collections.ObjectModel;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Controls;

/// <summary>
/// The accessible, keyboard/screen-reader-complete alternative to <see cref="RegionCanvas"/>:
/// a checkable region list plus add/duplicate/delete/move-up/move-down commands. Extracted from
/// the former OpticalWorkbenchPage code-behind.
/// </summary>
public sealed partial class RegionListPane : UserControl
{
    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions),
        typeof(ObservableCollection<WorkbenchRegionItem>),
        typeof(RegionListPane),
        new PropertyMetadata(null, OnRegionsChanged));

    public static readonly DependencyProperty SelectedRegionProperty = DependencyProperty.Register(
        nameof(SelectedRegion),
        typeof(WorkbenchRegionItem),
        typeof(RegionListPane),
        new PropertyMetadata(null, OnSelectedRegionChanged));

    private bool _updatingSelection;

    public RegionListPane()
    {
        InitializeComponent();
    }

    /// <summary>The live target region collection. Assigned once per target switch (never
    /// null-then-reassigned), so <see cref="ListView"/> selection and scroll position survive
    /// in-place edits — the underlying items already raise property-changed notifications.</summary>
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

    /// <summary>Raised when the user selects a different region from the list.</summary>
    public event EventHandler<WorkbenchRegionItem?>? SelectionChanged;

    /// <summary>Raised when a region's enabled checkbox is toggled from the list.</summary>
    public event EventHandler<(WorkbenchRegionItem Region, bool Enabled)>? RegionEnabledChanged;

    public event EventHandler? AddRequested;
    public event EventHandler? DuplicateRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? MoveUpRequested;
    public event EventHandler? MoveDownRequested;

    private static void OnRegionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var pane = (RegionListPane)sender;
        pane.RegionListView.ItemsSource = args.NewValue;
    }

    private static void OnSelectedRegionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var pane = (RegionListPane)sender;
        if (pane._updatingSelection)
        {
            return;
        }

        pane._updatingSelection = true;
        pane.RegionListView.SelectedItem = args.NewValue;
        pane._updatingSelection = false;
    }

    private void OnRegionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection)
        {
            return;
        }

        var region = RegionListView.SelectedItem as WorkbenchRegionItem;
        _updatingSelection = true;
        SelectedRegion = region;
        _updatingSelection = false;
        SelectionChanged?.Invoke(this, region);
    }

    private void OnRegionEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: WorkbenchRegionItem region } checkBox)
        {
            return;
        }

        RegionEnabledChanged?.Invoke(this, (region, checkBox.IsChecked.GetValueOrDefault()));
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => AddRequested?.Invoke(this, EventArgs.Empty);

    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this, EventArgs.Empty);

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveUpRequested?.Invoke(this, EventArgs.Empty);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveDownRequested?.Invoke(this, EventArgs.Empty);
}
