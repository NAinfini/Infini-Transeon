using System.Collections.ObjectModel;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Settings;

public sealed partial class ServicesModelsPage : Page
{
    public ServicesModelsPage()
    {
        ViewModel = App.GetService<ServicesModelsViewModel>();
        InitializeComponent();
    }

    public ServicesModelsViewModel ViewModel { get; }

    public ObservableCollection<ProviderRow> Providers => ViewModel.Providers;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();
}
