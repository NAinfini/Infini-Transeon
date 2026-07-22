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

    public RealProfileService(ProfileRepository repository, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _repository = repository;
        _databasePath = Path.GetFullPath(databasePath);
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

    public async Task<Guid> SaveAsync(ProfileEditModel profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileDocument existing = profile.ProfileId == Guid.Empty
            ? new ProfileDocument()
            : await _repository.LoadAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false)
                ?? new ProfileDocument { ProfileId = profile.ProfileId };

        ProfileDocument document = Apply(existing, profile);
        await _repository.SaveAsync(document, cancellationToken).ConfigureAwait(false);
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

    private static ProfileDocument Apply(ProfileDocument existing, ProfileEditModel edit)
    {
        // The wizard carries a provider display name or id; resolve it to the catalog's stable id.
        string providerId = string.IsNullOrWhiteSpace(edit.TranslationProviderId)
            ? string.Empty
            : ProviderCatalog.Find(edit.TranslationProviderId)?.Id ?? edit.TranslationProviderId;

        var regions = edit.Regions.Select(region =>
            ProfileRegion.Create(region.Name, new NormalizedRect(0, 0, 1, 1)) with
            {
                Priority = ToCorePriority(region.Priority),
                TranslationChannels = string.IsNullOrWhiteSpace(providerId)
                    ? []
                    :
                    [
                        ProfileTranslationChannel.Create(providerId) with { DisplayOrder = 0 },
                    ],
            }).ToList();

        var target = ProfileTarget.Create(
            string.IsNullOrWhiteSpace(edit.TargetName) ? edit.Name : edit.TargetName,
            ParseKind(edit.TargetKind)) with
        {
            TargetId = edit.TargetId == Guid.Empty ? Guid.NewGuid() : edit.TargetId,
            Regions = regions,
        };

        ProfileDocument document = existing with
        {
            SchemaVersion = ProfileDocument.CurrentVersion,
            ProfileId = existing.ProfileId == Guid.Empty ? Guid.NewGuid() : existing.ProfileId,
            Name = edit.Name,
            SourceLanguage = edit.SourceLanguage,
            TargetLanguage = edit.TargetLanguage,
            Targets = [target],
        };

        return ProfileDocumentData.WithResolution(document, edit.Resolution);
    }

    private static ProfileCard ToCard(ProfileDocument document)
    {
        ProfileTarget? target = document.Targets.FirstOrDefault();
        int regionCount = document.Targets.Sum(current => current.Regions.Count);
        int channelCount = document.Targets.Sum(current =>
            current.Regions.Sum(region => region.TranslationChannels.Count));
        string resolution = ProfileDocumentData.ReadResolution(document);
        return new ProfileCard(
            document.ProfileId,
            document.Name,
            target is null ? "No capture target" : $"{target.Name} ({target.Kind})",
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
            .Select(region => new ProfileRegionDraft(region.Name, ToDraftPriority(region.Priority)))
            .ToArray() ?? [];
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
            regions);
    }

    private static CaptureTargetKind ParseKind(string kind) =>
        Enum.TryParse(kind, ignoreCase: true, out CaptureTargetKind parsed) ? parsed : CaptureTargetKind.Window;

    private static RegionPriority ToCorePriority(RegionPriorityLevel priority) => priority switch
    {
        RegionPriorityLevel.P0 => RegionPriority.P0,
        RegionPriorityLevel.P1 => RegionPriority.P1,
        RegionPriorityLevel.P2 => RegionPriority.P2,
        _ => RegionPriority.P3,
    };

    private static RegionPriorityLevel ToDraftPriority(RegionPriority priority) => priority switch
    {
        RegionPriority.P0 => RegionPriorityLevel.P0,
        RegionPriority.P1 => RegionPriorityLevel.P1,
        RegionPriority.P2 => RegionPriorityLevel.P2,
        _ => RegionPriorityLevel.P3,
    };
}
