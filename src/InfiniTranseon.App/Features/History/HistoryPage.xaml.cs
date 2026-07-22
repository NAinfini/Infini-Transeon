using System.Collections.ObjectModel;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.History;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        ViewModel = App.GetService<HistoryViewModel>();
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }

    public ObservableCollection<HistoryEvent> Events => ViewModel.Events;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();
}
