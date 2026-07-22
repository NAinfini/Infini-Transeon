using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace InfiniTranseon.App.Features.SetupWizard;

public sealed partial class SetupWizardPage : Page
{
    private Guid _editProfileId;

    public SetupWizardPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<SetupWizardViewModel>();
    }

    public SetupWizardViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _editProfileId = e.Parameter is Guid profileId ? profileId : Guid.Empty;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_editProfileId == Guid.Empty)
        {
            await ViewModel.InitializeAsync();
        }
        else
        {
            await ViewModel.LoadForEditAsync(_editProfileId);
        }
    }

    private void OnAddRegionClick(object sender, RoutedEventArgs e)
    {
        RegionPriorityLevel priority = RegionPrioritySelector.SelectedIndex switch
        {
            1 => RegionPriorityLevel.P1,
            2 => RegionPriorityLevel.P2,
            3 => RegionPriorityLevel.P3,
            _ => RegionPriorityLevel.P0,
        };
        ViewModel.AddRegion(RegionNameBox.Text, priority);
        RegionNameBox.Text = string.Empty;
    }

    private void OnRemoveRegionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileRegionDraft region })
        {
            ViewModel.RemoveRegion(region);
        }
    }

    private async void OnSaveSecretClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SetProviderSecretAsync(ProviderSecretBox.Password);
        ProviderSecretBox.Password = string.Empty;
    }

    private async void OnSaveProfileClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveCommand.ExecuteAsync(null);
        if (ViewModel.SavedProfileId != Guid.Empty && string.IsNullOrEmpty(ViewModel.ErrorMessage))
        {
            Frame.Navigate(typeof(ProfileCenter.ProfileCenterPage));
        }
    }
}
