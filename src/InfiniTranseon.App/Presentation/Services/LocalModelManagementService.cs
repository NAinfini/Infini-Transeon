using System.Net;
using InfiniTranseon.Contracts.Security;
using InfiniTranseon.Core.Diagnostics;
using InfiniTranseon.Core.Translation.Local;
using InfiniTranseon.Core.Updates;

namespace InfiniTranseon.App.Presentation.Services;

public enum LocalModelCatalogState
{
    Available,
    Missing,
    Invalid,
}

public enum LocalModelInstallState
{
    NotInstalled,
    Installed,
    RuntimeUnavailable,
    Corrupt,
    Uncatalogued,
}

public sealed record LocalModelPackageView(
    string ModelId,
    string Version,
    string DisplayName,
    string LicenseSpdx,
    string Runtime,
    IReadOnlyList<string> SourceLanguages,
    IReadOnlyList<string> TargetLanguages,
    long DownloadBytes,
    string PackageDirectory,
    LocalModelInstallState State);

public sealed record LocalModelCatalogView(
    LocalModelCatalogState State,
    string? Problem,
    IReadOnlyList<LocalModelPackageView> Packages);

public sealed record LocalModelOperationProgress(
    string ModelId,
    string Version,
    string RelativePath,
    int CompletedFiles,
    int TotalFiles,
    long BytesReceived,
    long TotalBytes);

/// <summary>
/// Concrete application facade over the signed catalog and transactional package installer. It
/// deliberately exposes no automatic install path: the only network-capable method requires the
/// caller to pass explicit approval, and strict-offline mode is forwarded to Core before any
/// HttpClient is created.
/// </summary>
public sealed class LocalModelManagementService
{
    private const string CatalogFileName = "model-catalog.json";
    private readonly ModelCatalogService _catalogs;
    private readonly ModelPackageService _packages;
    private readonly string _catalogPath;
    private readonly Func<ModelCatalogEntry, bool> _isRuntimeReady;
    private readonly AppStatusLog? _statusLog;
    private readonly ModelUsageTracker? _usage;
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public LocalModelManagementService(
        ModelCatalogService catalogs,
        ModelPackageService packages,
        string catalogPath,
        Func<ModelCatalogEntry, bool> isRuntimeReady,
        AppStatusLog? statusLog = null,
        ModelUsageTracker? usage = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(isRuntimeReady);
        _catalogs = catalogs;
        _packages = packages;
        _catalogPath = Path.GetFullPath(catalogPath);
        _isRuntimeReady = isRuntimeReady;
        _statusLog = statusLog;
        _usage = usage;
    }

    public static LocalModelManagementService CreateProduction(
        AppDataOptions options,
        AppStatusLog? statusLog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var catalogs = new ModelCatalogService(
            new SignatureVerifier(ProductionReleaseTrustRoot.Create()),
            new FileSignedSequenceState(
                options.ModelCatalogSequencePath,
                "github:NAinfini/Infini-Transeon:models:win-x64"));
        var usage = new ModelUsageTracker();
        var packages = new ModelPackageService(
            CreateHttpClient,
            options.ModelDirectory,
            isModelActive: usage.IsActive);
        return new LocalModelManagementService(
            catalogs,
            packages,
            Path.Combine(AppContext.BaseDirectory, CatalogFileName),
            model => LocalModelRuntimeAvailability.IsReady(
                model,
                AppContext.BaseDirectory),
            statusLog,
            usage);
    }

    public LocalModelCatalogView GetSnapshot()
    {
        if (!File.Exists(_catalogPath))
        {
            IReadOnlyList<LocalModelPackageView> installed =
                DiscoverUncataloguedPackages(out string? inventoryProblem);
            return new LocalModelCatalogView(
                inventoryProblem is null
                    ? LocalModelCatalogState.Missing
                    : LocalModelCatalogState.Invalid,
                inventoryProblem is null
                    ? "The signed model catalog is not included in this release."
                    : "The signed model catalog is not included in this release. " +
                        inventoryProblem,
                installed);
        }

        try
        {
            VerifiedModelCatalog catalog = LoadCatalog();
            LocalModelPackageView[] catalogPackages = catalog.Models
                .Select(model => ToView(
                    model,
                    _packages.Inspect(
                        catalog,
                        model.ModelId,
                        model.Version,
                        _isRuntimeReady(model))))
                .ToArray();
            var known = catalogPackages
                .Select(model => (model.ModelId, model.Version))
                .ToHashSet();
            LocalModelPackageView[] packages =
            [
                .. catalogPackages,
                .. _packages.ListManagedPackages()
                    .Where(package => !known.Contains((package.ModelId, package.Version)))
                    .Select(ToUncataloguedView),
            ];
            return new LocalModelCatalogView(
                LocalModelCatalogState.Available,
                catalogPackages.Length == 0
                    ? "The signed model catalog contains no published packages."
                    : null,
                packages);
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            Record(
                "model.catalog.invalid",
                StatusEventSeverity.Error);
            IReadOnlyList<LocalModelPackageView> installed =
                DiscoverUncataloguedPackages(out string? inventoryProblem);
            return new LocalModelCatalogView(
                LocalModelCatalogState.Invalid,
                $"The signed model catalog could not be verified: {exception.Message}" +
                    (inventoryProblem is null ? string.Empty : $" {inventoryProblem}"),
                installed);
        }
    }

    public IDisposable AcquireUsage(string modelId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return (_usage ?? throw new InvalidOperationException(
            "Local model usage tracking is unavailable in this composition."))
            .Acquire(modelId, version);
    }

