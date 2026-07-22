using System.Collections.ObjectModel;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Diagnostics;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        ViewModel = App.GetService<DiagnosticsViewModel>();
        InitializeComponent();
    }

    public DiagnosticsViewModel ViewModel { get; }

    public ObservableCollection<DiagnosticEvent> Events => ViewModel.Events;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();
}
