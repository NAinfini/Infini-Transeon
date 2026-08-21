using System;
using InfiniTranseon.App.Composition;
using InfiniTranseon.App.Deployment;
using InfiniTranseon.App.Hotkeys;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.State;
using InfiniTranseon.App.Theme;
using InfiniTranseon.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace InfiniTranseon.App;

public partial class App : Application
{
    private Window? _window;
    private static bool _replacingShell;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>Resolved service provider. Null only if composition failed at startup.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    internal static Window? MainWindow { get; private set; }

    internal static GlobalHotkeyService? GlobalHotkeys { get; private set; }

    internal static string? HotkeyInitializationError { get; private set; }

    public static T GetService<T>() where T : notnull
        => Services.GetRequiredService<T>();

    /// <summary>
    /// Points MRT's resolution at the stored UI language. Uses the Windows App SDK override rather
    /// than <c>Windows.Globalization</c>, which needs package identity this app does not have at
    /// process start.
    ///
    /// The override only governs resource contexts created after it changes, so the cached loader is
    /// discarded here as well. Strings that XAML resolved through <c>x:Uid</c> are fixed in the
    /// element tree that loaded them; <see cref="ReloadShellForLanguageChangeAsync"/> rebuilds that
    /// tree.
    /// </summary>
    internal static void ApplyUiLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        Localization.AppStrings.Reload();
    }

    /// <summary>
    /// Rebuilds the main window so every <c>x:Uid</c> re-resolves in the language now in effect.
    ///
    /// XAML applies <c>x:Uid</c> strings when an element tree is loaded and never revisits them, so
    /// the navigation labels and page content of an open shell keep their original language however
    /// the resource context changes. Replacing the window is the only way to re-run that step. The
    /// service provider, runtime and view models are untouched: they are singletons the new shell
    /// resolves exactly as the old one did, so an unsaved workbench draft survives the swap.
    /// </summary>
    internal static async Task ReloadShellForLanguageChangeAsync()
    {
        // Read live rather than reusing a launch-time snapshot: hotkeys the user edited since startup
        // would otherwise be silently reverted to what they were when the process began.
        ApplicationSettings settings = await Services
            .GetRequiredService<ISettingsService>()
            .GetSettingsAsync();

        if (MainWindow is not Shell.AppShell current)
        {
            return;
        }

        if (Current is not App application)
        {
            return;
        }

        Windows.Graphics.PointInt32 position = current.AppWindow.Position;
        Windows.Graphics.SizeInt32 size = current.AppWindow.Size;
        AppNavigationRequest route = current.DescribeCurrentRoute();

        Shell.AppShell replacement;
        _replacingShell = true;
        try
        {
            current.CloseForShellReplacement();
            replacement = new Shell.AppShell();
            application.AttachShell(replacement, settings);
        }
        finally
        {
            _replacingShell = false;
        }

        replacement.AppWindow.Move(position);
        replacement.AppWindow.Resize(size);
        replacement.Activate();

        var navigation = Services.GetRequiredService<AppNavigationState>();
        if (route.IsWorkspace)
        {
            navigation.NavigateToProfile(route.ProfileId, route.WorkspaceSection!.Value);
        }
        else if (route.GlobalDestination is GlobalDestination destination)
        {
            navigation.Navigate(destination);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ApplicationSettings settings;
        try
        {
            if (Program.LaunchPrerequisiteFailure is Exception prerequisiteFailure)
            {
                throw prerequisiteFailure;
            }
            // Resolved per lookup, not captured: AppStrings.Loader is replaced when the UI language
            // changes, and a captured loader would keep answering in the launch language.
            Services = PresentationComposition.BuildReal(
                AppDataOptions.Default,
                key => Localization.AppStrings.Loader.GetString(key));
            var crashReporter = Services.GetRequiredService<AppCrashReporter>();
            crashReporter.Install();
            // Install() only covers AppDomain and TaskScheduler. UI-thread failures — every throwing
            // async void event handler — arrive here instead, and WinUI fails the process fast right
            // afterwards, so without this hook those crashes leave no report at all. The exception is
            // deliberately left unhandled: recording it must not turn a crash into a silent one.
            UnhandledException += (_, unhandled) =>
                crashReporter.ReportFatal(unhandled.Exception, "crash.ui.unhandled");
            var statusLog = Services.GetRequiredService<AppStatusLog>();
            BorderlessCaptureAuthorizationStatus authorization =
                Program.BorderlessCaptureAuthorization
                ?? throw new InvalidOperationException("launch.capture.authorizationMissing");
            statusLog.Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "app.startup",
                authorization == BorderlessCaptureAuthorizationStatus.Allowed
                    ? "capture.borderless.allowed"
                    : authorization == BorderlessCaptureAuthorizationStatus.DeniedByUser
                        ? "capture.borderless.deniedByUser"
                        : authorization == BorderlessCaptureAuthorizationStatus.DeniedBySystem
                            ? "capture.borderless.deniedBySystem"
                            : "capture.borderless.unavailableWithoutPackageIdentity",
                "status.capture.borderless.authorization",
                authorization == BorderlessCaptureAuthorizationStatus.Allowed
                    ? StatusEventSeverity.Information
                    : StatusEventSeverity.Warning,
                new Dictionary<string, StatusArgument>
                {
                    ["accessState"] = authorization,
                }));

            if (Program.SingleInstanceRedirectTimedOut)
            {
                statusLog.Record(new StatusEvent(
                    DateTimeOffset.UtcNow,
                    "app.startup",
                    "app.singleInstance.redirectTimedOut",
                    "status.app.singleInstance.redirectTimedOut",
                    StatusEventSeverity.Warning,
                    new Dictionary<string, StatusArgument>()));
            }

            // Read the persisted settings up front so the launch reflects the stored theme. The awaits
            // inside the repository use ConfigureAwait(false), so this one-time startup block cannot
            // deadlock the dispatcher. A failure here is a real settings/database fault and is routed to
            // the recovery window rather than silently degraded.
            settings = Services
                .GetRequiredService<ISettingsService>()
                .GetSettingsAsync()
                .GetAwaiter()
                .GetResult();
            // Applied before the shell exists so the first frame already resolves resources in the
            // stored language. Without this the language setting persisted and displayed correctly
            // while never changing a single string.
            ApplyUiLanguage(settings.UiLanguage);
        }
        catch (Exception exception)
        {
            // Debug-first: surface the real failure in a recovery window; never a fake success path.
            ShowCompositionError(exception);
            return;
        }

        Shell.AppShell shell;
        try
        {
            shell = new Shell.AppShell();
        }
        catch (Exception exception)
        {
            ShowCompositionError(exception);
            return;
        }
        try
        {
            AttachShell(shell, settings);
            shell.Activate();
            shell.ApplyActivationRoute(Program.LaunchActivation);
            _ = CheckForUpdatesAfterLaunchAsync();
        }
        catch (Exception exception)
        {
            ShowCompositionError(exception);
        }
    }

    /// <summary>
    /// Makes <paramref name="shell"/> the live main window: hotkeys, theme, and the single graceful
    /// shutdown path. Runs for the launch shell and again for the one that replaces it on a language
    /// change, so the two can never drift apart.
    /// </summary>
    private void AttachShell(Shell.AppShell shell, ApplicationSettings settings)
    {
        _window = shell;
        MainWindow = shell;

        // The service owns a message-only window and holds the process-wide RegisterHotKey claims, so
        // a second one attaching while the first is alive loses every gesture to its own predecessor:
        // Apply would report "conflict" for each binding. Ownership transfers with the shell.
        GlobalHotkeys?.Dispose();
        GlobalHotkeys = null;
        HotkeyInitializationError = null;

        try
        {
            GlobalHotkeys = new GlobalHotkeyService(
                Services.GetRequiredService<IRuntimeControlService>(),
                Services.GetRequiredService<AppStatusLog>());
            GlobalHotkeys.Initialize(shell.DispatcherQueue);
            GlobalHotkeys.Apply(settings.EffectiveHotkeys);
        }
        catch (Exception exception)
        {
            HotkeyInitializationError = exception.Message;
            Services.GetRequiredService<AppStatusLog>().Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "hotkey",
                "hotkey.initialization.failed",
                "status.hotkey.initialization.failed",
                StatusEventSeverity.Error,
                new Dictionary<string, StatusArgument>
                {
                    ["failureType"] = StatusArgument.Id(exception.GetType().Name),
                }));
            GlobalHotkeys?.Dispose();
            GlobalHotkeys = null;
        }

        // AppShell cancels ordinary close requests and hides to the notification area. This handler
        // therefore runs only for an explicit Exit action and remains the single graceful shutdown
        // path for the runtime and service provider — except when the shell is being replaced, where
        // tearing the provider down would take the application with it.
        shell.Closed += async (_, _) =>
        {
            if (_replacingShell)
            {
                return;
            }

            GlobalHotkeys?.Dispose();
            GlobalHotkeys = null;
            var runtime = Services.GetRequiredService<IRuntimeControlService>();
            Services.GetRequiredService<AppStatusLog>().Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "app.lifecycle",
                "app.shutdown.requested",
                "status.app.shutdown.requested",
                StatusEventSeverity.Information,
                new Dictionary<string, StatusArgument>()));
            try
            {
                await runtime.StopAsync();
            }
            finally
            {
                // The provider owns all singleton lifetimes, including the runtime and status log.
                // Disposing it once avoids double-disposing the runtime semaphore and guarantees
                // that the bounded status channel is drained before process exit.
                if (Services is IAsyncDisposable asyncServices)
                {
                    await asyncServices.DisposeAsync();
                }
                else if (Services is IDisposable disposableServices)
                {
                    disposableServices.Dispose();
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
    }

    /// <summary>
    /// Routes an activation that arrived while this instance was already running (a redirected deep
    /// link or shortcut). Runs on the activation callback thread, so the work is marshalled onto the
    /// shell dispatcher.
    /// </summary>
    internal static void HandleActivation(AppActivationParseResult activation)
    {
        if (MainWindow is not Shell.AppShell shell)
        {
            // The recovery window is showing: there is no shell to route into. Recorded rather than
            // dropped so the lost deep link is visible in the activity feed.
            RecordActivationEvent(
                "app.activation.unroutable",
                StatusEventSeverity.Warning,
                activation.ErrorCode ?? activation.Status.ToString());
            return;
        }

        shell.DispatcherQueue.TryEnqueue(() => shell.ApplyActivationRoute(activation));
    }

    /// <summary>
    /// Records why an activation could not be routed. The parse result's error detail is deliberately
    /// not taken: it is prose built from the activation URI, which is exactly the free-form text the
    /// status log refuses to hold. The error code names the cause, and a caller that failed on an
    /// exception adds its type so the family of causes behind one code stays distinguishable.
    /// </summary>
    internal static void RecordActivationEvent(
        string code,
        StatusEventSeverity severity,
        string errorCode,
        string? failureType = null)
    {
        if (Services is null)
        {
            return;
        }

        Services.GetService<AppStatusLog>()?.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "app.activation",
            code,
            "status." + code,
            severity,
            new Dictionary<string, StatusArgument>
            {
                ["errorCode"] = StatusArgument.Id(errorCode),
                ["failureType"] = StatusArgument.Id(failureType),
            }));
    }

    private void ShowCompositionError(Exception exception)
    {
        WriteStartupFailure(exception);
        _window = new Shell.CompositionErrorWindow(exception);
        MainWindow = _window;
        _window.Activate();
    }

    private static void WriteStartupFailure(Exception exception)
    {
        try
        {
            AppDataOptions options = AppDataOptions.Default;
            Directory.CreateDirectory(options.CrashReportDirectory);
            string path = Path.Combine(options.CrashReportDirectory, "startup-failure.log");
            File.WriteAllText(
                path,
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}",
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // Preserve the original startup exception. Recovery-window construction remains the
            // last reporting path when the local data directory itself is unavailable.
        }
    }

    private static async Task CheckForUpdatesAfterLaunchAsync()
    {
        IAppUpdateService updates = Services.GetRequiredService<IAppUpdateService>();
        await updates.CheckAsync(
            explicitUserAction: false,
            mainUiVisible: true);
    }

}
