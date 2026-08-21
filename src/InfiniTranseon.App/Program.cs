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

    // Handing an activation to a live instance is a local call that completes in milliseconds. This
    // budget only has to outlast a busy machine, never a dead one.
    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(5);

    internal static Exception? LaunchPrerequisiteFailure { get; private set; }

    /// <summary>
    /// True when the registered single instance did not accept this process's activation, so this
    /// process took over. Recorded at startup because a stale registration is a real fault the user
    /// should be able to see in the activity feed, not a silent recovery.
    /// </summary>
    internal static bool SingleInstanceRedirectTimedOut { get; private set; }
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
    ///
    /// A registered instance that never answers is treated as gone rather than as a reason to stop:
    /// the point of redirecting is to raise a window the user can see, so when no window can be
    /// raised this process opens its own.
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

            if (TryRedirectActivationTo(primary))
            {
                return false;
            }

            SingleInstanceRedirectTimedOut = true;
            return true;
        }
        catch (Exception exception)
        {
            LaunchPrerequisiteFailure = exception;
            return true;
        }
    }

    /// <summary>
    /// Hands this process's activation to <paramref name="primary"/> and reports whether the handoff
    /// actually landed.
    ///
    /// RedirectActivationToAsync must not be awaited on the STA main thread, which is still pumping
    /// the activation call that produced these arguments, so it is completed on a separate thread.
    /// That wait is bounded because the registration outlives the process it names: after a crash or
    /// a forced kill, FindOrRegisterForKey still hands back the dead instance and the redirect never
    /// completes. An unbounded wait there left the user with a process that had no window, wrote no
    /// log, and never exited — the application simply stopped opening, permanently.
    /// </summary>
    private static bool TryRedirectActivationTo(AppInstance primary)
    {
        AppActivationArguments arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Not disposed: on the timeout path the worker is still inside the redirect call and will set
        // this handle whenever it returns.
        var completed = new ManualResetEventSlim(false);
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
            // Background, so an abandoned redirect can never hold the process open after Main returns.
            IsBackground = true,
        };
        worker.Start();
        if (!completed.Wait(RedirectTimeout))
        {
            return false;
        }

        if (failure is not null)
        {
            throw failure;
        }

        return true;
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

    /// <summary>
    /// Elevated/child-process helper that registers the sparse identity package. It has no window, so
    /// the only channel back to the caller is the exit code and standard error — a bare
    /// <c>catch { ExitCode = 1; }</c> discarded the one piece of information needed to work out why
    /// borderless capture never became available.
    /// </summary>
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
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            Environment.ExitCode = 1;
        }
    }
}
