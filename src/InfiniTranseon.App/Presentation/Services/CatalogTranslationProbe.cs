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

    /// <summary>The selected provider requires the network, which the current global policy blocks.</summary>
    public const string StrictOfflineCode = "translation.probe.strictOffline";

    private readonly IBoundCredentialStore _credentials;
    private readonly CustomRestAdapterStore _customAdapters;
    private readonly LocalModelManagementService _localModels;
    private readonly ISettingsService _settings;
    private readonly AppDataOptions _appData;

    public CatalogTranslationProbe(
        IBoundCredentialStore credentials,
        CustomRestAdapterStore customAdapters,
        LocalModelManagementService localModels,
        ISettingsService settings,
        AppDataOptions appData)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(customAdapters);
        ArgumentNullException.ThrowIfNull(localModels);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appData);
        _credentials = credentials;
        _customAdapters = customAdapters;
        _localModels = localModels;
        _settings = settings;
        _appData = appData;
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
        if (request.ProviderId.StartsWith(
                EngineRuntimeComposition.LocalTranslationProviderIdPrefix,
                StringComparison.Ordinal))
        {
            return await TranslateLocallyAsync(
                    request,
                    request.ProviderId,
                    customDefinitions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

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

        ApplicationSettings settings =
            await _settings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.StrictOffline)
        {
            return new TranslationProbeResult(
                provider.Id, string.Empty, TimeSpan.Zero, StrictOfflineCode);
        }

        foreach (CatalogCredential credential in provider.Credentials)
        {
            string? secret;
            try
            {
                secret = await _credentials
                    .ReadAsync(
                        credential.Reference,
                        credential.ResolveBinding(settings.EffectiveProviderEndpoints),
                        cancellationToken)
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
            EngineRuntimeComposition.BuildProviderRegistry(
                _credentials,
                customDefinitions,
                providerEndpoints: settings.EffectiveProviderEndpoints,
                providerModels: settings.EffectiveProviderModels);
        return await new TranslationProbe(registry, provider.Id)
            .TranslateAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the test through a model installed on this machine. The worker starts for this call and
    /// is shut down again afterwards: the test must not leave a multi-gigabyte model resident
    /// because someone pressed a button once.
    /// </summary>
    private async ValueTask<TranslationProbeResult> TranslateLocallyAsync(
        TranslationProbeRequest request,
        string providerId,
        IReadOnlyList<DeclarativeRestAdapterDefinition> customDefinitions,
        CancellationToken cancellationToken)
    {
        using LocalTranslationProviderLease? lease =
            EngineRuntimeComposition.CreateLocalTranslationProvider(
                providerId,
                _localModels,
                _appData);
        if (lease is null)
        {
            return new TranslationProbeResult(
                providerId, string.Empty, TimeSpan.Zero, ProviderUnknownCode);
        }

        ProviderRegistry registry = EngineRuntimeComposition.BuildProviderRegistry(
            _credentials,
            customDefinitions,
            [lease.Registration]);
        return await new TranslationProbe(registry, providerId)
            .TranslateAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