    public async ValueTask<LocalModelPackageView> InstallAsync(
        string modelId,
        string version,
        bool userApproved,
        bool strictOffline,
        IProgress<LocalModelOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!userApproved)
            throw new InvalidOperationException(
                "Model installation requires explicit user approval.");
        if (strictOffline)
            throw new InvalidOperationException(
                "Model installation is disabled while strict-offline mode is active.");
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VerifiedModelCatalog catalog = LoadCatalog();
            ModelCatalogEntry model = ResolveModel(catalog, modelId, version);
            Uri origin = model.DownloadOrigins[0];
            Record(
                "model.install.started",
                StatusEventSeverity.Information,
                model);
            var adapter = progress is null
                ? null
                : new DirectProgress<ModelPackageInstallProgress>(value => progress.Report(
                    new LocalModelOperationProgress(
                        value.ModelId,
                        value.Version,
                        value.RelativePath,
                        value.CompletedFiles,
                        value.TotalFiles,
                        value.BytesReceived,
                        value.TotalBytes)));
            ModelPackageSnapshot installed;
            try
            {
                installed = await _packages.InstallAsync(
                    new ModelPackageInstallRequest(
                        catalog,
                        model.ModelId,
                        model.Version,
                        origin,
                        userApproved,
                        strictOffline),
                    _isRuntimeReady(model),
                    adapter,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Record(
                    "model.install.failed",
                    StatusEventSeverity.Error,
                    model);
                throw;
            }
            Record(
                "model.install.completed",
                StatusEventSeverity.Information,
                model);
            return ToView(model, installed);
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async ValueTask RemoveAsync(
        string modelId,
        string version,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        if (!userConfirmed)
            throw new InvalidOperationException(
                "Model removal requires explicit user confirmation.");
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _packages.RemoveAsync(
                    modelId,
                    version,
                    userConfirmed,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Record(
                    "model.remove.failed",
                    StatusEventSeverity.Error,
                    modelId,
                    version);
                throw;
            }
            Record(
                "model.remove.completed",
                StatusEventSeverity.Information,
                modelId,
                version);
        }
        finally
        {
            _mutation.Release();
        }
    }

    private VerifiedModelCatalog LoadCatalog() =>
        _catalogs.LoadVerified(File.ReadAllBytes(_catalogPath));

    private static ModelCatalogEntry ResolveModel(
        VerifiedModelCatalog catalog,
        string modelId,
        string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return catalog.Models.SingleOrDefault(model =>
                string.Equals(model.ModelId, modelId, StringComparison.Ordinal) &&
                string.Equals(model.Version, version, StringComparison.Ordinal)) ??
            throw new InvalidDataException("The requested model is absent from the verified catalog.");
    }

    private static LocalModelPackageView ToView(
        ModelCatalogEntry model,
        ModelPackageSnapshot package) =>
        new(
            model.ModelId,
            model.Version,
            string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelId : model.DisplayName,
            model.LicenseSpdx,
            model.Runtime,
            model.SourceLanguages,
            model.TargetLanguages,
            package.DownloadBytes,
            package.PackageDirectory,
            package.State switch
            {
                ModelPackageState.Installed => LocalModelInstallState.Installed,
                ModelPackageState.RuntimeUnavailable => LocalModelInstallState.RuntimeUnavailable,
                ModelPackageState.Corrupt => LocalModelInstallState.Corrupt,
                _ => LocalModelInstallState.NotInstalled,
            });

    private static LocalModelPackageView ToUncataloguedView(ManagedModelPackage package) =>
        new(
            package.ModelId,
            package.Version,
            package.ModelId,
            "Unknown",
            "Unknown",
            [],
            [],
            0,
            package.PackageDirectory,
            LocalModelInstallState.Uncatalogued);

    private IReadOnlyList<LocalModelPackageView> DiscoverUncataloguedPackages(
        out string? problem)
    {
        try
        {
            problem = null;
            return _packages.ListManagedPackages()
                .Select(ToUncataloguedView)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Record(
                "model.inventory.invalid",
                StatusEventSeverity.Error);
            problem = $"Installed model inventory could not be inspected: {exception.Message}";
            return [];
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.Brotli |
                DecompressionMethods.Deflate |
                DecompressionMethods.GZip,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
    }

    private static bool IsCatalogFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
            SignatureVerificationException or ArgumentException or OverflowException;

    private void Record(
        string behaviorCode,
        StatusEventSeverity severity,
        ModelCatalogEntry? model = null) =>
        Record(behaviorCode, severity, model?.ModelId, model?.Version);

    private void Record(
        string behaviorCode,
        StatusEventSeverity severity,
        string? modelId,
        string? version) =>
        _statusLog?.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "app.model",
            behaviorCode,
            "status.app.model",
            severity,
            modelId is null || version is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>
                {
                    ["modelId"] = modelId,
                    ["version"] = version,
                }));

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public sealed class ModelUsageTracker
    {
        private readonly object _sync = new();
        private readonly Dictionary<(string ModelId, string Version), int> _leases = [];

        public IDisposable Acquire(string modelId, string version)
        {
            lock (_sync)
            {
                var key = (modelId, version);
                _leases[key] = _leases.GetValueOrDefault(key) + 1;
                return new UsageLease(this, key);
            }
        }

        public bool IsActive(string modelId, string version)
        {
            lock (_sync)
            {
                return _leases.ContainsKey((modelId, version));
            }
        }

        private void Release((string ModelId, string Version) key)
        {
            lock (_sync)
            {
                if (!_leases.TryGetValue(key, out int count))
                    return;
                if (count == 1)
                    _leases.Remove(key);
                else
                    _leases[key] = count - 1;
            }
        }

        private sealed class UsageLease(
            ModelUsageTracker owner,
            (string ModelId, string Version) key) : IDisposable
        {
            private ModelUsageTracker? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key);
        }
    }
}
