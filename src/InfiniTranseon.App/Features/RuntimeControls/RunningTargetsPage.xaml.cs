using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.RuntimeControls;

public sealed partial class RunningTargetsPage : Page
{
    private static readonly ResourceLoader Strings = new();

    public RunningTargetsPage()
    {
        ViewModel = App.GetService<RunningTargetsViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    public RunningTargetsViewModel ViewModel { get; }

    public string StatusText(EngineRuntimeStatus status) =>
        Strings.GetString(EngineStatusPresenter.ResourceKeyFor(status));

    public StatusSeverity SeverityOf(EngineRuntimeStatus status) =>
        EngineStatusPresenter.SeverityFor(status);

    public string PauseLabel(bool isPaused) =>
        Strings.GetString(isPaused ? "ResumeAllLabel" : "PauseAllLabel");

    public string OverlayLabel(bool isOverlayVisible) =>
        Strings.GetString(isOverlayVisible ? "HideOverlayLabel" : "ShowOverlayLabel");

    public Visibility HasDetail(string detail) =>
        string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
}
