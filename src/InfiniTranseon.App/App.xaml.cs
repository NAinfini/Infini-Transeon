using System;
using InfiniTranseon.App.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace InfiniTranseon.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>Resolved service provider. Null only if composition failed at startup.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public static T GetService<T>() where T : notnull
        => Services.GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = PresentationComposition.Build();
        }
        catch (Exception exception)
        {
            // Debug-first: surface the real failure in a recovery window; never a fake success path.
            _window = new Shell.CompositionErrorWindow(exception);
            _window.Activate();
            return;
        }

        _window = new Shell.AppShell();
        _window.Activate();
    }
}
