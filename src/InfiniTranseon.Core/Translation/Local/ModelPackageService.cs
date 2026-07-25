using System.Text.Json;

namespace InfiniTranseon.Core.Translation.Local;

public enum ModelPackageState
{
    NotInstalled,
    Installed,
    RuntimeUnavailable,
    Corrupt,
}

public sealed record ModelPackageSnapshot(
    string ModelId,
    string Version,
    string LicenseSpdx,
    long DownloadBytes,
    string PackageDirectory,
    ModelPackageState State)
{
    public bool IsSelectable => State == ModelPackageState.Installed;
}

public sealed record ModelPackageInstallRequest(
    VerifiedModelCatalog Catalog,
    string ModelId,
    string ModelVersion,
    Uri Origin,
    bool UserApproved,
    bool StrictOffline = false);

public sealed record ModelPackageInstallProgress(
    string ModelId,
    string Version,
    string RelativePath,
    int CompletedFiles,
    int TotalFiles,
    long BytesReceived,
    long TotalBytes);

public sealed record ManagedModelPackage(
    string ModelId,
    string Version,
    string PackageDirectory);

/// <summary>
/// Installs a complete verified model package into an application-owned directory. Network access
/// begins only after explicit approval. Every file is downloaded into a private staging directory;
/// the directory is published only after all signed sizes and hashes have been verified.
/// </summary>
public sealed class ModelPackageService
{
    private const string MarkerFileName = "installation.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<HttpClient> _httpClientFactory;
    private readonly string _managedRoot;
    private readonly Func<string, long> _getAvailableBytes;
    private readonly Func<string, string, bool> _isModelActive;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public ModelPackageService(
        Func<HttpClient> httpClientFactory,
        string managedRoot,
        Func<string, long>? getAvailableBytes = null,
        Func<string, string, bool>? isModelActive = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _httpClientFactory = httpClientFactory;
        _managedRoot = Path.GetFullPath(managedRoot);
        _getAvailableBytes = getAvailableBytes ?? GetAvailableBytes;
        _isModelActive = isModelActive ?? ((_, _) => false);
    }

    public ModelPackageSnapshot Inspect(
        VerifiedModelCatalog catalog,
        string modelId,
        string version,
        bool runtimeReady)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ModelCatalogEntry model = catalog.ResolveModel(modelId, version);
        string packageDirectory = ResolvePackageDirectory(modelId, version);
        long downloadBytes = CheckedTotalBytes(model);
        if (!Directory.Exists(packageDirectory))
        {
            return Snapshot(ModelPackageState.NotInstalled);
        }

        string markerPath = Path.Combine(packageDirectory, MarkerFileName);
        try
        {
            InstallationMarker marker = JsonSerializer.Deserialize<InstallationMarker>(
                File.ReadAllBytes(markerPath), JsonOptions) ??
                throw new InvalidDataException("Model installation marker is empty.");
            if (marker.Files is null)
                throw new InvalidDataException("Model installation marker has no file inventory.");
            Dictionary<string, InstalledFile> markerFiles = marker.Files.ToDictionary(
                file => file.RelativePath,
                StringComparer.Ordinal);
            bool markerMatches =
                marker.SchemaVersion == 2 &&
                marker.CatalogSequence >= 1 &&
                string.Equals(marker.ModelId, model.ModelId, StringComparison.Ordinal) &&
                string.Equals(marker.Version, model.Version, StringComparison.Ordinal) &&
                string.Equals(marker.Runtime, model.Runtime, StringComparison.Ordinal) &&
                marker.Files.Count == model.Files.Count &&
                model.Files.All(file =>
                    markerFiles.TryGetValue(file.RelativePath, out InstalledFile? installed) &&
                    installed.ByteSize == file.ByteSize &&
                    string.Equals(
                        installed.Sha256,
                        file.Sha256,
                        StringComparison.OrdinalIgnoreCase));
            bool filesMatch = markerMatches && model.Files.All(file =>
            {
                string path = ModelPathPolicy.ResolveManagedPath(packageDirectory, file.RelativePath);
                return File.Exists(path) && new FileInfo(path).Length == file.ByteSize;
            });
            if (!filesMatch)
            {
                return Snapshot(ModelPackageState.Corrupt);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or ArgumentException)
        {
            return Snapshot(ModelPackageState.Corrupt);
        }

        return Snapshot(runtimeReady
            ? ModelPackageState.Installed
            : ModelPackageState.RuntimeUnavailable);

        ModelPackageSnapshot Snapshot(ModelPackageState state) => new(
            model.ModelId,
            model.Version,
            model.LicenseSpdx,
            downloadBytes,
            packageDirectory,
            state);
    }

