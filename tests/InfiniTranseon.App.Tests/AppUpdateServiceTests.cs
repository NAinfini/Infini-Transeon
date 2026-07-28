using System.Text;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Updates;

namespace InfiniTranseon.App.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public void ProductionUpdaterUsesTheOfficialRepositoryAndEmbeddedReleaseKey()
    {
        Assert.Equal(
            "https://api.github.com/repos/NAinfini/Infini-Transeon/releases/latest",
            ReleaseUpdateComposition.LatestReleaseApi.AbsoluteUri);
        var trustRoot = ProductionReleaseTrustRoot.Create();
        Assert.Equal("release-2026-b", trustRoot.Current.KeyId);
        Assert.Equal(32, trustRoot.Current.KeyBytes.Length);
        Assert.Contains(trustRoot.Current.KeyBytes.ToArray(), value => value != 0);
        Assert.Equal(new Version(0, 1, 0), ReleaseUpdateComposition.CurrentApplicationVersion());
    }

    [Fact]
    public async Task ManualCheckPublishesAvailableUpdateAndDownloadsOnlyTheInstaller()
    {
        byte[] installer = Encoding.UTF8.GetBytes("signed msi fixture");
        var core = new StubReleaseUpdateClient(installer);
        var settings = new StubSettingsService(strictOffline: false);
        string root = CreateTemporaryDirectory();
        try
        {
            var service = new RealAppUpdateService(
                core,
                settings,
                new StoppedRuntimeControlService(),
                new AppDataOptions(root),
                new Version(1, 0, 0));
            int changes = 0;
            service.Changed += (_, _) => changes++;

            await service.CheckAsync(
                explicitUserAction: true,
                mainUiVisible: true,
                TestContext.Current.CancellationToken);

            Assert.Equal(AppUpdateStatus.Available, service.Snapshot.Status);
            Assert.Equal("2.0.0", service.Snapshot.AvailableVersion);
            Assert.False(service.Snapshot.InstallerIsAuthenticodeSigned);
            Assert.True(changes >= 2);

            await service.DownloadInstallerAsync(
                userApproved: true,
                TestContext.Current.CancellationToken);

            Assert.Equal(AppUpdateStatus.ReadyToInstall, service.Snapshot.Status);
            Assert.NotNull(service.Snapshot.InstallerPath);
            Assert.Equal(installer, await File.ReadAllBytesAsync(
                service.Snapshot.InstallerPath!,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineModeIsVisibleAndNeverContactsGitHub()
    {
        var core = new StubReleaseUpdateClient(Encoding.UTF8.GetBytes("unused"));
        string root = CreateTemporaryDirectory();
        try
        {
            var service = new RealAppUpdateService(
                core,
                new StubSettingsService(strictOffline: true),
                new StoppedRuntimeControlService(),
                new AppDataOptions(root),
                new Version(1, 0, 0));

            await service.CheckAsync(
                explicitUserAction: true,
                mainUiVisible: true,
                TestContext.Current.CancellationToken);

            Assert.Equal(AppUpdateStatus.DisabledByOfflineMode, service.Snapshot.Status);
            Assert.Equal(0, core.CheckCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadRequiresBothAnAvailableUpdateAndExplicitApproval()
    {
        byte[] installer = Encoding.UTF8.GetBytes("signed msi fixture");
        string root = CreateTemporaryDirectory();
        try
        {
            var service = new RealAppUpdateService(
                new StubReleaseUpdateClient(installer),
                new StubSettingsService(strictOffline: false),
                new StoppedRuntimeControlService(),
                new AppDataOptions(root),
                new Version(1, 0, 0));

            await service.DownloadInstallerAsync(
                userApproved: true,
                TestContext.Current.CancellationToken);
            Assert.Equal(AppUpdateStatus.Failed, service.Snapshot.Status);
            Assert.Equal("update.noAvailableInstaller", service.Snapshot.ErrorCode);

            await service.CheckAsync(
                explicitUserAction: true,
                mainUiVisible: true,
                TestContext.Current.CancellationToken);
            await service.DownloadInstallerAsync(
                userApproved: false,
                TestContext.Current.CancellationToken);

            Assert.Equal(AppUpdateStatus.Available, service.Snapshot.Status);
            Assert.Null(service.Snapshot.InstallerPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "infini-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubReleaseUpdateClient(byte[] installer) : IReleaseUpdateClient
    {
        public int CheckCount { get; private set; }

        public ValueTask<UpdateMetadata?> CheckAsync(
            UpdateCheckContext context,
            CancellationToken cancellationToken)
        {
            if (context.StrictOffline)
                throw new UpdatePolicyException(
                    "update.strictOffline",
                    "Offline mode blocks updates.");
            CheckCount++;
            UpdateMetadata metadata = new(
                new Version(2, 0, 0),
                2,
                "stable",
                "win-x64",
                DateTimeOffset.UtcNow,
                [new UpdateArtifact(
                    new Uri("https://github.com/NAinfini/Infini-Transeon/releases/download/v2.0.0/Infini-Transeon.msi"),
                    "Infini-Transeon.msi",
                    installer.Length,
                    new string('a', 64),
                    ArtifactCodeSigningPolicies.Unsigned,
                    null)],
                "release-2026-b");
            return ValueTask.FromResult<UpdateMetadata?>(metadata);
        }

        public async ValueTask<string> DownloadApprovedAsync(
            UpdateArtifact artifact,
            string destinationPath,
            bool userApproved,
            CancellationToken cancellationToken)
        {
            if (!userApproved)
                throw new UpdatePolicyException("update.approvalRequired", "Approval required.");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, installer, cancellationToken);
            return destinationPath;
        }
    }

    private sealed class StubSettingsService(bool strictOffline) : ISettingsService
    {
        public Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationSettings(
                UiThemePreference.System,
                strictOffline,
                HistoryRetention.Off,
                "en-US"));

        public Task UpdateAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ProviderRow>> GetProvidersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderRow>>([]);

        public Task<ProviderRow> ImportRestAdapterAsync(
            Stream source,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ProviderRow>(new NotSupportedException());

        public Task RemoveCustomProviderAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());
    }

    private sealed class StoppedRuntimeControlService : IRuntimeControlService
    {
        public EngineRuntimeStatus Status => EngineRuntimeStatus.Stopped;
        public EngineRuntimeStatusChange? LastChange => null;
        public bool IsPaused => false;
        public bool IsOverlayVisible => true;
        public event EventHandler<EngineRuntimeStatusChange>? StatusChanged
        {
            add { }
            remove { }
        }
        public event EventHandler? TargetsChanged
        {
            add { }
            remove { }
        }
        public IReadOnlyList<RunningTarget> GetRunningTargets() => [];
        public Task StartAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RequestManualOcrAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
