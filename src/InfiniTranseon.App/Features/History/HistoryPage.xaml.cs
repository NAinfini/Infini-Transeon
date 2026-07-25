using System.Collections.ObjectModel;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;

namespace InfiniTranseon.App.Features.History;

// Display-only wrapper pairing a localized group header with its events. Kept in the page (not the
// presentation models) because label text formatting is a UI-layer concern — the view model only
// exposes the WinUI-free HistoryDateGroup (Kind + DateOnly) so grouping stays unit-testable.
// 属性显式重声明为可写:XAML 类型信息生成器会为 init-only 属性发出赋值代码(CS8852)。
public sealed record HistoryGroupDisplay(string Label, IReadOnlyList<HistoryEvent> Items)
{
    public string Label { get; set; } = Label;

    public IReadOnlyList<HistoryEvent> Items { get; set; } = Items;
}

public sealed partial class HistoryPage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private Guid? _profileId;
    public HistoryPage()
    {
        ViewModel = App.GetService<HistoryViewModel>();
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }

    public ObservableCollection<HistoryEvent> Events => ViewModel.Events;

    public ObservableCollection<HistoryGroupDisplay> DisplayGroups { get; } = [];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _profileId = e.Parameter is Guid profileId && profileId != Guid.Empty
            ? profileId
            : null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectProfile(_profileId);
        await ViewModel.InitializeAsync();
        RefreshDisplayGroups();
    }

    private void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;
        ApplyFilter();
    }

    private void OnDateFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        int days = HistoryDateFilter.SelectedIndex switch
        {
            1 => 1,
            2 => 7,
            3 => 30,
            _ => 0,
        };
        ViewModel.ApplyFilter(HistorySearchBox.Text, days);
        RefreshDisplayGroups();
    }

    // Resolves each pure HistoryDateGroup (Kind + DateOnly) into a localized header string. "Earlier"
    // groups use a culture-aware short date since there is no bounded set of resource strings to
    // localize a specific calendar date.
    private void RefreshDisplayGroups()
    {
        DisplayGroups.Clear();
        foreach (HistoryDateGroup group in ViewModel.GroupedEvents)
        {
            string label = group.Kind switch
            {
                HistoryDateGroupKind.Today => Strings.GetString("HistoryGroupToday"),
                HistoryDateGroupKind.Yesterday => Strings.GetString("HistoryGroupYesterday"),
                _ => group.Date.ToDateTime(TimeOnly.MinValue).ToString(
                    "d",
                    System.Globalization.CultureInfo.CurrentCulture),
            };
            DisplayGroups.Add(new HistoryGroupDisplay(label, group.Items));
        }
    }

    private void OnCopyTextClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string text } || string.IsNullOrEmpty(text))
            return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
