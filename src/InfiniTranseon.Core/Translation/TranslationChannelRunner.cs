using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Translation;

public sealed record TranslationRunOptions(
    Guid ProfileId,
    TranslationContext Context,
    IReadOnlyList<GlossaryEntry> Glossary,
    TimeSpan AttemptTimeout,
    int MaximumOutputCharacters,
    int MaximumOutputTokens,
    bool StrictOffline,
    string BillingUnit = "characters",
    decimal? MaximumCostPerAttempt = null,
    string? Currency = null,
    string SourceLanguage = "auto",
    string TargetLanguage = "configured",
    string StyleVersion = "1",
    string PromptVersion = "1",
    string GlossaryVersion = "1",
    string ProfilePolicyVersion = "1",
    string? StylePrompt = null);

public sealed class TranslationChannelRunner
{
    private readonly OnlineProviderService _providers;
    private readonly ProviderDispatchCoordinator _dispatch;
    private readonly TranslationMemory? _memory;
    private readonly CorrectionStore? _corrections;
    private readonly IProviderRetryPolicy _retryPolicy;

    public TranslationChannelRunner(
        OnlineProviderService providers,
        ProviderDispatchCoordinator? dispatch = null,
        TranslationMemory? memory = null,
        CorrectionStore? corrections = null,
        IProviderRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
        _dispatch = dispatch ?? new ProviderDispatchCoordinator();
        _memory = memory;
        _corrections = corrections;
        _retryPolicy = retryPolicy ?? new ExponentialJitterRetryPolicy();
    }

    public async IAsyncEnumerable<TranslationOutput> RunAsync(
        TextGeneration source,
        TranslationChannelDefinition channel,
        TranslationRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate(channel, options);
        var channelToken = new ChannelExecutionToken(
            source.SourceToken,
            channel.Id,
            Guid.NewGuid(),
            channel.DisplaySlot.SlotId);
        TranslationContext context = ContextBuilder.ApplyPolicy(options.Context, channel.Context);
        if (_corrections is not null)
        {
            var correctionScope = new CorrectionScope(
                options.ProfileId,
                source.SourceToken.Area.UserRegionId?.Value,
                options.SourceLanguage,
                options.TargetLanguage,
                options.GlossaryVersion);
            TranslationCorrection? correction = await _corrections.FindAsync(
                correctionScope,
                source.SourceText,
                cancellationToken).ConfigureAwait(false);
            if (correction is not null)
            {
                Guid stageId = Guid.NewGuid();
                var execution = new StageExecutionToken(channelToken, stageId, 1, 1, 1);
                yield return new TranslationOutput(
                    channel.Id,
                    execution,
                    channel.DisplaySlot.SlotId,
                    stageId,
                    1,
                    1,
                    TranslationStage.Initial,
                    correction.Corrected,
                    "correction.manual",
                    TimeSpan.Zero,
                    0m,
                    null,
                    CacheHit: true,
                    StreamCompleted: true,
                    FallbackFromProviderId: null,
                    TerminalErrorCode: null,
                    SupersededReason: null)
                {
                    EstimateOnly = false,
                };
                yield break;
            }
        }
        string currentText = source.SourceText;
        int stageIndex = 0;
        string? fallbackFrom = null;

        string[] providerAttempts = [channel.InitialProviderId, .. channel.FallbackProviderIds];
        int maximumAttempts = Math.Min(_retryPolicy.MaximumAttempts, channel.RetryCount + 1);
        bool initialSucceeded = false;
        for (int providerIndex = 0; providerIndex < providerAttempts.Length; providerIndex++)
        {
            string providerId = providerAttempts[providerIndex];
            stageIndex++;
            Guid stageId = Guid.NewGuid();
            TranslationStage stage = providerIndex == 0 ? TranslationStage.Initial : TranslationStage.Fallback;
            AttemptResult attempt = new(false, false, currentText, null);
            for (int retry = 0; retry < maximumAttempts; retry++)
            {
                await foreach (TranslationOutput output in RunAttemptAsync(
                                   currentText,
                                   channelToken,
                                   stageId,
                                   stageIndex,
                                   retry + 1,
                                   stage,
                                   providerId,
                                   fallbackFrom,
                                   context,
                                   channel.Cache,
                                   options.PromptVersion,
                                   options,
                                   cancellationToken))
                {
                    attempt = new AttemptResult(
                        output.StreamCompleted && output.TerminalErrorCode is null,
                        output.TerminalErrorCode is not null && output.SupersededReason == "retryable",
                        output.Text,
                        output.TerminalErrorCode);
                    if (output.TerminalErrorCode is null ||
                        retry == maximumAttempts - 1 ||
                        output.SupersededReason != "retryable")
                        yield return output;
                }
                if (attempt.Succeeded || !attempt.Retryable) break;
                if (retry < maximumAttempts - 1)
                    await _retryPolicy.WaitBeforeRetryAsync(retry + 1, cancellationToken).ConfigureAwait(false);
            }
            if (attempt.Succeeded)
            {
                currentText = attempt.Text;
                initialSucceeded = true;
                break;
            }
            fallbackFrom = providerId;
        }

        if (!initialSucceeded) yield break;

        foreach (RefinementStepDefinition refinement in channel.RefinementSteps)
        {
            stageIndex++;
            await foreach (TranslationOutput output in RunAttemptAsync(
                               currentText,
                               channelToken,
                               refinement.StageId,
                               stageIndex,
                               1,
                               TranslationStage.Refinement,
                               refinement.ProviderId,
                               null,
                               context,
                               channel.Cache,
                               $"{options.PromptVersion}:{refinement.PromptTemplateId}",
                               options,
                               cancellationToken))
            {
                yield return output;
                if (output.StreamCompleted && output.TerminalErrorCode is null) currentText = output.Text;
            }
        }
    }

