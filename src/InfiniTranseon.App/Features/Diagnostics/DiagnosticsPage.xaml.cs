using InfiniTranseon.App.DesignData;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Diagnostics;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
    }

    public IReadOnlyList<DiagnosticEvent> Events => SampleData.DiagnosticEvents;
}
