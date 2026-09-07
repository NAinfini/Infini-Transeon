using System.Diagnostics;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Probes;

/// <summary>
/// Performs a one-shot translation through a configured provider from the
/// <see cref="ProviderRegistry"/>. When the provider is not registered, or a required
/// credential is absent, the probe returns an explicit error code rather than silently
/// succeeding, so configuration gaps are visible in the UI.
/// </summary>
public sealed class TranslationProbe : ITranslationProbe
{
    public const string ProviderNotRegisteredCode = "translation.probe.providerNotRegistered";
    public const string CredentialMissingCode = "translation.probe.credentialMissing";
    public const string CredentialRebindCode = "translation.probe.credentialRebindRequired";
    public const string NoOutputCode = "translation.probe.noOutput";

    private readonly ProviderRegistry _registry;
    private readonly string _providerId;
    private readonly TimeSpan _timeout;
    private readonly IBoundCredentialStore? _credentialStore;
    private readonly CredentialBinding? _credentialBinding;
    private readonly string? _credentialReference;

    public TranslationProbe(
        ProviderRegistry registry,
        string providerId,
        TimeSpan? timeout = null,
        IBoundCredentialStore? credentialStore = null,
        CredentialBinding? credentialBinding = null,
        string? credentialReference = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        bool credentialRequested = credentialStore is not null ||
            credentialBinding is not null || credentialReference is not null;
        if (credentialRequested &&
            (credentialStore is null || credentialBinding is null ||
                string.IsNullOrWhiteSpace(credentialReference)))
        {
            throw new ArgumentException(
                "A credential check requires the store, binding and reference together.",
                nameof(credentialStore));
        }
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _registry = registry;
        _providerId = providerId;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _credentialStore = credentialStore;
        _credentialBinding = credentialBinding;
        _credentialReference = credentialReference;
    }

    public async ValueTask<TranslationProbeResult> TranslateAsync(
        TranslationProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        long start = Stopwatch.GetTimestamp();

        if (_credentialStore is not null)
        {
            string? secret;
            try
            {
                secret = await _credentialStore.ReadAsync(
                    _credentialReference!, _credentialBinding!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CredentialBindingException)
            {
                return Failure(request, CredentialRebindCode, start);
            }
            if (string.IsNullOrEmpty(secret))
            {
                return Failure(request, CredentialMissingCode, start);
            }
        }

        if (!_registry.TryGet(_providerId, out ProviderRegistration? registration) ||
            registration is null)
        {
            return Failure(request, ProviderNotRegisteredCode, start);
        }

        TranslationRequest translationRequest = BuildRequest(request);
        using var providers = new OnlineProviderService(
            _registry,
            new ProviderServiceLimits(MaximumConcurrentCalls: 1));
        await foreach (ProviderEvent providerEvent in providers.StreamAsync(
                           _providerId, translationRequest, cancellationToken).ConfigureAwait(false))
        {
            switch (providerEvent)
            {
                case ProviderCompleted completed:
                    return new TranslationProbeResult(
                        _providerId, completed.FinalText, Stopwatch.GetElapsedTime(start), null);
                case ProviderFailed failure:
                    return Failure(
                        request,
                        failure.ErrorCode is "provider.missingTerminal" or "provider.emptyOutput"
                            ? NoOutputCode
                            : failure.ErrorCode,
                        start);
                case ProviderCancelled cancelled:
                    return Failure(request, cancelled.ReasonCode, start);
            }
        }

        return Failure(request, NoOutputCode, start);
    }

    private TranslationProbeResult Failure(
        TranslationProbeRequest request, string errorCode, long start) =>
        new(_providerId, string.Empty, Stopwatch.GetElapsedTime(start), errorCode);

    private TranslationRequest BuildRequest(TranslationProbeRequest request)
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.FullTarget,
            new TextTrackId(Guid.NewGuid()),
            1,
            1);
        var channel = new ChannelExecutionToken(
            source,
            new TranslationChannelId(Guid.NewGuid()),
            Guid.NewGuid(),
            Guid.NewGuid());
        var execution = new StageExecutionToken(channel, Guid.NewGuid(), 1, 1, 1);
        var context = new TranslationContext(
            null, null, request.Context, null, [], []);
        return new TranslationRequest(
            request.SourceText,
            request.SourceLanguage,
            request.TargetLanguage,
            context,
            [],
            execution,
            _timeout,
            Guid.NewGuid().ToString("n"),
            MaximumOutputCharacters: 8_192,
            MaximumOutputTokens: 4_096,
            new ProviderCostReservation("characters", 100_000, null, null),
            StrictOffline: false);
    }
}
