using InfiniTranseon.App.DesignData;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.History;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    public IReadOnlyList<HistoryEvent> Events => SampleData.HistoryEvents;
}
