using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.RuntimeControls;

public sealed partial class RunningTargetsPage : Page
{
    public RunningTargetsPage()
    {
        ViewModel = App.GetService<RunningTargetsViewModel>();
        InitializeComponent();
    }

    public RunningTargetsViewModel ViewModel { get; }

    public IReadOnlyList<RunningTarget> Targets => ViewModel.Targets;
}
