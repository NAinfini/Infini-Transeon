using System;
using InfiniTranseon.App.Composition;
using InfiniTranseon.App.Deployment;
using InfiniTranseon.App.Hotkeys;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Theme;
using InfiniTranseon.Core.Diagnostics;
using InfiniTranseon.Core.Storage;
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

    internal static Window? MainWindow { get; private set; }

    internal static GlobalHotkeyService? GlobalHotkeys { get; private set; }

    internal static string? HotkeyInitializationError { get; private set; }

    public static T GetService<T>() where T : notnull
        => Services.GetRequiredService<T>();

    /// <summary>
    /// Points MRT's resolution at the stored UI language. Uses the Windows App SDK override rather
    /// than <c>Windows.Globalization</c>, which needs package identity this app does not have at
    /// process start.
    /// </summary>
    internal static void ApplyUiLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
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
            Services = PresentationComposition.BuildReal(AppDataOptions.Default);
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
                new Dictionary<string, object?>
                {
                    ["accessState"] = authorization,
                }));

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
        _window = shell;
        MainWindow = shell;
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
                new Dictionary<string, object?> { ["error"] = exception.Message }));
            GlobalHotkeys?.Dispose();
            GlobalHotkeys = null;
        }

        try
        {
            // AppShell cancels ordinary close requests and hides to the notification area. This
            // handler therefore runs only for an explicit Exit action and remains the single
            // graceful shutdown path for the runtime and service provider.
            shell.Closed += async (_, _) =>
            {
                GlobalHotkeys?.Dispose();
                GlobalHotkeys = null;
                var runtime = Services.GetRequiredService<IRuntimeControlService>();
                Services.GetRequiredService<AppStatusLog>().Record(new StatusEvent(
                    DateTimeOffset.UtcNow,
                    "app.lifecycle",
                    "app.shutdown.requested",
                    "status.app.shutdown.requested",
                    StatusEventSeverity.Information,
                    new Dictionary<string, object?>()));
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

            _window.Activate();
            shell.ApplyActivationRoute(Program.LaunchActivation);
            _ = CheckForUpdatesAfterLaunchAsync();
            _ = MaintainOcrModelsAfterLaunchAsync(settings.StrictOffline);
        }
        catch (Exception exception)
        {
            ShowCompositionError(exception);
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
                activation.ErrorCode ?? activation.Status.ToString(),
                activation.ErrorDetail ?? "The main shell is not available.");
            return;
        }

        shell.DispatcherQueue.TryEnqueue(() => shell.ApplyActivationRoute(activation));
    }

    internal static void RecordActivationEvent(
        string code,
        StatusEventSeverity severity,
        string errorCode,
        string detail)
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
            new Dictionary<string, object?>
            {
                ["errorCode"] = errorCode,
                ["detail"] = detail,
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

    /// <summary>
    /// Downloads and updates the OCR models the saved profiles need, and retires superseded copies.
    /// The user is never asked and never shown progress; the outcome goes to the status log, where
    /// the activity feed already surfaces model events for anyone who looks.
    ///
    /// Failures are recorded inside the service rather than thrown, because nothing is waiting on
    /// this: an unreachable network simply means the next launch tries again, and the app still runs
    /// with whatever is already on disk.
    /// </summary>
    private static async Task MaintainOcrModelsAfterLaunchAsync(bool strictOffline)
    {
        var profiles = Services.GetRequiredService<ProfileRepository>();
        var provisioning = Services.GetRequiredService<OcrModelProvisioningService>();
        string[] languages =
        [
            .. (await profiles.ListAsync())
                .Select(profile => profile.SourceLanguage)
                .Where(language => !string.IsNullOrWhiteSpace(language))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        await provisioning.EnsureAsync(languages, strictOffline, CancellationToken.None);
    }
}
