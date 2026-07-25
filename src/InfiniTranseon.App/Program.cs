using System;
using System.Threading;
using InfiniTranseon.App.Deployment;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace InfiniTranseon.App;

// Custom entry point (generated Main disabled via DISABLE_XAML_GENERATED_MAIN). The DI graph is
// composed inside App startup so construction failures can open a local recovery window rather
// than reporting fake success.
public static class Program
{
    // A tray-resident app must stay single-instance: a second process would start a second engine
    // against the same profile database. Deep links therefore redirect into the running instance.
    private const string SingleInstanceKey = "InfiniTranseon.Desktop.Main";

    internal static Exception? LaunchPrerequisiteFailure { get; private set; }
    internal static BorderlessCaptureAuthorizationStatus? BorderlessCaptureAuthorization { get; private set; }

    /// <summary>Route requested by the command line that started this process.</summary>
    internal static AppActivationParseResult LaunchActivation { get; private set; } =
        AppActivationParseResult.None;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Length == 1 &&
            string.Equals(
                args[0],
                PackageIdentityBootstrapper.IdentityRegistrationArgument,
                StringComparison.Ordinal))
        {
            RunIdentityRegistrationHelper();
            return;
        }

        LaunchActivation = AppActivationRouteParser.ParseCommandLine(args);

        // Claimed before the capture-authorization prompt so a redirecting process never prompts.
        if (!TryClaimPrimaryInstance())
        {
            return;
        }

        try
        {
            var identityPlatform = new WindowsPackageIdentityPlatform();
            if (identityPlatform.WindowsBuild < PackageIdentityBootstrapper.MinimumWindowsBuild)
            {
                throw new LaunchPrerequisiteException(
                    "launch.windows.unsupported",
                    $"Infini-Transeon requires Windows build {PackageIdentityBootstrapper.MinimumWindowsBuild} or newer.");
            }

            BorderlessCaptureAuthorization = identityPlatform.HasPackageIdentity()
                ? Deployment.BorderlessCaptureAuthorization
                    .CreateForCurrentProcess()
                    .RequestAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                : BorderlessCaptureAuthorizationStatus.UnavailableWithoutPackageIdentity;
        }
        catch (Exception exception)
        {
            LaunchPrerequisiteFailure = exception;
        }
        Application.Start(callbackParams =>
        {
            _ = callbackParams;
            DispatcherQueueSynchronizationContext context = new(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    /// <summary>
    /// Registers this process as the single instance, or hands its activation to the one already
    /// running and reports that the caller should exit. A redirect failure is not swallowed: it is
    /// promoted to a launch prerequisite failure so the recovery window shows the real exception
    /// instead of a silent second instance.
    /// </summary>
    private static bool TryClaimPrimaryInstance()
    {
        try
        {
            AppInstance primary = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (primary.IsCurrent)
            {
                primary.Activated += OnPrimaryInstanceActivated;
                return true;
            }

            RedirectActivationTo(primary);
            return false;
        }
        catch (Exception exception)
        {
            LaunchPrerequisiteFailure = exception;
            return true;
        }
    }

    // RedirectActivationToAsync must not be awaited on the STA main thread, which is still pumping
    // the activation call that produced these arguments. The documented workaround is to complete it
    // on a separate thread and block here until it finishes.
    private static void RedirectActivationTo(AppInstance primary)
    {
        AppActivationArguments arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var completed = new ManualResetEventSlim(false);
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                primary.RedirectActivationToAsync(arguments).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = false,
        };
        worker.Start();
        completed.Wait();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static void OnPrimaryInstanceActivated(object? sender, AppActivationArguments arguments) =>
        App.HandleActivation(ResolveActivation(arguments));

    /// <summary>Translates a Windows activation into a route, covering protocol and launch kinds.</summary>
    internal static AppActivationParseResult ResolveActivation(AppActivationArguments arguments) =>
        arguments.Kind switch
        {
            ExtendedActivationKind.Protocol when arguments.Data is IProtocolActivatedEventArgs protocol =>
                AppActivationRouteParser.ParseUri(protocol.Uri.ToString()),
            ExtendedActivationKind.Launch when arguments.Data is ILaunchActivatedEventArgs launch =>
                AppActivationRouteParser.ParseCommandLineString(launch.Arguments),
            _ => AppActivationParseResult.None,
        };

    private static void RunIdentityRegistrationHelper()
    {
        try
        {
            WindowsPackageIdentityPlatform
                .RegisterCurrentApplicationIdentityDirectAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = 0;
        }
        catch
        {
            Environment.ExitCode = 1;
        }
    }
}
