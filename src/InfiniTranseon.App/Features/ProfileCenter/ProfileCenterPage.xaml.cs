using System.Collections.ObjectModel;
using InfiniTranseon.App.Features.SetupWizard;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.ProfileCenter;

public sealed partial class ProfileCenterPage : Page
{
    private static readonly ResourceLoader Strings = new();

    public ProfileCenterPage()
    {
        ViewModel = App.GetService<ProfileCenterViewModel>();
        InitializeComponent();
    }

    public ProfileCenterViewModel ViewModel { get; }

    public ObservableCollection<ProfileCard> Profiles => ViewModel.Profiles;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();

    private void OnNewProfileClick(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(SetupWizardPage));

    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid profileId } && profileId != System.Guid.Empty)
        {
            Frame.Navigate(typeof(SetupWizardPage), profileId);
        }
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId } || profileId == System.Guid.Empty)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Strings.GetString("DeleteProfileTitle"),
            Content = Strings.GetString("DeleteProfileBody"),
            PrimaryButtonText = Strings.GetString("DeleteProfileConfirm"),
            CloseButtonText = Strings.GetString("DeleteProfileCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteAsync(profileId);
        }
    }
}
