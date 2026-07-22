using InfiniTranseon.App.DesignData;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.RuntimeControls;

public sealed partial class RunningTargetsPage : Page
{
    public RunningTargetsPage()
    {
        InitializeComponent();
    }

    public IReadOnlyList<RunningTarget> Targets => SampleData.RunningTargets;
}
