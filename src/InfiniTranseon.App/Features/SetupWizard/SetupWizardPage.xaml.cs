using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.SetupWizard;

public sealed partial class SetupWizardPage : Page
{
    public SetupWizardPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<SetupWizardViewModel>();
    }

    public SetupWizardViewModel ViewModel { get; }
}
