using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Settings;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        var (preference, mode) = ThemeSelector.SelectedIndex switch
        {
            1 => (UiThemePreference.Light, ThemeMode.Light),
            2 => (UiThemePreference.Dark, ThemeMode.Dark),
            _ => (UiThemePreference.System, ThemeMode.System),
        };

        ViewModel.UpdateTheme(preference);
        if (XamlRoot?.Content is FrameworkElement root)
        {
            ThemeService.Instance.Apply(mode, root);
        }
    }
}
