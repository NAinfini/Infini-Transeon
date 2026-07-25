using InfiniTranseon.App.Presentation.Services;

namespace InfiniTranseon.App.Tests;

public sealed class OcrModelProvisioningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RequiredModelsAreTheSharedDetectorPlusOneRecognizerPerLanguage() =>
        Assert.Equal(
            ["ppocr-v4-base", "ppocr-v4-rec-ja", "ppocr-v4-rec-zh-hans"],
            OcrModelProvisioningService.ResolveRequiredModelIds(["ja-JP", "zh-CN", "ja"]));

    /// <summary>
    /// The local models cannot detect their own language, so "auto" asks for nothing. Downloading
    /// some arbitrary recognizer for it would only produce confident nonsense later.
    /// </summary>
    [Fact]
    public void AutomaticSourceLanguageRequiresNothing() =>
        Assert.Equal(
            ["ppocr-v4-base"],
            OcrModelProvisioningService.ResolveRequiredModelIds(["auto", "  "]));

    [Fact]
    public async Task NothingIsFetchedWhenNoProfileNamesALanguage()
    {
        var gateway = new RecordingGateway();
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["auto"], false, Ct);

        Assert.False(outcome.ChangedAnything);
        Assert.Empty(gateway.Installed);
        Assert.Equal(0, gateway.Snapshots);
    }

    [Fact]
    public async Task MissingPackagesAreInstalledWithoutBeingAskedAbout()
    {
        var gateway = new RecordingGateway(
            Package("ppocr-v4-base", "4.0.0", LocalModelInstallState.NotInstalled),
            Package("ppocr-v4-rec-ja", "4.0.0", LocalModelInstallState.NotInstalled));
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["ja"], false, Ct);

        Assert.Equal(["ppocr-v4-base", "ppocr-v4-rec-ja"], outcome.Installed);
        Assert.Empty(outcome.Failed);
        Assert.All(gateway.Installed, request => Assert.True(request.UserApproved));
    }

    /// <summary>
    /// The new version must be in place before the old one is retired, so an interrupted update can
    /// never leave the user with no model at all.
    /// </summary>
    [Fact]
    public async Task NewerVersionsInstallBeforeSupersededCopiesAreRemoved()
    {
        var gateway = new RecordingGateway(
            Package("ppocr-v4-base", "4.0.0", LocalModelInstallState.Installed),
            Package("ppocr-v4-rec-ja", "4.0.0", LocalModelInstallState.Installed),
            Package("ppocr-v4-rec-ja", "4.1.0", LocalModelInstallState.NotInstalled));
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["ja"], false, Ct);

        Assert.Equal(["ppocr-v4-rec-ja"], outcome.Installed);
        Assert.Equal(["ppocr-v4-rec-ja@4.0.0"], outcome.Removed);
        Assert.Equal(["install:ppocr-v4-rec-ja@4.1.0", "remove:ppocr-v4-rec-ja@4.0.0"], gateway.Order);
    }

    [Fact]
    public async Task AlreadyCurrentPackagesAreLeftAlone()
    {
        var gateway = new RecordingGateway(
            Package("ppocr-v4-base", "4.0.0", LocalModelInstallState.Installed),
            Package("ppocr-v4-rec-en", "4.0.0", LocalModelInstallState.Installed));
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["en-GB"], false, Ct);

        Assert.False(outcome.ChangedAnything);
        Assert.Empty(gateway.Order);
    }

    /// <summary>A damaged package is discarded and refetched rather than reported to the user.</summary>
    [Fact]
    public async Task CorruptPackagesAreReplaced()
    {
        var gateway = new RecordingGateway(
            Package("ppocr-v4-base", "4.0.0", LocalModelInstallState.Installed),
            Package("ppocr-v4-rec-ja", "4.0.0", LocalModelInstallState.Corrupt));
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["ja"], false, Ct);

        Assert.Equal(["remove:ppocr-v4-rec-ja@4.0.0", "install:ppocr-v4-rec-ja@4.0.0"], gateway.Order);
        Assert.Equal(["ppocr-v4-rec-ja"], outcome.Installed);
    }

    /// <summary>
    /// Strict-offline mode is the visible switch that turns the silent downloading off. It must stop
    /// the attempt before any network call, not merely report a failure afterwards.
    /// </summary>
    [Fact]
    public async Task StrictOfflineDefersEverythingWithoutTouchingTheNetwork()
    {
        var gateway = new RecordingGateway(
            Package("ppocr-v4-base", "4.0.0", LocalModelInstallState.NotInstalled),
            Package("ppocr-v4-rec-ja", "4.0.0", LocalModelInstallState.NotInstalled));
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["ja"], true, Ct);

        Assert.Equal(["ppocr-v4-base", "ppocr-v4-rec-ja"], outcome.Deferred);
        Assert.Empty(gateway.Order);
    }

    [Fact]
    public async Task AnUnreachableHostIsDeferredRatherThanReportedAsAFailure()
    {
        var gateway = new RecordingGateway(
            Package("ppocr-v4-base", "4.0.0", LocalModelInstallState.NotInstalled))
        {
            InstallFailure = () => new HttpRequestException("no route to host"),
        };
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["ja"], false, Ct);

        Assert.Equal(["ppocr-v4-base"], outcome.Deferred);
        Assert.Empty(outcome.Failed);
    }

    [Fact]
    public async Task ALanguageTheCatalogDoesNotPublishIsReportedAsUnavailable()
    {
        var gateway = new RecordingGateway(
            Package("ppocr-v4-base", "4.0.0", LocalModelInstallState.Installed));
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["ko"], false, Ct);

        Assert.Equal(["ppocr-v4-rec-ko"], outcome.Unavailable);
        Assert.Empty(outcome.Failed);
    }

    /// <summary>
    /// Without a verified catalog there is nothing to trust, and installing anyway would defeat the
    /// signature check the whole delivery path is built on.
    /// </summary>
    [Fact]
    public async Task AnUnverifiedCatalogInstallsNothing()
    {
        var gateway = new RecordingGateway { State = LocalModelCatalogState.Invalid };
        var service = new OcrModelProvisioningService(gateway);

        OcrModelProvisioningOutcome outcome = await service.EnsureAsync(["ja"], false, Ct);

        Assert.Equal(["ppocr-v4-base", "ppocr-v4-rec-ja"], outcome.Deferred);
        Assert.Empty(gateway.Order);
    }

    private static LocalModelPackageView Package(
        string modelId,
        string version,
        LocalModelInstallState state) =>
        new(modelId, version, modelId, "Apache-2.0", "ppocr-onnx-v4", [], [], 1, modelId, state);

    private sealed class RecordingGateway(params LocalModelPackageView[] packages) : IManagedModelGateway
    {
        private readonly List<LocalModelPackageView> _packages = [.. packages];

        public LocalModelCatalogState State { get; init; } = LocalModelCatalogState.Available;

        public Func<Exception>? InstallFailure { get; init; }

        public int Snapshots { get; private set; }

        public List<(string ModelId, string Version, bool UserApproved)> Installed { get; } = [];

        public List<string> Order { get; } = [];

        public LocalModelCatalogView GetSnapshot()
        {
            Snapshots++;
            return new LocalModelCatalogView(State, null, _packages);
        }

        public ValueTask<LocalModelPackageView> InstallAsync(
            string modelId,
            string version,
            bool userApproved,
            bool strictOffline,
            IProgress<LocalModelOperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (InstallFailure is not null)
            {
                throw InstallFailure();
            }

            Installed.Add((modelId, version, userApproved));
            Order.Add($"install:{modelId}@{version}");
            var view = Package(modelId, version, LocalModelInstallState.Installed);
            _packages.RemoveAll(package =>
                package.ModelId == modelId && package.Version == version);
            _packages.Add(view);
            return ValueTask.FromResult(view);
        }

        public ValueTask RemoveAsync(
            string modelId,
            string version,
            bool userConfirmed,
            CancellationToken cancellationToken)
        {
            Order.Add($"remove:{modelId}@{version}");
            _packages.RemoveAll(package =>
                package.ModelId == modelId && package.Version == version);
            return ValueTask.CompletedTask;
        }
    }
}
