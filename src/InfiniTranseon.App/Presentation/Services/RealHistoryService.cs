using InfiniTranseon.App.Controls;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;

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

    public RealHistoryService(AppDataOptions options, ProfileRepository profiles, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        _options = options;
        _profiles = profiles;
        _settings = settings;
    }

    public async Task<IReadOnlyList<HistoryEvent>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        ApplicationSettings settings = await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        HistoryOptions historyOptions = ToHistoryOptions(settings.HistoryRetention);
        if (!historyOptions.Enabled)
        {
            return [];
        }

        IReadOnlyList<ProfileDocument> profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        ProfileDocument? active = profiles.FirstOrDefault();
        if (active is null)
        {
            return [];
        }

        var repository = new HistoryRepository(_options.DatabasePath, historyOptions);
        HistoryPage page = await repository
            .ReadPageAsync(active.ProfileId, PageSize, cursor: null, cancellationToken)
            .ConfigureAwait(false);
        return page.Items.Select(ToEvent).ToArray();
    }

    private static HistoryOptions ToHistoryOptions(HistoryRetention retention) => retention switch
    {
        HistoryRetention.Days30 => new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(30)),
        HistoryRetention.Days90 => new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(90)),
        _ => new HistoryOptions(Enabled: false),
    };

    private static HistoryEvent ToEvent(HistoryRecord record)
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
            channels);
    }
}
