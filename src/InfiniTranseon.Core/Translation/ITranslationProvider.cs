using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation;

public interface ITranslationProvider
{
    IAsyncEnumerable<ProviderWireEvent> StreamAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
