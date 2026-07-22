using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Storage;

public sealed class RuntimeTranslationRecordSink :
    IRuntimeTranslationRecordSink,
    IRuntimeTranslationSessionSink
{
    private readonly RecentTranslationBuffer _recent;
    private readonly HistoryRepository _history;

    public RuntimeTranslationRecordSink(
        RecentTranslationBuffer recent,
        HistoryRepository history)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(history);
        _recent = recent;
        _history = history;
    }

    public async ValueTask SaveAsync(
        Guid profileId,
        TextGeneration source,
        IReadOnlyList<TranslationOutput> outputs,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outputs);
        TranslationOutput[] terminal = outputs
            .Where(output => output.StreamCompleted || output.TerminalErrorCode is not null)
            .ToArray();
        string[] latestTranslations = terminal
            .Where(output => output.StreamCompleted && output.TerminalErrorCode is null)
            .GroupBy(output => output.ChannelId)
            .Select(group => group.OrderByDescending(output => output.StageIndex)
                .ThenByDescending(output => output.Attempt)
                .ThenByDescending(output => output.ExecutionToken.StreamSequence)
                .First().Text)
            .Where(text => text.Length > 0)
            .ToArray();
        _recent.Add(profileId, new RecentTranslationEntry(
            source.SourceEventId.Value,
            source.SourceText,
            latestTranslations,
            source.CapturedAtUtc));
        var history = new HistoryRecord(
            profileId,
            source.SourceEventId.Value,
            source.CapturedAtUtc,
            source.SourceText,
            terminal.Select(output => new HistoryTranslationResult(
                output.ChannelId.Value,
                output.ProviderId,
                output.Text,
                output.Stage.ToString(),
                output.Latency.TotalMilliseconds,
                output.EstimatedCost,
                output.CostCurrency,
                output.CacheHit,
                output.TerminalErrorCode)).ToArray());
        await _history.SaveAsync(history, cancellationToken).ConfigureAwait(false);
    }

    public void ProfileStopped(Guid profileId) => _recent.Clear(profileId);
}
