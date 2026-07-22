using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Translation;

public enum TranslatorGroupSwitchScope
{
    AllRunning,
    ForegroundMatched,
    TargetSet,
}

public sealed record RunningTranslationTarget(
    TargetInstanceId TargetInstanceId,
    Guid ProfileId,
    bool IsForeground,
    IReadOnlyList<string> VisibleSourceText);

public sealed record TranslatorGroupSwitchResult(
    bool Applied,
    string StatusCode,
    IReadOnlyList<TargetInstanceId> AffectedTargets);

public sealed class TranslatorGroupSwitchService
{
    private readonly Func<TargetInstanceId, CancellationToken, ValueTask> _cancel;
    private readonly Func<TargetInstanceId, Guid, IReadOnlyList<string>, ValueTask> _retranslate;

    public TranslatorGroupSwitchService(
        Func<TargetInstanceId, CancellationToken, ValueTask> cancel,
        Func<TargetInstanceId, Guid, IReadOnlyList<string>, ValueTask> retranslate)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(retranslate);
        _cancel = cancel;
        _retranslate = retranslate;
    }

    public async ValueTask<TranslatorGroupSwitchResult> SwitchAsync(
        Guid translatorGroupId,
        TranslatorGroupSwitchScope scope,
        IReadOnlyList<RunningTranslationTarget> runningTargets,
        IReadOnlySet<TargetInstanceId>? targetSet,
        CancellationToken cancellationToken)
    {
        if (translatorGroupId == Guid.Empty)
            throw new ArgumentException("Translator group ID cannot be empty.", nameof(translatorGroupId));
        ArgumentNullException.ThrowIfNull(runningTargets);
        if (scope == TranslatorGroupSwitchScope.TargetSet && (targetSet is null || targetSet.Count == 0))
            throw new ArgumentException("TargetSet scope requires at least one explicit target.", nameof(targetSet));

        RunningTranslationTarget[] affected = runningTargets.Where(target => scope switch
        {
            TranslatorGroupSwitchScope.AllRunning => true,
            TranslatorGroupSwitchScope.ForegroundMatched => target.IsForeground,
            TranslatorGroupSwitchScope.TargetSet => targetSet!.Contains(target.TargetInstanceId),
            _ => false,
        }).ToArray();
        if (affected.Length == 0)
            return new(false, "translationGroup.noMatchingTarget", []);

        foreach (RunningTranslationTarget target in affected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _cancel(target.TargetInstanceId, cancellationToken).ConfigureAwait(false);
        }
        foreach (RunningTranslationTarget target in affected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _retranslate(
                target.TargetInstanceId,
                translatorGroupId,
                target.VisibleSourceText).ConfigureAwait(false);
        }
        return new(true, "translationGroup.applied", affected.Select(item => item.TargetInstanceId).ToArray());
    }
}
