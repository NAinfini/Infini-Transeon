using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Diagnostics;
using InfiniTranseon.Core.Updates;

namespace InfiniTranseon.App.Presentation.Services;

public sealed class RealAppUpdateService : IAppUpdateService
{
    private readonly IReleaseUpdateClient _client;
    private readonly ISettingsService _settings;
    private readonly IRuntimeControlService _runtime;
    private readonly AppDataOptions _options;
    private readonly Version _currentVersion;
    private readonly AppStatusLog? _statusLog;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private UpdateArtifact? _installer;

    public RealAppUpdateService(
        IReleaseUpdateClient client,
        ISettingsService settings,
        IRuntimeControlService runtime,
        AppDataOptions options,
        Version currentVersion,
        AppStatusLog? statusLog = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentVersion);
        _client = client;
        _settings = settings;
        _runtime = runtime;
        _options = options;
        _currentVersion = currentVersion;
        _statusLog = statusLog;
        Snapshot = new AppUpdateSnapshot(AppUpdateStatus.Idle, DisplayVersion(currentVersion));
    }

    public AppUpdateSnapshot Snapshot { get; private set; }

    public event EventHandler? Changed;

    public async Task CheckAsync(
        bool explicitUserAction,
        bool mainUiVisible,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(new AppUpdateSnapshot(AppUpdateStatus.Checking, DisplayVersion(_currentVersion)));
            Record(
                "update.check.started",
                StatusEventSeverity.Information,
                new Dictionary<string, object?>
                {
                    ["explicitUserAction"] = explicitUserAction,
                });
            ApplicationSettings settings =
                await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            var context = new UpdateCheckContext(
                settings.StrictOffline,
                explicitUserAction,
                CaptureTargetActive: _runtime.Status is EngineRuntimeStatus.Running
                    or EngineRuntimeStatus.Restarting,
                mainUiVisible,
                _currentVersion);
            UpdateMetadata? update =
                await _client.CheckAsync(context, cancellationToken).ConfigureAwait(false);
            if (update is null)
            {
                _installer = null;
                Publish(new AppUpdateSnapshot(
                    AppUpdateStatus.UpToDate,
                    DisplayVersion(_currentVersion)));
                Record("update.check.upToDate", StatusEventSeverity.Information);
                return;
            }

            UpdateArtifact[] installers = update.Artifacts
                .Where(artifact => Path.GetExtension(artifact.FileName)
                    .Equals(".msi", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (installers.Length != 1)
                throw new InvalidDataException("A release must contain exactly one MSI installer.");

            _installer = installers[0];
            bool authenticodeSigned =
                _installer.CodeSigning == ArtifactCodeSigningPolicies.Authenticode;
            Publish(new AppUpdateSnapshot(
                AppUpdateStatus.Available,
                DisplayVersion(_currentVersion),
                DisplayVersion(update.Version),
                InstallerIsAuthenticodeSigned: authenticodeSigned));
            Record(
                "update.check.available",
                StatusEventSeverity.Information,
                new Dictionary<string, object?>
                {
                    ["availableVersion"] = DisplayVersion(update.Version),
                    ["verifiedKeyId"] = update.VerifiedKeyId,
                    ["codeSigning"] = _installer.CodeSigning,
                });
        }
        catch (UpdatePolicyException exception) when (exception.Code == "update.strictOffline")
        {
            _installer = null;
            Publish(new AppUpdateSnapshot(
                AppUpdateStatus.DisabledByOfflineMode,
                DisplayVersion(_currentVersion),
                ErrorCode: exception.Code));
            Record(exception.Code, StatusEventSeverity.Information);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _installer = null;
            Publish(new AppUpdateSnapshot(
                AppUpdateStatus.Failed,
                DisplayVersion(_currentVersion),
                ErrorCode: ErrorCodeFor(exception, "update.checkFailed")));
            Record(
                ErrorCodeFor(exception, "update.checkFailed"),
                StatusEventSeverity.Warning,
                new Dictionary<string, object?>
                {
                    ["failureType"] = exception.GetType().Name,
                });
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task DownloadInstallerAsync(
        bool userApproved,
        CancellationToken cancellationToken = default)
    {
        if (!userApproved)
            return;

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_installer is null || string.IsNullOrWhiteSpace(Snapshot.AvailableVersion))
            {
                Publish(Snapshot with
                {
                    Status = AppUpdateStatus.Failed,
                    ErrorCode = "update.noAvailableInstaller",
                });
                Record(
                    "update.noAvailableInstaller",
                    StatusEventSeverity.Warning);
                return;
            }

            Publish(Snapshot with
            {
                Status = AppUpdateStatus.Downloading,
                InstallerPath = null,
                ErrorCode = null,
            });
            string updateDirectory = Path.Combine(
                _options.UpdateDownloadDirectory,
                Snapshot.AvailableVersion);
            string destination = Path.Combine(updateDirectory, _installer.FileName);
            string verifiedPath = await _client.DownloadApprovedAsync(
                _installer,
                destination,
                userApproved: true,
                cancellationToken).ConfigureAwait(false);
            Publish(Snapshot with
            {
                Status = AppUpdateStatus.ReadyToInstall,
                InstallerPath = verifiedPath,
                ErrorCode = null,
            });
            Record(
                "update.download.verified",
                StatusEventSeverity.Information,
                new Dictionary<string, object?>
                {
                    ["availableVersion"] = Snapshot.AvailableVersion,
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Publish(Snapshot with
            {
                Status = AppUpdateStatus.Failed,
                InstallerPath = null,
                ErrorCode = ErrorCodeFor(exception, "update.downloadFailed"),
            });
            Record(
                ErrorCodeFor(exception, "update.downloadFailed"),
                StatusEventSeverity.Warning,
                new Dictionary<string, object?>
                {
                    ["failureType"] = exception.GetType().Name,
                });
        }
        finally
        {
            _operation.Release();
        }
    }

    private void Publish(AppUpdateSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Record(
        string behaviorCode,
        StatusEventSeverity severity,
        IReadOnlyDictionary<string, object?>? data = null) =>
        _statusLog?.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "app.update",
            behaviorCode,
            "status.app.update",
            severity,
            data ?? new Dictionary<string, object?>()));

    private static string ErrorCodeFor(Exception exception, string fallback) =>
        exception switch
        {
            UpdatePolicyException policy => policy.Code,
            SignatureVerificationException signature => signature.Code,
            _ => fallback,
        };

    private static string DisplayVersion(Version version) =>
        version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
}