    public IReadOnlyList<ManagedModelPackage> ListManagedPackages()
    {
        string packagesRoot = ResolveChildDirectory("packages");
        if (!Directory.Exists(packagesRoot))
            return [];
        if ((File.GetAttributes(packagesRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The managed package root cannot be a reparse point.");

        var result = new List<ManagedModelPackage>();
        foreach (string modelDirectory in Directory.EnumerateDirectories(packagesRoot))
        {
            if ((File.GetAttributes(modelDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A managed model directory cannot be a reparse point.");
            string modelId = Path.GetFileName(modelDirectory);
            ValidateIdentifier(modelId);
            foreach (string versionDirectory in Directory.EnumerateDirectories(modelDirectory))
            {
                if ((File.GetAttributes(versionDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A managed model version cannot be a reparse point.");
                string version = Path.GetFileName(versionDirectory);
                ValidateIdentifier(version);
                string expected = ResolvePackageDirectory(modelId, version);
                if (!string.Equals(
                        Path.GetFullPath(versionDirectory),
                        expected,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A managed model package has an invalid path.");
                result.Add(new ManagedModelPackage(modelId, version, expected));
            }
        }
        return result;
    }

    public async ValueTask<ModelPackageSnapshot> InstallAsync(
        ModelPackageInstallRequest request,
        bool runtimeReady,
        IProgress<ModelPackageInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserApproved)
            throw new InvalidOperationException("Model installation requires explicit user approval.");
        if (request.StrictOffline)
            throw new InvalidOperationException(
                "Model installation is disabled while strict-offline mode is active.");
        ArgumentNullException.ThrowIfNull(request.Catalog);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ModelCatalogEntry model = request.Catalog.ResolveModel(
                request.ModelId, request.ModelVersion);
            if (!model.DownloadOrigins.Contains(request.Origin))
                throw new InvalidDataException("Model origin is absent from the verified catalog.");

            long totalBytes = CheckedTotalBytes(model);
            Directory.CreateDirectory(_managedRoot);
            string stagingRoot = ResolveStagingDirectory(
                $"{model.ModelId}-{model.Version}");
            ValidateStagingDirectory(stagingRoot);
            long reusableBytes = CountReusableStagedBytes(stagingRoot, model);
            long availableBytes = _getAvailableBytes(_managedRoot);
            long requiredBytes = checked(totalBytes - reusableBytes);
            if (availableBytes < requiredBytes)
                throw new IOException(
                    $"Insufficient disk space for model package. Required: {requiredBytes}; available: {availableBytes}.");

            string destination = ResolvePackageDirectory(model.ModelId, model.Version);
            if (Directory.Exists(destination))
                throw new InvalidOperationException(
                    "The model package directory already exists. Remove the existing package before reinstalling.");

            Directory.CreateDirectory(stagingRoot);
            CleanStagingDirectory(stagingRoot, model);
            var downloader = new ModelDownloadService(_httpClientFactory, stagingRoot);
            long completedBytes = 0;
            for (int index = 0; index < model.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModelCatalogFile file = model.Files[index];
                long fileBase = completedBytes;
                var fileProgress = progress is null
                    ? null
                    : new InlineProgress<ModelDownloadProgress>(value => progress.Report(
                        new ModelPackageInstallProgress(
                            model.ModelId,
                            model.Version,
                            file.RelativePath,
                            index,
                            model.Files.Count,
                            checked(fileBase + value.BytesReceived),
                            totalBytes)));
                await downloader.DownloadAsync(
                    new ModelDownloadRequest(
                        request.Catalog,
                        model.ModelId,
                        model.Version,
                        file.RelativePath,
                        request.Origin,
                        UserApproved: true,
                        StrictOffline: false),
                    fileProgress,
                    cancellationToken).ConfigureAwait(false);
                completedBytes = checked(completedBytes + file.ByteSize);
                progress?.Report(new ModelPackageInstallProgress(
                    model.ModelId,
                    model.Version,
                    file.RelativePath,
                    index + 1,
                    model.Files.Count,
                    completedBytes,
                    totalBytes));
            }

            var marker = new InstallationMarker(
                2,
                request.Catalog.CatalogSequence,
                model.ModelId,
                model.Version,
                model.Runtime,
                DateTimeOffset.UtcNow,
                model.Files.Select(file =>
                    new InstalledFile(file.RelativePath, file.ByteSize, file.Sha256)).ToArray());
            await File.WriteAllBytesAsync(
                Path.Combine(stagingRoot, MarkerFileName),
                JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            CleanStagingDirectory(stagingRoot, model, allowMarker: true);
            string parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            Directory.Move(stagingRoot, destination);
            return Inspect(request.Catalog, model.ModelId, model.Version, runtimeReady);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask RemoveAsync(
        string modelId,
        string version,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!userConfirmed)
            throw new InvalidOperationException("Model removal requires explicit user confirmation.");
        if (_isModelActive(modelId, version))
            throw new InvalidOperationException("The active model cannot be removed.");

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string packageDirectory = ResolvePackageDirectory(modelId, version);
            if (Directory.Exists(packageDirectory))
            {
                if ((File.GetAttributes(packageDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Model package directory cannot be a reparse point.");
                string trashDirectory = ResolveTrashDirectory(
                    $"{modelId}-{version}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path.GetDirectoryName(trashDirectory)!);
                Directory.Move(packageDirectory, trashDirectory);
                Directory.Delete(trashDirectory, recursive: true);
            }
            string stagingDirectory = ResolveStagingDirectory($"{modelId}-{version}");
            if (Directory.Exists(stagingDirectory))
            {
                if ((File.GetAttributes(stagingDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Model staging directory cannot be a reparse point.");
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static long CountReusableStagedBytes(
        string stagingRoot,
        ModelCatalogEntry model)
    {
        if (!Directory.Exists(stagingRoot))
            return 0;
        long total = 0;
        foreach (ModelCatalogFile file in model.Files)
        {
            string complete = ModelPathPolicy.ResolveManagedPath(
                stagingRoot,
                file.RelativePath);
            string partial = complete + ".partial";
            if (File.Exists(complete))
            {
                if ((File.GetAttributes(complete) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Model staging cannot contain a reparse point.");
                total = checked(total + Math.Min(new FileInfo(complete).Length, file.ByteSize));
            }
            else if (File.Exists(partial))
            {
                if ((File.GetAttributes(partial) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Model staging cannot contain a reparse point.");
                total = checked(total + Math.Min(new FileInfo(partial).Length, file.ByteSize));
            }
        }
        return total;
    }

    private static void ValidateStagingDirectory(string stagingRoot)
    {
        if (Directory.Exists(stagingRoot) &&
            (File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Model staging directory cannot be a reparse point.");
    }

    private static void CleanStagingDirectory(
        string stagingRoot,
        ModelCatalogEntry model,
        bool allowMarker = false)
    {
        if (!Directory.Exists(stagingRoot))
            return;
        var allowed = model.Files
            .SelectMany(file => new[]
            {
                file.RelativePath,
                file.RelativePath + ".partial",
            })
            .ToHashSet(StringComparer.Ordinal);
        if (allowMarker)
            allowed.Add(MarkerFileName);
        var pending = new Stack<string>();
        pending.Push(stagingRoot);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Model staging cannot contain a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                string relative = Path.GetRelativePath(stagingRoot, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!allowed.Contains(relative))
                    File.Delete(entry);
            }
        }
    }

    private string ResolvePackageDirectory(string modelId, string version)
    {
        ValidateIdentifier(modelId);
        ValidateIdentifier(version);
        return ResolveChildDirectory(Path.Combine("packages", modelId, version));
    }

    private string ResolveStagingDirectory(string name)
    {
        ValidateIdentifier(name);
        return ResolveChildDirectory(Path.Combine(".staging", name));
    }

    private string ResolveTrashDirectory(string name)
    {
        ValidateIdentifier(name);
        return ResolveChildDirectory(Path.Combine(".trash", name));
    }

    private string ResolveChildDirectory(string relative)
    {
        string root = _managedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model package path escapes the managed directory.");
        return candidate;
    }

    private static void ValidateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 160 || value.Any(character => character is not (
                >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new InvalidDataException("Model package identifier is not path-safe.");
    }

    private static long CheckedTotalBytes(ModelCatalogEntry model)
    {
        long result = 0;
        foreach (ModelCatalogFile file in model.Files)
            result = checked(result + file.ByteSize);
        return result;
    }

    private static long GetAvailableBytes(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ??
            throw new IOException("The managed model directory has no storage root.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private sealed record InstallationMarker(
        int SchemaVersion,
        long CatalogSequence,
        string ModelId,
        string Version,
        string Runtime,
        DateTimeOffset InstalledAtUtc,
        IReadOnlyList<InstalledFile> Files);

    private sealed record InstalledFile(string RelativePath, long ByteSize, string Sha256);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
