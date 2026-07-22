using Microsoft.UI.Xaml;

namespace InfiniTranseon.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Shell.AppShell();
        _window.Activate();
    }
}
