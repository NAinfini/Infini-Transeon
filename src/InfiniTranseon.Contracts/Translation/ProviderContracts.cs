using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Contracts.Translation;

public enum ProviderKind
{
    Translation,
    LargeLanguageModel,
    CloudOcr,
}

public sealed record TranslationContext(
    string? GameName,
    string? GameDescription,
    string? Scene,
    string? Speaker,
    IReadOnlyList<string> RecentSource,
    IReadOnlyList<string> RecentTranslation);

public sealed record GlossaryEntry(string Source, string Target);

public sealed record ProviderCostReservation(
    string BillingUnit,
    long MaximumUnits,
    decimal? MaximumCost,
    string? Currency);

public enum TranslationOperation
{
    Translate,
    Refine,
}

public sealed record TranslationRequest(
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    TranslationContext Context,
    IReadOnlyList<GlossaryEntry> Glossary,
    StageExecutionToken Execution,
    TimeSpan Timeout,
    string IdempotencyKey,
    int MaximumOutputCharacters,
    int MaximumOutputTokens,
    ProviderCostReservation CostReservation,
    bool StrictOffline,
    TranslationOperation Operation = TranslationOperation.Translate,
    string? StylePrompt = null);

public sealed record ProviderUsage(long InputUnits, long OutputUnits, string BillingUnit)
{
    public static ProviderUsage None { get; } = new(0, 0, "none");
}

public abstract record ProviderWireEvent;
public sealed record ProviderDelta(long ProviderDeltaSequence, string Text) : ProviderWireEvent;
public sealed record ProviderDone(long LastProviderDeltaSequence, ProviderUsage Usage) : ProviderWireEvent;
public sealed record ProviderWireFailure(string ErrorCode, bool Retryable) : ProviderWireEvent;
public sealed record ProviderWireCancelled(string ReasonCode) : ProviderWireEvent;

public abstract record ProviderEvent(StageExecutionToken Execution);
public sealed record ProviderSnapshot(
    StageExecutionToken Execution,
    long LastProviderDeltaSequence,
    string CumulativeText) : ProviderEvent(Execution);
public sealed record ProviderCompleted(
    StageExecutionToken Execution,
    long LastProviderDeltaSequence,
    string FinalText,
    ProviderUsage Usage) : ProviderEvent(Execution);
public sealed record ProviderFailed(
    StageExecutionToken Execution,
    string ErrorCode,
    bool Retryable) : ProviderEvent(Execution);
public sealed record ProviderCancelled(
    StageExecutionToken Execution,
    string ReasonCode) : ProviderEvent(Execution);
