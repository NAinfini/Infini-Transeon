using InfiniTranseon.App.Features.SetupWizard;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.ProfileCenter;

public sealed partial class ProfileCenterPage : Page
{
    public ProfileCenterPage()
    {
        ViewModel = App.GetService<ProfileCenterViewModel>();
        InitializeComponent();
    }

    public ProfileCenterViewModel ViewModel { get; }

    public IReadOnlyList<ProfileCard> Profiles => ViewModel.Profiles;

    private void OnNewProfileClick(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(SetupWizardPage));
}
