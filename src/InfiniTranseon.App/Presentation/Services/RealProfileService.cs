using System.Globalization;
using InfiniTranseon.App.Controls;
using InfiniTranseon.Contracts.Probes;
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
public sealed class RealProfileService : IProfileService, IProfileTargetDirectory
{
    private readonly ProfileRepository _repository;
    private readonly ICaptureProbe _captureProbe;
    private readonly ResourceTextLookup _text;
    private readonly string _databasePath;

    public RealProfileService(
        ProfileRepository repository,
        ICaptureProbe captureProbe,
        ResourceTextLookup text,
        string databasePath)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(captureProbe);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _repository = repository;
        _captureProbe = captureProbe;
        _text = text;
        _databasePath = Path.GetFullPath(databasePath);
    }

    /// <summary>
    /// Lists the stored profiles with the state each one is actually in. The machine is enumerated
    /// once for the whole list rather than once per card: the answer is the same for all of them, and
    /// walking every top-level window is not free.
    /// </summary>
    public async Task<IReadOnlyList<ProfileCard>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProfileDocument> documents = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        CaptureProbeResult probe = await _captureProbe
            .ProbeAsync(new CaptureProbeRequest(NameFilter: null), cancellationToken)
            .ConfigureAwait(false);
        return documents.Select(document => ToCard(document, probe.Targets)).ToArray();
    }

    public async Task<IReadOnlyList<ProfileTargetDirectoryEntry>> GetTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProfileDocument> documents =
            await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return documents.SelectMany(document => document.Targets.Select(target =>
            new ProfileTargetDirectoryEntry(
                document.ProfileId,
                target.TargetId,
                document.Name,
                target.Name))).ToArray();
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

    public async Task<IReadOnlyList<string>> GetRequiredProviderIdsAsync(
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

        return ProfileProviderReadiness.GetRequiredProviderIds(document);
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
        string? existingPrimaryProviderId = primaryExistingTarget?.Regions
            .SelectMany(region => region.TranslationChannels)
            .Select(channel => channel.InitialProviderId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        bool primaryProviderChanged = !string.IsNullOrWhiteSpace(providerId) &&
            !string.Equals(
                providerId,
                existingPrimaryProviderId,
                StringComparison.OrdinalIgnoreCase);

        List<ProfileTranslationChannel> PrimaryChannels(ProfileRegion? preserved)
        {
            if (preserved is null)
            {
                return string.IsNullOrWhiteSpace(providerId)
                    ? []
                    : [ProfileTranslationChannel.Create(providerId) with { DisplayOrder = 0 }];
            }
            if (!primaryProviderChanged || string.IsNullOrWhiteSpace(providerId))
            {
                return preserved.TranslationChannels;
            }

            List<ProfileTranslationChannel> channels = preserved.TranslationChannels.ToList();
            if (channels.Count == 0)
            {
                channels.Add(ProfileTranslationChannel.Create(providerId) with { DisplayOrder = 0 });
                return channels;
            }

            ProfileTranslationChannel current = channels[0];
            channels[0] = current with
            {
                InitialProviderId = providerId,
                DisplayLabel = string.IsNullOrWhiteSpace(current.DisplayLabel) ||
                    string.Equals(
                        current.DisplayLabel,
                        current.InitialProviderId,
                        StringComparison.OrdinalIgnoreCase)
                            ? providerId
                            : current.DisplayLabel,
                FallbackProviderIds = current.FallbackProviderIds
                    .Where(id => !string.Equals(id, providerId, StringComparison.Ordinal))
                    .ToList(),
                RefinementSteps = current.RefinementSteps
                    .Where(step => !string.Equals(
                        step.ProviderId,
                        providerId,
                        StringComparison.Ordinal))
                    .ToList(),
            };
            return channels;
        }

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
                List<ProfileTranslationChannel> channels = PrimaryChannels(preserved);
                return (preserved ?? ProfileRegion.Create(region.Name, bounds)) with
                {
                    RegionId = region.RegionId == Guid.Empty
                        ? preserved?.RegionId ?? Guid.NewGuid()
                        : region.RegionId,
                    Name = region.Name,
                    Bounds = bounds,
                    Priority = ProfilePresentationMapper.Priority(region.Priority),
                    ContextRole = ProfilePresentationMapper.ContextRole(region.ContextRole),
                    Ocr = preserved?.Ocr ?? new ProfileOcrSettings
                    {
                        RecognitionLanguage = edit.SourceLanguage,
                    },
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
                    Ocr = new ProfileOcrSettings
                    {
                        RecognitionLanguage = edit.SourceLanguage,
                    },
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
            string targetName = string.IsNullOrWhiteSpace(captureTarget.Name)
                ? edit.Name
                : captureTarget.Name;
            bool bindingChanged = existingTarget is not null &&
                (existingTarget.Kind != kind ||
                    !string.Equals(
                        CaptureTargetResolver.WantedName(existingTarget),
                        targetName,
                        StringComparison.OrdinalIgnoreCase));
            targets.Add((existingTarget ?? ProfileTarget.Create(captureTarget.Name, kind)) with
            {
                TargetId = captureTarget.TargetId == Guid.Empty
                    ? existingTarget?.TargetId ?? Guid.NewGuid()
                    : captureTarget.TargetId,
                Name = targetName,
                Kind = kind,
                MachineBinding = bindingChanged ? null : existingTarget?.MachineBinding,
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

    private ProfileCard ToCard(
        ProfileDocument document,
        IReadOnlyList<CaptureProbeTarget> liveTargets)
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

        // A profile with several targets can start only if every one of them resolves, so the card
        // reports the whole set rather than the first target it happens to list.
        ProfileTargetMatchState matchState = target is null
            ? ProfileTargetMatchState.NotConfigured
            : document.Targets.All(current => CaptureTargetResolver.Matches(current, liveTargets))
                ? ProfileTargetMatchState.Matched
                : ProfileTargetMatchState.Missing;
        CaptureProbeTarget? liveTarget = target is null
            ? null
            : CaptureTargetResolver.Find(target, liveTargets);

        return new ProfileCard(
            document.ProfileId,
            document.Name,
            target is null
                ? _text("ProfileTargetNone")
                : document.Targets.Count > 1
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        _text("ProfileTargetDescriptionMore"),
                        target.Name,
                        _text(TargetKindResourceKey(target.Kind)),
                        document.Targets.Count - 1)
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        _text("ProfileTargetDescription"),
                        target.Name,
                        _text(TargetKindResourceKey(target.Kind))),
            string.IsNullOrEmpty(resolution) ? "—" : resolution,
            string.Format(
                CultureInfo.CurrentCulture,
                _text("ProfileLanguagePair"),
                LanguageCatalog.DisplayNameFor(document.SourceLanguage, _text("UiLanguageTag")),
                LanguageCatalog.DisplayNameFor(document.TargetLanguage, _text("UiLanguageTag"))),
            regionCount,
            channelCount,
            matchState,
            _text(MatchStateResourceKey(matchState)),
            matchState == ProfileTargetMatchState.Matched
                ? StatusSeverity.Success
                : StatusSeverity.Warning,
            _text("ProfileActionStart"),
            IsPinned: false,
            TargetProbeId: liveTarget?.TargetId.Value ?? Guid.Empty,
            TargetNativeHandle: liveTarget?.NativeHandle ?? 0,
            TargetProbeKind: liveTarget?.Kind ?? string.Empty);
    }

    private static string TargetKindResourceKey(CaptureTargetKind kind) => kind switch
    {
        CaptureTargetKind.Window => "ProfileTargetKindWindow",
        CaptureTargetKind.Display => "ProfileTargetKindDisplay",
        CaptureTargetKind.DesktopFixedRegion => "ProfileTargetKindDesktopRegion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string MatchStateResourceKey(ProfileTargetMatchState state) => state switch
    {
        ProfileTargetMatchState.Matched => "ProfileMatchStateMatched",
        ProfileTargetMatchState.Missing => "ProfileMatchStateMissing",
        ProfileTargetMatchState.NotConfigured => "ProfileMatchStateNotConfigured",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

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
