using System.Text;
using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Security;
using InfiniTranseon.Core.Settings;
using InfiniTranseon.Core.Translation.Local;
using InfiniTranseon.Core.Updates;

namespace InfiniTranseon.App.Tests;

public sealed class LocalModelManagementTests
{
    private const string PublicKey =
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";

    [Fact]
    public void MissingAndInvalidCatalogsAreExplicitAndNeverCreateAnHttpClient()
    {
        using var temp = new TempDirectory();
        int httpClients = 0;
        LocalModelManagementService missing = CreateService(
            temp.Path,
            Path.Combine(temp.Path, "missing.json"),
            () =>
            {
                httpClients++;
                return new HttpClient();
            });

        LocalModelCatalogView missingSnapshot = missing.GetSnapshot();

        Assert.Equal(LocalModelCatalogState.Missing, missingSnapshot.State);
        Assert.Contains("not included", missingSnapshot.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(missingSnapshot.Packages);
        Assert.Equal(0, httpClients);

        string invalidPath = Path.Combine(temp.Path, "invalid.json");
        File.WriteAllText(invalidPath, """{"schemaVersion":1}""");
        LocalModelManagementService invalid = CreateService(
            temp.Path,
            invalidPath,
            () =>
            {
                httpClients++;
                return new HttpClient();
            });

        LocalModelCatalogView invalidSnapshot = invalid.GetSnapshot();

        Assert.Equal(LocalModelCatalogState.Invalid, invalidSnapshot.State);
        Assert.Contains("could not be verified", invalidSnapshot.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invalidSnapshot.Packages);
        Assert.Equal(0, httpClients);
    }

    [Fact]
    public async Task VerifiedCatalogProjectsRealModelMetadataWithoutDownloading()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        string catalogPath = Path.Combine(temp.Path, "catalog.json");
        await File.WriteAllTextAsync(catalogPath, SignedCatalog, ct);
        int httpClients = 0;
        var manager = CreateService(
            temp.Path,
            catalogPath,
            () =>
            {
                httpClients++;
                return new HttpClient();
            });
        var settings = new RealSettingsService(
            new ApplicationSettingsRepository(Path.Combine(temp.Path, "settings.db")),
            new FakeSecretReferenceService(),
            new CustomRestAdapterStore(Path.Combine(temp.Path, "adapters.json")),
            manager);

        ProviderRow model = Assert.Single(
            await settings.GetProvidersAsync(ct),
            row => row.ModelId == "ja-en-basic");

        Assert.Equal("Japanese Basic", model.Name);
        Assert.Equal("Apache-2.0", model.ModelLicense);
        Assert.Equal("phrase-table-v1", model.ModelRuntime);
        Assert.Equal("7 B", model.ModelDownloadSize);
        Assert.Contains("ja → en", model.Detail, StringComparison.Ordinal);
        Assert.True(model.CanDownloadModel);
        Assert.False(model.CanRemoveModel);
        Assert.False(model.IsSelectable);
        Assert.Equal(StatusSeverity.Neutral, model.StateSeverity);
        Assert.Equal(0, httpClients);
    }

    [Fact]
    public async Task InstallRequiresApprovalAndStrictOfflineRejectsBeforeNetworkConstruction()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        string catalogPath = Path.Combine(temp.Path, "catalog.json");
        await File.WriteAllTextAsync(catalogPath, SignedCatalog, ct);
        int httpClients = 0;
        var manager = CreateService(
            temp.Path,
            catalogPath,
            () =>
            {
                httpClients++;
                return new HttpClient();
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync(
            "ja-en-basic",
            "1",
            userApproved: false,
            strictOffline: false,
            progress: null,
            ct).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync(
            "ja-en-basic",
            "1",
            userApproved: true,
            strictOffline: true,
            progress: null,
            ct).AsTask());

        Assert.Equal(0, httpClients);
    }

