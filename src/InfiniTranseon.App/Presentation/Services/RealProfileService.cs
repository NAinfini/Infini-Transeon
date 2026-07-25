using InfiniTranseon.App.Controls;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;
using Microsoft.Data.Sqlite;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Real profile service backed by the Core SQLite <see cref="ProfileRepository"/>. Maps between the
/// presentation <see cref="ProfileCard"/>/<see cref="ProfileEditModel"/> and the Core
/// <see cref="ProfileDocument"/> so no view model ever depends on Core types. Editing loads the
/// existing document first so the glossary and other data survive a save.
/// </summary>
public sealed class RealProfileService : IProfileService
{
    private readonly ProfileRepository _repository;
    private readonly string _databasePath;
    private readonly Func<string, Task>? _onSourceLanguageSaved;

    /// <param name="onSourceLanguageSaved">
    /// Invoked, without being awaited, after a profile is written with a non-empty source language.
    /// The composition root uses it to fetch the OCR models that language needs. It must not throw
    /// for an expected condition — nothing observes the returned task — and it must never be made
    /// blocking: a save has to complete at UI speed even when a download is starting behind it.
    /// </param>
    public RealProfileService(
        ProfileRepository repository,
        string databasePath,
        Func<string, Task>? onSourceLanguageSaved = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _repository = repository;
        _databasePath = Path.GetFullPath(databasePath);
        _onSourceLanguageSaved = onSourceLanguageSaved;
    }

