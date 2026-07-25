using InfiniTranseon.Core.Diagnostics;
using InfiniTranseon.Core.Ocr;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// What one maintenance pass did. Every list holds model ids (or <c>id@version</c> for removals) so
/// the outcome can be asserted in tests and written to the status log; nothing here is shown to the
/// user, who is not asked to think about OCR packages at all.
/// </summary>
public sealed record OcrModelProvisioningOutcome(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Deferred,
    IReadOnlyList<string> Unavailable,
    IReadOnlyList<string> Failed)
{
    public static OcrModelProvisioningOutcome Nothing { get; } = new([], [], [], [], []);

    public bool ChangedAnything => Installed.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// Keeps the local OCR packages a profile needs present, current, and free of superseded copies,
/// without ever asking the user about it.
///
/// This is the one place in the application that installs a model package without a per-download
/// prompt, and that is deliberate rather than accidental. The user's decision to read a language
/// the Windows recognizer cannot handle is the approval; a dialog offering a 15 MB file the app has
/// already decided it needs is ceremony, not consent. Two safeguards keep it honest and both are
/// visible: strict-offline mode in Settings stops every network call before an HttpClient is
/// constructed, and only the PP-OCR package ids are ever touched — the multi-gigabyte translation
/// model stays behind its explicit prompt.
///
/// Ordering is chosen so an interruption can never leave the user with nothing: the new version is
/// installed first and superseded copies are removed only afterwards.
/// </summary>
public sealed class OcrModelProvisioningService(
    IManagedModelGateway models,
    AppStatusLog? statusLog = null)
{
    private readonly IManagedModelGateway _models =
        models ?? throw new ArgumentNullException(nameof(models));

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Brings the shared detector and one recognizer per requested language up to the newest
    /// published version. Never throws for an expected condition — no catalog, no connection, a
    /// language the catalog does not publish — because there is no user waiting on the result.
    /// </summary>
    public async ValueTask<OcrModelProvisioningOutcome> EnsureAsync(
        IReadOnlyCollection<string> languageCodes,
        bool strictOffline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(languageCodes);
        string[] required = ResolveRequiredModelIds(languageCodes);
        if (required.Length <= 1)
        {
            // Only the shared detector would be required, which on its own reads nothing.
            return OcrModelProvisioningOutcome.Nothing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LocalModelCatalogView snapshot = _models.GetSnapshot();
            if (snapshot.State != LocalModelCatalogState.Available)
            {
                Record("ocr.models.catalogUnavailable", StatusEventSeverity.Warning);
                return OcrModelProvisioningOutcome.Nothing with { Deferred = required };
            }

            var installed = new List<string>();
            var removed = new List<string>();
            var deferred = new List<string>();
            var unavailable = new List<string>();
            var failed = new List<string>();

            foreach (string modelId in required)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LocalModelPackageView[] published =
                [
                    .. snapshot.Packages.Where(package =>
                        string.Equals(package.ModelId, modelId, StringComparison.Ordinal) &&
                        package.State != LocalModelInstallState.Uncatalogued),
                ];
                if (published.Length == 0)
                {
                    unavailable.Add(modelId);
                    continue;
                }

                LocalModelPackageView newest = published.Aggregate((left, right) =>
                    ManagedPaddleOcrModelCatalog.CompareVersions(left.Version, right.Version) >= 0
                        ? left
                        : right);

                if (newest.State is LocalModelInstallState.Corrupt &&
                    !await TryRemoveAsync(newest, removed, failed, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (newest.State is not LocalModelInstallState.Installed)
                {
                    if (strictOffline)
                    {
                        deferred.Add(modelId);
                        continue;
                    }

                    switch (await TryInstallAsync(newest, cancellationToken).ConfigureAwait(false))
                    {
                        case InstallVerdict.Installed:
                            installed.Add(modelId);
                            break;
                        case InstallVerdict.Deferred:
                            deferred.Add(modelId);
                            continue;
                        default:
                            failed.Add(modelId);
                            continue;
                    }
                }

                // Retire superseded copies only once the version that replaces them is in place.
                foreach (LocalModelPackageView stale in published.Where(package =>
                    !string.Equals(package.Version, newest.Version, StringComparison.Ordinal) &&
                    package.State is not LocalModelInstallState.NotInstalled))
                {
                    await TryRemoveAsync(stale, removed, failed, cancellationToken).ConfigureAwait(false);
                }
            }

            var outcome = new OcrModelProvisioningOutcome(
                installed, removed, deferred, unavailable, failed);
            if (outcome.ChangedAnything)
            {
                Record("ocr.models.maintained", StatusEventSeverity.Information);
            }

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The shared detector plus one recognizer per distinct language. "auto" is skipped: the local
    /// models are per-language and cannot detect which one they are looking at, so there is no
    /// package that would satisfy it.
    /// </summary>
    internal static string[] ResolveRequiredModelIds(IReadOnlyCollection<string> languageCodes)
    {
        var required = new List<string> { ManagedPaddleOcrModelCatalog.BaseModelId };
        foreach (string code in languageCodes)
        {
            if (string.IsNullOrWhiteSpace(code) ||
                string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string modelId = ManagedPaddleOcrModelCatalog.RecognitionModelIdPrefix +
                ManagedPaddleOcrModelCatalog.NormalizeLanguageTag(code.Trim());
            if (!required.Contains(modelId, StringComparer.Ordinal))
            {
                required.Add(modelId);
            }
        }

        return [.. required];
    }

    private async ValueTask<InstallVerdict> TryInstallAsync(
        LocalModelPackageView package,
        CancellationToken cancellationToken)
    {
        try
        {
            LocalModelPackageView result = await _models.InstallAsync(
                package.ModelId,
                package.Version,
                userApproved: true,
                strictOffline: false,
                progress: null,
                cancellationToken).ConfigureAwait(false);
            return result.State == LocalModelInstallState.Installed
                ? InstallVerdict.Installed
                : InstallVerdict.Failed;
        }
        catch (HttpRequestException)
        {
            // No connection, or the host is unreachable. Expected on a laptop that opened the app
            // on a train; the next pass picks it up.
            Record("ocr.models.offline", StatusEventSeverity.Information, package.ModelId);
            return InstallVerdict.Deferred;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Record("ocr.models.timedOut", StatusEventSeverity.Information, package.ModelId);
            return InstallVerdict.Deferred;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException)
        {
            Record("ocr.models.installFailed", StatusEventSeverity.Error, package.ModelId);
            return InstallVerdict.Failed;
        }
    }

    private async ValueTask<bool> TryRemoveAsync(
        LocalModelPackageView package,
        List<string> removed,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        try
        {
            await _models.RemoveAsync(
                package.ModelId,
                package.Version,
                userConfirmed: true,
                cancellationToken).ConfigureAwait(false);
            removed.Add($"{package.ModelId}@{package.Version}");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException)
        {
            // A package still in use by a running engine cannot be retired yet. That is not a fault
            // to escalate, but it must be visible rather than swallowed.
            Record("ocr.models.removeFailed", StatusEventSeverity.Warning, package.ModelId);
            failed.Add($"{package.ModelId}@{package.Version}");
            return false;
        }
    }

    private void Record(string behaviorCode, StatusEventSeverity severity, string? modelId = null) =>
        statusLog?.Record(new StatusEvent(
            DateTimeOffset.UtcNow,
            "app.model",
            behaviorCode,
            "status.app.model",
            severity,
            modelId is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["modelId"] = modelId }));

    private enum InstallVerdict
    {
        Installed,
        Deferred,
        Failed,
    }
}
