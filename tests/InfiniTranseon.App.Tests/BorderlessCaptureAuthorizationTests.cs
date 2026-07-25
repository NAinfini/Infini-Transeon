using InfiniTranseon.App.Deployment;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace InfiniTranseon.App.Tests;

public sealed class BorderlessCaptureAuthorizationTests
{
    [Theory]
    [InlineData(
        AppCapabilityAccessStatus.Allowed,
        BorderlessCaptureAuthorizationStatus.Allowed)]
    [InlineData(
        AppCapabilityAccessStatus.DeniedByUser,
        BorderlessCaptureAuthorizationStatus.DeniedByUser)]
    [InlineData(
        AppCapabilityAccessStatus.DeniedBySystem,
        BorderlessCaptureAuthorizationStatus.DeniedBySystem)]
    public async Task MapsResolvedAuthorizationStates(
        AppCapabilityAccessStatus platformStatus,
        BorderlessCaptureAuthorizationStatus expected)
    {
        var authorization = new BorderlessCaptureAuthorization(
            new StubPlatform(platformStatus));

        BorderlessCaptureAuthorizationStatus actual = await authorization.RequestAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task MissingManifestCapabilityFailsExplicitly()
    {
        var authorization = new BorderlessCaptureAuthorization(
            new StubPlatform(AppCapabilityAccessStatus.NotDeclaredByApp));

        LaunchPrerequisiteException error =
            await Assert.ThrowsAsync<LaunchPrerequisiteException>(() =>
                authorization.RequestAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("launch.capture.capabilityMissing", error.ErrorCode);
    }

    private sealed class StubPlatform(AppCapabilityAccessStatus status)
        : IBorderlessCaptureAccessPlatform
    {
        public ValueTask<AppCapabilityAccessStatus> RequestAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(status);
    }
}
