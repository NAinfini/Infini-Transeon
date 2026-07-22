using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Settings;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            await ViewModel.InitializeAsync();
            ApplicationSettings settings = ViewModel.Settings;
            ThemeSelector.SelectedIndex = settings.Theme switch
            {
                UiThemePreference.Light => 1,
                UiThemePreference.Dark => 2,
                _ => 0,
            };
            LanguageSelector.SelectedIndex = string.Equals(settings.UiLanguage, "zh-CN", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            OfflineToggle.IsOn = settings.StrictOffline;
            RetentionSelector.SelectedIndex = settings.HistoryRetention switch
            {
                HistoryRetention.Off => 0,
                HistoryRetention.Days90 => 2,
                _ => 1,
            };
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        var (preference, mode) = ThemeSelector.SelectedIndex switch
        {
            1 => (UiThemePreference.Light, ThemeMode.Light),
            2 => (UiThemePreference.Dark, ThemeMode.Dark),
            _ => (UiThemePreference.System, ThemeMode.System),
        };

        if (XamlRoot?.Content is FrameworkElement root)
        {
            ThemeService.Instance.Apply(mode, root);
        }

        if (!_loading)
        {
            await ViewModel.UpdateThemeAsync(preference);
        }
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        string language = LanguageSelector.SelectedIndex == 1 ? "zh-CN" : "en-US";
        await ViewModel.UpdateLanguageAsync(language);
    }

    private async void OnOfflineToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            await ViewModel.UpdateStrictOfflineAsync(OfflineToggle.IsOn);
        }
    }

    private async void OnRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        HistoryRetention retention = RetentionSelector.SelectedIndex switch
        {
            0 => HistoryRetention.Off,
            2 => HistoryRetention.Days90,
            _ => HistoryRetention.Days30,
        };
        await ViewModel.UpdateHistoryRetentionAsync(retention);
    }
}