    [Fact]
    public async Task InstalledPackageRemainsVisibleAndRemovableWithoutACatalog()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        string package = Path.Combine(
            temp.Path,
            "models",
            "packages",
            "retired-model",
            "1");
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(
            Path.Combine(package, "installation.json"),
            "{}",
            ct);
        var manager = CreateService(
            temp.Path,
            Path.Combine(temp.Path, "missing.json"),
            () => throw new InvalidOperationException(
                "Inventory and removal must not create an HTTP client."));

        LocalModelCatalogView snapshot = manager.GetSnapshot();
        LocalModelPackageView retired = Assert.Single(snapshot.Packages);

        Assert.Equal(LocalModelCatalogState.Missing, snapshot.State);
        Assert.Equal(LocalModelInstallState.Uncatalogued, retired.State);
        Assert.Equal("retired-model", retired.ModelId);

        await manager.RemoveAsync(
            retired.ModelId,
            retired.Version,
            userConfirmed: true,
            ct);

        Assert.False(Directory.Exists(package));
    }

    [Fact]
    public async Task ActiveModelLeaseBlocksRemovalUntilTheEngineReleasesIt()
    {
        using var temp = new TempDirectory();
        string package = Path.Combine(
            temp.Path,
            "models",
            "packages",
            "madlad",
            "1");
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(
            Path.Combine(package, "installation.json"),
            "{}",
            TestContext.Current.CancellationToken);
        var usage = new LocalModelManagementService.ModelUsageTracker();
        var manager = new LocalModelManagementService(
            new ModelCatalogService(
                new SignatureVerifier(new Ed25519TrustRootSet(
                    new Ed25519PublicKey("rfc8032", Convert.FromHexString(PublicKey)),
                    null,
                    [])),
                new SignedSequenceState()),
            new ModelPackageService(
                () => throw new InvalidOperationException(),
                Path.Combine(temp.Path, "models"),
                isModelActive: usage.IsActive),
            Path.Combine(temp.Path, "missing.json"),
            static _ => false,
            usage: usage);

        IDisposable lease = manager.AcquireUsage("madlad", "1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAsync(
            "madlad",
            "1",
            userConfirmed: true,
            TestContext.Current.CancellationToken).AsTask());
        lease.Dispose();
        await manager.RemoveAsync(
            "madlad",
            "1",
            userConfirmed: true,
            TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(package));
    }

    private static LocalModelManagementService CreateService(
        string root,
        string catalogPath,
        Func<HttpClient> httpClientFactory) =>
        new(
            new ModelCatalogService(
                new SignatureVerifier(new Ed25519TrustRootSet(
                    new Ed25519PublicKey("rfc8032", Convert.FromHexString(PublicKey)),
                    null,
                    [])),
                new SignedSequenceState()),
            new ModelPackageService(httpClientFactory, Path.Combine(root, "models")),
            catalogPath,
            static _ => false);

    private const string SignedCatalog = """
        {
          "schemaVersion": 1,
          "catalogSequence": 1,
          "publishedAtUtc": "2026-07-24T00:00:00+00:00",
          "models": [
            {
              "modelId": "ja-en-basic",
              "version": "1",
              "licenseSpdx": "Apache-2.0",
              "runtime": "phrase-table-v1",
              "opset": 0,
              "architectures": ["win-x64"],
              "downloadOrigins": ["https://models.example.test/"],
              "files": [
                {
                  "relativePath": "phrase-tables/ja-en-basic.json",
                  "byteSize": 7,
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
              ],
              "displayName": "Japanese Basic",
              "sourceLanguages": ["ja"],
              "targetLanguages": ["en"]
            }
          ],
          "signatures": [
            {
              "keyId": "rfc8032",
              "algorithm": "Ed25519",
              "signature": "pNQPIOvpHj2MvRjPm76ZEj57XcgHVYfwSxlnu9jI7uVi6eqXgbHY1aXnB8XbIDYvDpi1P/v96Fy4UKBAndR1AQ=="
            }
          ]
        }
        """;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "infini-app-model-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
