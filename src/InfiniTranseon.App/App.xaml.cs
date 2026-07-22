using System;
using InfiniTranseon.App.Composition;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Theme;
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
        ApplicationSettings settings;
        try
        {
            Services = PresentationComposition.BuildReal(AppDataOptions.Default);

            // Read the persisted settings up front so the launch reflects the stored theme. The awaits
            // inside the repository use ConfigureAwait(false), so this one-time startup block cannot
            // deadlock the dispatcher. A failure here is a real settings/database fault and is routed to
            // the recovery window rather than silently degraded.
            settings = Services
                .GetRequiredService<ISettingsService>()
                .GetSettingsAsync()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            // Debug-first: surface the real failure in a recovery window; never a fake success path.
            _window = new Shell.CompositionErrorWindow(exception);
            _window.Activate();
            return;
        }

        var shell = new Shell.AppShell();
        _window = shell;
        // Never leave an EngineHost behind: stop and dispose the runtime facade when the window
        // closes. The launcher's kill-on-close job object remains the hard backstop if this
        // graceful path is interrupted.
        shell.Closed += async (_, _) =>
        {
            var runtime = Services.GetRequiredService<IRuntimeControlService>();
            try
            {
                await runtime.StopAsync();
            }
            finally
            {
                if (runtime is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync();
                }
            }
        };
        ThemeMode mode = settings.Theme switch
        {
            UiThemePreference.Light => ThemeMode.Light,
            UiThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (shell.Content is FrameworkElement root)
        {
            ThemeService.Instance.Apply(mode, root);
        }

        _window.Activate();
    }
}