    public async Task<IReadOnlyList<ProfileCard>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProfileDocument> documents = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(ToCard).ToArray();
    }

    public async Task<ProfileEditModel?> LoadForEditAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return null;
        }

        ProfileDocument? document = await _repository.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToEditModel(document);
    }

    public async Task<IReadOnlyList<string>> GetTranslationProviderIdsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            return [];
        }

        ProfileDocument? document =
            await _repository.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return [];
        }

        return document.Targets
            .SelectMany(target => target.RemainingAreaRegion is null
                ? target.Regions
                : target.Regions.Append(target.RemainingAreaRegion))
            .Where(region => region.Enabled && region.TranslationEnabled)
            .SelectMany(region => region.TranslationChannels)
            .Where(channel => channel.Enabled)
            .SelectMany(channel => new[] { channel.InitialProviderId }
                .Concat(channel.FallbackProviderIds)
                .Concat(channel.RefinementSteps.Select(step => step.ProviderId)))
            .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<Guid> SaveAsync(ProfileEditModel profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileDocument existing = profile.ProfileId == Guid.Empty
            ? new ProfileDocument()
            : await _repository.LoadAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false)
                ?? new ProfileDocument { ProfileId = profile.ProfileId };

        ProfileDocument document = Apply(existing, profile);
        await _repository.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        if (_onSourceLanguageSaved is not null &&
            !string.IsNullOrWhiteSpace(document.SourceLanguage))
        {
            _ = _onSourceLanguageSaved(document.SourceLanguage);
        }

        return document.ProfileId;
    }

    // The Core profile repository has no delete API, so the App issues a direct scoped DELETE against
    // the same SQLite file using the same connection settings the repository uses.
    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM profiles WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportAsync(
        Guid profileId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(destination);
        ProfileDocument document = await _repository.LoadAsync(profileId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Profile was not found.");
        new ProfileArchiveService().Export(document, destination);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> ImportAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ProfileDocument imported = new ProfileArchiveService().Import(source) with
        {
            ProfileId = Guid.NewGuid(),
        };
        await _repository.SaveAsync(imported, cancellationToken).ConfigureAwait(false);
        return imported.ProfileId;
    }

    private static ProfileDocument Apply(ProfileDocument existing, ProfileEditModel edit)
    {
        // The wizard carries a provider display name or id; resolve it to the catalog's stable id.
        CatalogProvider? catalogProvider = string.IsNullOrWhiteSpace(edit.TranslationProviderId)
            ? null
            : ProviderCatalog.Find(edit.TranslationProviderId);
        if (catalogProvider is { IsSelectable: false })
            throw new InvalidOperationException(
                $"Provider '{catalogProvider.DisplayName}' is not installed or runtime-ready.");
        string providerId = string.IsNullOrWhiteSpace(edit.TranslationProviderId)
            ? string.Empty
            : catalogProvider?.Id ?? edit.TranslationProviderId;

        ProfileTarget? primaryExistingTarget = existing.Targets.FirstOrDefault(target =>
            target.TargetId == edit.TargetId) ?? existing.Targets.FirstOrDefault();

        List<ProfileRegion> BuildPrimaryRegions(ProfileTarget? existingTarget)
        {
            var existingRegions = (existingTarget?.Regions ?? [])
                .ToDictionary(region => region.RegionId);
            return edit.Regions.Select(region =>
            {
                ProfileRegion? preserved = region.RegionId == Guid.Empty
                    ? null
                    : existingRegions.GetValueOrDefault(region.RegionId);
                var bounds = new NormalizedRect(region.X, region.Y, region.Width, region.Height);
                List<ProfileTranslationChannel> channels = string.IsNullOrWhiteSpace(providerId)
                    ? preserved?.TranslationChannels ?? []
                    :
                    [
                        ProfileTranslationChannel.Create(providerId) with { DisplayOrder = 0 },
                    ];
                return (preserved ?? ProfileRegion.Create(region.Name, bounds)) with
                {
                    RegionId = region.RegionId == Guid.Empty
                        ? preserved?.RegionId ?? Guid.NewGuid()
                        : region.RegionId,
                    Name = region.Name,
                    Bounds = bounds,
                    Priority = ProfilePresentationMapper.Priority(region.Priority),
                    ContextRole = ProfilePresentationMapper.ContextRole(region.ContextRole),
                    TranslationChannels = channels,
                };
            }).ToList();
        }

        List<ProfileRegion> BuildNewTargetRegions() =>
            edit.Regions.Select(region =>
            {
                var bounds = new NormalizedRect(region.X, region.Y, region.Width, region.Height);
                List<ProfileTranslationChannel> channels = string.IsNullOrWhiteSpace(providerId)
                    ? []
                    :
                    [
                        ProfileTranslationChannel.Create(providerId) with { DisplayOrder = 0 },
                    ];
                return ProfileRegion.Create(region.Name, bounds) with
                {
                    Priority = ProfilePresentationMapper.Priority(region.Priority),
                    ContextRole = ProfilePresentationMapper.ContextRole(region.ContextRole),
                    TranslationChannels = channels,
                };
            }).ToList();

        var targets = new List<ProfileTarget>();
        foreach (ProfileCaptureTargetDraft captureTarget in edit.EffectiveCaptureTargets)
        {
            ProfileTarget? existingTarget = existing.Targets.FirstOrDefault(target =>
                target.TargetId == captureTarget.TargetId);
            bool isPrimary = captureTarget.TargetId == edit.TargetId ||
                captureTarget == edit.EffectiveCaptureTargets[0];
            List<ProfileRegion> regions = isPrimary
                ? BuildPrimaryRegions(primaryExistingTarget)
                : existingTarget?.Regions ?? BuildNewTargetRegions();
            CaptureTargetKind kind = ProfilePresentationMapper.CaptureTargetKind(captureTarget.Kind);
            targets.Add((existingTarget ?? ProfileTarget.Create(captureTarget.Name, kind)) with
            {
                TargetId = captureTarget.TargetId == Guid.Empty
                    ? existingTarget?.TargetId ?? Guid.NewGuid()
                    : captureTarget.TargetId,
                Name = string.IsNullOrWhiteSpace(captureTarget.Name) ? edit.Name : captureTarget.Name,
                Kind = kind,
                DesktopRegion = captureTarget.DesktopRegion,
                Regions = regions,
            });
        }

        ProfileDocument document = existing with
        {
            SchemaVersion = ProfileDocument.CurrentVersion,
            ProfileId = existing.ProfileId == Guid.Empty ? Guid.NewGuid() : existing.ProfileId,
            Name = edit.Name,
            SourceLanguage = edit.SourceLanguage,
            TargetLanguage = edit.TargetLanguage,
            Targets = targets,
        };

        return ProfileDocumentData.WithResolution(document, edit.Resolution);
    }

    private static ProfileCard ToCard(ProfileDocument document)
    {
        ProfileTarget? target = document.Targets.FirstOrDefault();
        // The "scan remaining area" region is a real, translatable region — it is simply stored
        // outside the explicit list. Excluding it made a profile configured with nothing but that
        // region report zero regions, which the workspace readiness check then reads as unusable.
        IEnumerable<ProfileRegion> AllRegions(ProfileTarget current) => current.RemainingAreaRegion is null
            ? current.Regions
            : current.Regions.Append(current.RemainingAreaRegion);
        int regionCount = document.Targets.Sum(current => AllRegions(current).Count());
        int channelCount = document.Targets.Sum(current =>
            AllRegions(current).Sum(region => region.TranslationChannels.Count));
        string resolution = ProfileDocumentData.ReadResolution(document);
        return new ProfileCard(
            document.ProfileId,
            document.Name,
            target is null
                ? "No capture target"
                : document.Targets.Count > 1
                    ? $"{target.Name} ({target.Kind}) +{document.Targets.Count - 1}"
                    : $"{target.Name} ({target.Kind})",
            string.IsNullOrEmpty(resolution) ? "—" : resolution,
            $"{document.SourceLanguage} → {document.TargetLanguage}",
            regionCount,
            channelCount,
            "Ready",
            StatusSeverity.Info,
            "Start");
    }

    private static ProfileEditModel ToEditModel(ProfileDocument document)
    {
        ProfileTarget? target = document.Targets.FirstOrDefault();
        string providerId = target?.Regions
            .SelectMany(region => region.TranslationChannels)
            .Select(channel => channel.InitialProviderId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty;
        var regions = target?.Regions
            .Select(region => new ProfileRegionDraft(
                region.Name,
                ProfilePresentationMapper.Priority(region.Priority),
                region.RegionId,
                region.Bounds.X,
                region.Bounds.Y,
                region.Bounds.Width,
                region.Bounds.Height,
                ProfilePresentationMapper.ContextRole(region.ContextRole)))
            .ToArray() ?? [];
        ProfileCaptureTargetDraft[] captureTargets = document.Targets
            .Select(current => new ProfileCaptureTargetDraft(
                current.TargetId,
                current.Name,
                current.Kind.ToString(),
                ProfileDocumentData.ReadResolution(document),
                current.DesktopRegion))
            .ToArray();
        return new ProfileEditModel(
            document.ProfileId,
            document.Name,
            document.SourceLanguage,
            document.TargetLanguage,
            target?.TargetId ?? Guid.Empty,
            target?.Name ?? string.Empty,
            target?.Kind.ToString() ?? nameof(CaptureTargetKind.Window),
            ProfileDocumentData.ReadResolution(document),
            providerId,
            regions,
            target?.DesktopRegion,
            captureTargets);
    }

}
