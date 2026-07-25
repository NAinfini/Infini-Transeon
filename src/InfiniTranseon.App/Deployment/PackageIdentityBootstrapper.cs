using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Management.Deployment;

namespace InfiniTranseon.App.Deployment;

public enum PackageIdentityBootstrapOutcome
{
    Ready,
    RestartRequired,
}

public sealed class LaunchPrerequisiteException : Exception
{
    public LaunchPrerequisiteException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public interface IPackageIdentityPlatform
{
    int WindowsBuild { get; }
    bool HasPackageIdentity();
    ValueTask RegisterExternalLocationAsync(
        string packagePath,
        string externalLocation,
        bool allowUnsigned,
        CancellationToken cancellationToken);
}

/// <summary>
/// Enforces the portable/MSI launch prerequisites before WinUI starts. An unpackaged first launch
/// registers the external-location identity package against the exact executable directory,
/// then requires a clean process restart so package-only capture consent APIs are never called from
/// the original identity-less process.
/// </summary>
public sealed class PackageIdentityBootstrapper
{
    public const int MinimumWindowsBuild = 22621;
    public const string IdentityPackageFileName = "InfiniTranseon.Identity.msix";
    public const string IdentityRegistrationArgument = "--register-package-identity";

    private readonly IPackageIdentityPlatform _platform;

    public PackageIdentityBootstrapper(IPackageIdentityPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _platform = platform;
    }

    public async ValueTask<PackageIdentityBootstrapOutcome> EnsureReadyAsync(
        string applicationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        string externalLocation = Path.GetFullPath(applicationDirectory);
        if (_platform.WindowsBuild < MinimumWindowsBuild)
            throw new LaunchPrerequisiteException(
                "launch.windows.unsupported",
                $"Infini-Transeon requires Windows build {MinimumWindowsBuild} or newer.");
        if (_platform.HasPackageIdentity())
            return PackageIdentityBootstrapOutcome.Ready;

        string packagePath = Path.Combine(externalLocation, IdentityPackageFileName);
        if (!File.Exists(packagePath))
            throw new LaunchPrerequisiteException(
                "launch.identity.packageMissing",
                $"The required identity package is missing: {packagePath}");

        try
        {
            await _platform.RegisterExternalLocationAsync(
                packagePath,
                externalLocation,
                allowUnsigned: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new LaunchPrerequisiteException(
                "launch.identity.registrationFailed",
                $"Package identity registration failed: {exception.Message}");
        }
        return PackageIdentityBootstrapOutcome.RestartRequired;
    }

    public static PackageIdentityBootstrapper CreateForCurrentProcess() =>
        new(new WindowsPackageIdentityPlatform());
}

internal sealed class WindowsPackageIdentityPlatform : IPackageIdentityPlatform
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public int WindowsBuild => Environment.OSVersion.Version.Build;

    public bool HasPackageIdentity()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        return result switch
        {
            ErrorSuccess => true,
            // The length-only probe returns ERROR_INSUFFICIENT_BUFFER when a package full name
            // exists; no second call is needed because this method only asks whether identity exists.
            ErrorInsufficientBuffer => true,
            AppModelErrorNoPackage => false,
            _ => throw new Win32Exception(result, "Could not determine the current package identity."),
        };
    }

    public async ValueTask RegisterExternalLocationAsync(
        string packagePath,
        string externalLocation,
        bool allowUnsigned,
        CancellationToken cancellationToken)
    {
        if (allowUnsigned && !IsProcessElevated())
        {
            await RunElevatedRegistrationHelperAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await RegisterExternalLocationDirectAsync(
            packagePath,
            externalLocation,
            allowUnsigned,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask RegisterCurrentApplicationIdentityDirectAsync(
        CancellationToken cancellationToken = default)
    {
        string externalLocation = Path.GetFullPath(AppContext.BaseDirectory);
        string packagePath = Path.Combine(
            externalLocation,
            PackageIdentityBootstrapper.IdentityPackageFileName);
        if (!File.Exists(packagePath))
            throw new FileNotFoundException(
                "The package identity file is missing.",
                packagePath);

        await RegisterExternalLocationDirectAsync(
            packagePath,
            externalLocation,
            allowUnsigned: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RegisterExternalLocationDirectAsync(
        string packagePath,
        string externalLocation,
        bool allowUnsigned,
        CancellationToken cancellationToken)
    {
        var options = new AddPackageOptions
        {
            ExternalLocationUri = new Uri(externalLocation),
            AllowUnsigned = allowUnsigned,
        };
        DeploymentResult result = await new PackageManager()
            .AddPackageByUriAsync(new Uri(packagePath), options)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (result.ExtendedErrorCode is Exception error)
            throw new InvalidOperationException(
                $"0x{error.HResult:X8}: {result.ErrorText}", error);
    }

    private static bool IsProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async ValueTask RunElevatedRegistrationHelperAsync(
        CancellationToken cancellationToken)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The application executable path is unavailable.");
        Process? helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = PackageIdentityBootstrapper.IdentityRegistrationArgument,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "Package identity registration was canceled at the Windows consent prompt.",
                exception,
                cancellationToken);
        }
        if (helper is null)
            throw new InvalidOperationException(
                "Windows did not start the package identity registration helper.");

        using (helper)
        {
            await helper.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (helper.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Package identity registration helper exited with code {helper.ExitCode}.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        char[]? packageFullName);
}
