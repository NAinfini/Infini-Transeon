using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace InfiniTranseon.App.Deployment;

public enum BorderlessCaptureAuthorizationStatus
{
    Allowed,
    DeniedByUser,
    DeniedBySystem,
    UnavailableWithoutPackageIdentity,
}

public interface IBorderlessCaptureAccessPlatform
{
    ValueTask<AppCapabilityAccessStatus> RequestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Performs the package-only borderless capture authorization flow before EngineHost starts.
/// User/system denial remains a valid, explicit state (EngineHost reports border-required);
/// missing manifest capability and unresolved prompt states are configuration failures.
/// </summary>
public sealed class BorderlessCaptureAuthorization
{
    private readonly IBorderlessCaptureAccessPlatform _platform;

    public BorderlessCaptureAuthorization(IBorderlessCaptureAccessPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _platform = platform;
    }

    public async ValueTask<BorderlessCaptureAuthorizationStatus> RequestAsync(
        CancellationToken cancellationToken = default)
    {
        AppCapabilityAccessStatus status =
            await _platform.RequestAsync(cancellationToken).ConfigureAwait(false);
        return status switch
        {
            AppCapabilityAccessStatus.Allowed =>
                BorderlessCaptureAuthorizationStatus.Allowed,
            AppCapabilityAccessStatus.DeniedByUser =>
                BorderlessCaptureAuthorizationStatus.DeniedByUser,
            AppCapabilityAccessStatus.DeniedBySystem =>
                BorderlessCaptureAuthorizationStatus.DeniedBySystem,
            AppCapabilityAccessStatus.NotDeclaredByApp =>
                throw new LaunchPrerequisiteException(
                    "launch.capture.capabilityMissing",
                    "The identity package does not declare borderless graphics capture."),
            _ => throw new LaunchPrerequisiteException(
                "launch.capture.authorizationUnresolved",
                $"Borderless graphics capture authorization returned '{status}'."),
        };
    }

    public static BorderlessCaptureAuthorization CreateForCurrentProcess() =>
        new(new WindowsBorderlessCaptureAccessPlatform());
}

internal sealed class WindowsBorderlessCaptureAccessPlatform : IBorderlessCaptureAccessPlatform
{
    public async ValueTask<AppCapabilityAccessStatus> RequestAsync(
        CancellationToken cancellationToken) =>
        await GraphicsCaptureAccess
            .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
}
