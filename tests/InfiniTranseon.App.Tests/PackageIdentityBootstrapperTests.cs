using InfiniTranseon.App.Deployment;

namespace InfiniTranseon.App.Tests;

public sealed class PackageIdentityBootstrapperTests
{
    [Fact]
    public async Task SupportedProcessWithIdentityIsReadyWithoutRegistration()
    {
        var platform = new StubPlatform { HasIdentity = true };
        var bootstrapper = new PackageIdentityBootstrapper(platform);

        PackageIdentityBootstrapOutcome outcome = await bootstrapper.EnsureReadyAsync(
            AppContext.BaseDirectory,
            TestContext.Current.CancellationToken);

        Assert.Equal(PackageIdentityBootstrapOutcome.Ready, outcome);
        Assert.Equal(0, platform.RegistrationCalls);
    }

    [Fact]
    public async Task UnsupportedWindowsIsRejectedBeforeIdentityInspection()
    {
        var platform = new StubPlatform
        {
            WindowsBuild = PackageIdentityBootstrapper.MinimumWindowsBuild - 1,
        };
        var bootstrapper = new PackageIdentityBootstrapper(platform);

        LaunchPrerequisiteException error =
            await Assert.ThrowsAsync<LaunchPrerequisiteException>(() =>
                bootstrapper.EnsureReadyAsync(
                    AppContext.BaseDirectory,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("launch.windows.unsupported", error.ErrorCode);
        Assert.Equal(0, platform.IdentityChecks);
    }

    [Fact]
    public async Task MissingIdentityPackageFailsExplicitly()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "InfiniTranseonIdentityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bootstrapper = new PackageIdentityBootstrapper(new StubPlatform());

            LaunchPrerequisiteException error =
                await Assert.ThrowsAsync<LaunchPrerequisiteException>(() =>
                    bootstrapper.EnsureReadyAsync(
                        directory,
                        TestContext.Current.CancellationToken).AsTask());

            Assert.Equal("launch.identity.packageMissing", error.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task SuccessfulFirstRegistrationRequiresProcessRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "InfiniTranseonIdentityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string package = Path.Combine(
            directory, PackageIdentityBootstrapper.IdentityPackageFileName);
        await File.WriteAllBytesAsync(package, [1], TestContext.Current.CancellationToken);
        var platform = new StubPlatform();
        try
        {
            var bootstrapper = new PackageIdentityBootstrapper(platform);

            PackageIdentityBootstrapOutcome outcome = await bootstrapper.EnsureReadyAsync(
                directory,
                TestContext.Current.CancellationToken);

            Assert.Equal(PackageIdentityBootstrapOutcome.RestartRequired, outcome);
            Assert.Equal(Path.GetFullPath(package), platform.RegisteredPackage);
            Assert.Equal(Path.GetFullPath(directory), platform.RegisteredExternalLocation);
            Assert.False(platform.AllowUnsigned);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubPlatform : IPackageIdentityPlatform
    {
        public int WindowsBuild { get; init; } =
            PackageIdentityBootstrapper.MinimumWindowsBuild;
        public bool HasIdentity { get; init; }
        public int IdentityChecks { get; private set; }
        public int RegistrationCalls { get; private set; }
        public string? RegisteredPackage { get; private set; }
        public string? RegisteredExternalLocation { get; private set; }
        public bool AllowUnsigned { get; private set; }

        public bool HasPackageIdentity()
        {
            IdentityChecks++;
            return HasIdentity;
        }

        public ValueTask RegisterExternalLocationAsync(
            string packagePath,
            string externalLocation,
            bool allowUnsigned,
            CancellationToken cancellationToken)
        {
            RegistrationCalls++;
            RegisteredPackage = packagePath;
            RegisteredExternalLocation = externalLocation;
            AllowUnsigned = allowUnsigned;
            return ValueTask.CompletedTask;
        }
    }
}
