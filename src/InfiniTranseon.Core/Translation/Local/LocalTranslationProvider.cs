using System.Runtime.CompilerServices;
using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation.Local;

public sealed class LocalTranslationProvider : ITranslationProvider
{
    private readonly string _providerId;
    private readonly string _modelId;
    private readonly LocalWorkerSessionManager _sessions;

    public LocalTranslationProvider(
        string providerId,
        string modelId,
        LocalWorkerSessionManager sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(sessions);
        _providerId = providerId;
        _modelId = modelId;
        _sessions = sessions;
    }

    public ProviderDescriptor Descriptor => new(
        _providerId,
        ProviderKind.Translation,
        RequiresNetwork: false,
        SupportsStreaming: false,
        SupportsContext: false,
        SupportsGlossary: false)
    {
        ModelId = _modelId,
    };

    public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LocalTranslationResponse? response = null;
        ProviderWireEvent? failure = null;
        try
        {
            response = await _sessions.TranslateAsync(
                _modelId,
                request.SourceLanguage,
                request.TargetLanguage,
                request.SourceText,
                request.MaximumOutputCharacters,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new ProviderWireCancelled("provider.cancelled");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            failure = new ProviderWireFailure("local.workerUnavailable", true);
        }
        if (failure is not null)
        {
            yield return failure;
            yield break;
        }
        if (response is null)
        {
            yield return new ProviderWireFailure("local.invalidResponse", false);
            yield break;
        }
        if (!response.Success)
        {
            yield return new ProviderWireFailure(response.ErrorCode ?? "local.unknownFailure", false);
            yield break;
        }
        if (string.IsNullOrEmpty(response.Text) || response.Text.Length > request.MaximumOutputCharacters)
        {
            yield return new ProviderWireFailure("local.invalidResponse", false);
            yield break;
        }
        yield return new ProviderDelta(1, response.Text);
        yield return new ProviderDone(1, new ProviderUsage(
            request.SourceText.Length, response.Text.Length, "characters"));
    }
}
