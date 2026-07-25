using InfiniTranseon.App.Controls;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Real history service backed by the Core <see cref="HistoryRepository"/>. History is privacy-off by
/// default; the retention setting decides whether the repository is enabled. When retention is Off, or
/// no profile exists, or the runtime has not yet recorded anything, the result is empty by design.
/// </summary>
public sealed class RealHistoryService : IHistoryService
{
    private const int PageSize = 100;
    private readonly AppDataOptions _options;
    private readonly ProfileRepository _profiles;
    private readonly ISettingsService _settings;
    private Guid? _selectedProfileId;

    public RealHistoryService(AppDataOptions options, ProfileRepository profiles, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        _options = options;
        _profiles = profiles;
        _settings = settings;
    }

    public void SelectProfile(Guid? profileId)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        _selectedProfileId = profileId;
    }

    public async Task<IReadOnlyList<HistoryEvent>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        ApplicationSettings settings = await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        HistoryOptions historyOptions = ToHistoryOptions(settings.HistoryRetention);
        if (!historyOptions.Enabled)
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

        var repository = new HistoryRepository(_options.DatabasePath, historyOptions);
        var events = new List<HistoryEvent>();
        foreach (ProfileDocument profile in profiles)
        {
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
                GlossaryVersion: "1"),
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

    private static HistoryOptions ToHistoryOptions(HistoryRetention retention) => retention switch
    {
        HistoryRetention.Days30 => new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(30)),
        HistoryRetention.Days90 => new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(90)),
        _ => new HistoryOptions(Enabled: false),
    };

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
            "—",
            channels,
            record.ProfileId,
            record.SourceEventId,
            profileName)
        {
            CapturedAtUtc = record.CapturedAtUtc,
        };
    }
}
