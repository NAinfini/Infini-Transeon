using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Probes;
using InfiniTranseon.Core.Translation;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Routes the wizard's and settings pages' "test translation" to the provider the user actually
/// selected, instead of one provider chosen at composition time. Every credential the selected
/// provider declares is checked before the call, so a partially configured multi-credential
/// provider (Yandex, Baidu, Youdao, Alibaba) reports the missing credential rather than an opaque
/// HTTP error from the provider.
/// </summary>
public sealed class CatalogTranslationProbe : ITranslationProbe
{
    /// <summary>No provider was supplied by the caller. The UI must not offer the test at all in
    /// that state; surfacing a code is better than testing an arbitrary provider.</summary>
    public const string ProviderNotSelectedCode = "translation.probe.providerNotSelected";

    /// <summary>The supplied id matches no built-in or imported provider.</summary>
    public const string ProviderUnknownCode = "translation.probe.providerUnknown";

    private readonly IBoundCredentialStore _credentials;
    private readonly CustomRestAdapterStore _customAdapters;

    public CatalogTranslationProbe(
        IBoundCredentialStore credentials,
        CustomRestAdapterStore customAdapters)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(customAdapters);
        _credentials = credentials;
        _customAdapters = customAdapters;
    }

    public async ValueTask<TranslationProbeResult> TranslateAsync(
        TranslationProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProviderId))
        {
            return new TranslationProbeResult(
                string.Empty, string.Empty, TimeSpan.Zero, ProviderNotSelectedCode);
        }

        IReadOnlyList<DeclarativeRestAdapterDefinition> customDefinitions = _customAdapters.Load();
        CatalogProvider? provider = ProviderCatalog.Default
            .Concat(_customAdapters.GetCatalogProviders())
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id, request.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    candidate.DisplayName, request.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null || provider.Capability != CatalogProviderCapability.Translation)
        {
            return new TranslationProbeResult(
                request.ProviderId, string.Empty, TimeSpan.Zero, ProviderUnknownCode);
        }

        foreach (CatalogCredential credential in provider.Credentials)
        {
            string? secret;
            try
            {
                secret = await _credentials
                    .ReadAsync(credential.Reference, credential.Binding, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CredentialBindingException)
            {
                return new TranslationProbeResult(
                    provider.Id, string.Empty, TimeSpan.Zero, TranslationProbe.CredentialRebindCode);
            }
            if (string.IsNullOrEmpty(secret))
            {
                return new TranslationProbeResult(
                    provider.Id, string.Empty, TimeSpan.Zero, TranslationProbe.CredentialMissingCode);
            }
        }

        // The credentials above are already verified, so the inner probe runs registry-only: it
        // supports a single credential binding and would silently skip the extra ones.
        ProviderRegistry registry =
            EngineRuntimeComposition.BuildProviderRegistry(_credentials, customDefinitions);
        return await new TranslationProbe(registry, provider.Id)
            .TranslateAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
