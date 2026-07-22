using InfiniTranseon.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Settings;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (XamlRoot?.Content is FrameworkElement root)
        {
            var mode = ThemeSelector.SelectedIndex switch
            {
                1 => ThemeMode.Light,
                2 => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
            ThemeService.Instance.Apply(mode, root);
        }
    }
}