    private async IAsyncEnumerable<TranslationOutput> RunAttemptAsync(
        string sourceText,
        ChannelExecutionToken channelToken,
        Guid stageId,
        int stageIndex,
        int attempt,
        TranslationStage stage,
        string providerId,
        string? fallbackFrom,
        TranslationContext context,
        CachePolicy cachePolicy,
        string promptVersion,
        TranslationRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var execution = new StageExecutionToken(channelToken, stageId, stageIndex, attempt, 1);
        var stopwatch = Stopwatch.StartNew();
        TranslationCacheKey? cacheKey = CreateCacheKey(
            sourceText, providerId, context, cachePolicy, promptVersion, options);
        if (cacheKey is not null)
        {
            TranslationMemoryHit? hit = await _memory!.FindAsync(
                options.ProfileId,
                cacheKey,
                cancellationToken,
                cachePolicy.FuzzyEnabled,
                cachePolicy.PersistentEnabled).ConfigureAwait(false);
            if (hit is not null)
            {
                yield return CreateOutput(execution, hit.Translation, true, null, null, true);
                yield break;
            }
        }
        var request = new TranslationRequest(
            sourceText,
            stage == TranslationStage.Refinement
                ? options.TargetLanguage
                : options.SourceLanguage,
            options.TargetLanguage,
            context,
            options.Glossary,
            execution,
            options.AttemptTimeout,
            $"{channelToken.ChannelRunId:N}-{stageIndex}-{attempt}",
            options.MaximumOutputCharacters,
            options.MaximumOutputTokens,
            new ProviderCostReservation(
                options.BillingUnit,
                sourceText.Length,
                options.MaximumCostPerAttempt,
                options.Currency),
            options.StrictOffline,
            stage == TranslationStage.Refinement
                ? TranslationOperation.Refine
                : TranslationOperation.Translate,
            options.StylePrompt);
        ProviderDispatchCoordinator.ProviderDispatchLease? lease = null;
        string? dispatchError = null;
        try
        {
            lease = await _dispatch.AcquireAsync(
                options.ProfileId, providerId, request.CostReservation, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderDispatchRejectedException exception)
        {
            dispatchError = exception.Code;
        }
        if (dispatchError is not null)
        {
            yield return CreateOutput(execution, string.Empty, false, dispatchError, null, false);
            yield break;
        }
        await using (lease)
        await foreach (ProviderEvent providerEvent in _providers.StreamAsync(
                           providerId, request, cancellationToken).ConfigureAwait(false))
        {
            switch (providerEvent)
            {
                case ProviderSnapshot snapshot:
                    yield return CreateOutput(snapshot.Execution, snapshot.CumulativeText, false, null, null, false);
                    break;
                case ProviderCompleted completed:
                    if (options.MaximumCostPerAttempt is decimal reservedCost) lease!.Settle(reservedCost);
                    if (cacheKey is not null)
                    {
                        await _memory!.StoreAsync(
                            options.ProfileId,
                            cacheKey,
                            completed.FinalText,
                            cancellationToken,
                            cachePolicy.PersistentEnabled).ConfigureAwait(false);
                    }
                    yield return CreateOutput(completed.Execution, completed.FinalText, true, null, null, false);
                    break;
                case ProviderFailed failed:
                    yield return CreateOutput(
                        failed.Execution,
                        string.Empty,
                        false,
                        failed.ErrorCode,
                        failed.Retryable ? "retryable" : null,
                        false);
                    break;
                case ProviderCancelled cancelled:
                    yield return CreateOutput(
                        cancelled.Execution,
                        string.Empty,
                        false,
                        cancelled.ReasonCode,
                        "cancelled",
                        false);
                    break;
            }
        }

        TranslationOutput CreateOutput(
            StageExecutionToken token,
            string text,
            bool completed,
            string? error,
            string? superseded,
            bool cacheHit) => new TranslationOutput(
                channelToken.ChannelId,
                token,
                channelToken.ImmutableSlotId,
                stageId,
                stageIndex,
                attempt,
                stage,
                text,
                providerId,
                stopwatch.Elapsed,
                completed ? cacheHit ? 0m : options.MaximumCostPerAttempt : null,
                completed && !cacheHit ? options.Currency : null,
                cacheHit,
                completed,
                fallbackFrom,
                error,
                superseded)
            {
                EstimateOnly = !cacheHit &&
                    (options.MaximumCostPerAttempt is null || options.Currency is null),
            };
    }

    private TranslationCacheKey? CreateCacheKey(
        string sourceText,
        string providerId,
        TranslationContext context,
        CachePolicy policy,
        string promptVersion,
        TranslationRunOptions options)
    {
        if (_memory is null || !policy.MemoryEnabled ||
            !_providers.TryGetDescriptor(providerId, out ProviderDescriptor? provider) || provider is null)
            return null;
        string? relevantContext = provider.SupportsContext ? JsonSerializer.Serialize(context) : null;
        return TranslationCacheKey.Create(
            providerId,
            provider.ModelId,
            options.SourceLanguage,
            options.TargetLanguage,
            sourceText,
            options.StyleVersion,
            promptVersion,
            options.GlossaryVersion,
            options.ProfilePolicyVersion,
            relevantContext);
    }

    private static void Validate(TranslationChannelDefinition channel, TranslationRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel.InitialProviderId);
        if (channel.RetryCount is < 0 or > 1)
            throw new ArgumentException("A translation channel supports at most one retry.", nameof(channel));
        if (channel.FallbackProviderIds.Count > 2)
            throw new ArgumentException("A translation channel supports at most two fallback providers.", nameof(channel));
        if (channel.RefinementSteps.Count > 2)
            throw new ArgumentException("A translation channel supports at most two refinements.", nameof(channel));
        if (channel.RefinementSteps.Select(item => item.StageId).Distinct().Count() !=
            channel.RefinementSteps.Count)
            throw new ArgumentException("Refinement stage IDs must be unique.", nameof(channel));
        if (options.ProfileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(options));
        foreach (string version in new[]
        {
            options.StyleVersion,
            options.PromptVersion,
            options.GlossaryVersion,
            options.ProfilePolicyVersion,
        }) ArgumentException.ThrowIfNullOrWhiteSpace(version);
    }

    private sealed record AttemptResult(bool Succeeded, bool Retryable, string Text, string? ErrorCode);
}
