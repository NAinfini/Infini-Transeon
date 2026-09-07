using InfiniTranseon.App.Controls;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Real history service backed by the Core <see cref="HistoryRepository"/>. The application setting is
/// the global privacy switch; each profile must also opt in and owns its own time and byte limits.
/// </summary>
public sealed class RealHistoryService : IHistoryService
{
    private const int PageSize = 100;
    private readonly AppDataOptions _options;
    private readonly ProfileRepository _profiles;
    private readonly ISettingsService _settings;
    private readonly IRuntimeControlService _runtime;
    private Guid? _selectedProfileId;

    public RealHistoryService(
        AppDataOptions options,
        ProfileRepository profiles,
        ISettingsService settings,
        IRuntimeControlService runtime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);
        _options = options;
        _profiles = profiles;
        _settings = settings;
        _runtime = runtime;
    }

    public void SelectProfile(Guid? profileId)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        _selectedProfileId = profileId;
    }

    public async Task<ProfileHistoryConfiguration?> GetProfileConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        if (_selectedProfileId is not Guid profileId) return null;
        ProfileDocument? profile = await _profiles
            .LoadAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        return profile is null
            ? null
            : new ProfileHistoryConfiguration(
                profile.ProfileId,
                profile.History.Enabled,
                profile.History.MaxAgeDays,
                profile.History.MaxBytes);
    }

    public async Task UpdateProfileConfigurationAsync(
        ProfileHistoryConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_selectedProfileId is not Guid selected || selected != configuration.ProfileId)
            throw new InvalidOperationException("History configuration does not match the selected profile.");
        if (configuration.MaxAgeDays is < 1 or > 365_000 ||
            configuration.MaxBytes is < 1024 or > 1_099_511_627_776)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "History retention must use a positive bounded age and 1 KiB to 1 TiB.");
        }

        ProfileDocument profile = await _profiles
            .LoadAsync(selected, cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException("The selected history profile no longer exists.");
        ProfileDocument updated = profile with
        {
            History = new ProfileHistorySettings
            {
                Enabled = configuration.Enabled,
                MaxAgeDays = configuration.MaxAgeDays,
                MaxBytes = configuration.MaxBytes,
            },
        };
        await _profiles.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await _runtime.ApplyProfileAsync(updated.ProfileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryEvent>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        ApplicationSettings settings = await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.HistoryRetention == HistoryRetention.Off)
        {
            return [];
        }

        IReadOnlyList<ProfileDocument> profiles = _selectedProfileId is Guid selected
            ? await LoadSelectedAsync(selected, cancellationToken).ConfigureAwait(false)
            : await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        if (profiles.Count == 0)
        {
            return [];
        }

        var events = new List<HistoryEvent>();
        foreach (ProfileDocument profile in profiles.Where(profile => profile.History.Enabled))
        {
            var repository = new HistoryRepository(
                _options.DatabasePath,
                CreateHistoryOptions(profile, settings.HistoryRetention));
            HistoryPage page = await repository
                .ReadPageAsync(profile.ProfileId, PageSize, cursor: null, cancellationToken)
                .ConfigureAwait(false);
            events.AddRange(page.Items.Select(record => ToEvent(record, profile.Name)));
        }
        return events
            .OrderByDescending(item => item.CapturedAtUtc)
            .Take(PageSize)
            .ToArray();
    }

    public async Task SaveCorrectionAsync(
        HistoryEvent historyEvent,
        string correctedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(historyEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(correctedText);
        if (historyEvent.ProfileId == Guid.Empty)
            throw new ArgumentException("History event does not identify a profile.", nameof(historyEvent));
        ProfileDocument profile = await _profiles
            .LoadAsync(historyEvent.ProfileId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException("The history event profile no longer exists.");
        var corrections = new CorrectionStore(_options.DatabasePath);
        await corrections.AddAsync(
            new CorrectionScope(
                profile.ProfileId,
                RegionId: null,
                profile.SourceLanguage,
                profile.TargetLanguage,
                GlossaryProcessor.ComputeVersion(ProfileDocumentData.ReadTranslationGlossary(profile))),
            historyEvent.SourceText,
            correctedText.Trim(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ProfileDocument>> LoadSelectedAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        ProfileDocument? profile = await _profiles
            .LoadAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        return profile is null ? [] : [profile];
    }

    internal static HistoryOptions CreateHistoryOptions(
        ProfileDocument profile,
        HistoryRetention retention)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return retention == HistoryRetention.Off || !profile.History.Enabled
            ? new HistoryOptions(Enabled: false)
            : new HistoryOptions(
                Enabled: true,
                Retention: TimeSpan.FromDays(Math.Min(
                    profile.History.MaxAgeDays,
                    retention == HistoryRetention.Days90 ? 90 : 30)),
                MaximumBytes: profile.History.MaxBytes);
    }

    private static HistoryEvent ToEvent(HistoryRecord record, string profileName)
    {
        var channels = record.Results
            .Select((result, index) => new ChannelResult(
                $"Channel {index + 1}",
                result.ProviderId,
                result.Text,
                result.ErrorCode is null ? "Success" : "Failed",
                result.ErrorCode is null ? StatusSeverity.Success : StatusSeverity.Critical,
                $"{result.LatencyMilliseconds:0} ms"))
            .ToArray();
        return new HistoryEvent(
            record.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
            record.SourceText,
            record.RegionName,
            channels,
            record.ProfileId,
            record.SourceEventId,
            profileName)
        {
            CapturedAtUtc = record.CapturedAtUtc,
        };
    }
}
