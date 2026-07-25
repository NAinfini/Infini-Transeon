using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace InfiniTranseon.App.Controls;

/// <summary>
/// Card-shaped loading placeholder. Static by default; only pulses opacity when the system
/// "Show animations" setting is enabled, so reduced-motion users get a static skeleton.
/// </summary>
public sealed partial class SkeletonCard : UserControl
{
    private readonly UISettings _uiSettings = new();

    public SkeletonCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_uiSettings.AnimationsEnabled)
        {
            PulseStoryboard.Begin();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => PulseStoryboard.Stop();
}
