using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
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

    public IReadOnlyList<HistoryEvent> Events => ViewModel.Events;
}
