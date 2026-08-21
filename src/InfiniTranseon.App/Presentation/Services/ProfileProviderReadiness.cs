using InfiniTranseon.App.Presentation;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.App.Presentation.Services;

public sealed record ProviderReadinessStatus(
    string ProviderId,
    string DisplayName,
    bool IsReady);

/// <summary>
/// The single profile-level source for providers a runtime session will use. It deliberately covers
/// enabled translation channels and enabled cloud-OCR regions so preflight, start and hot apply
/// cannot disagree about which credentials are required.
/// </summary>
public static class ProfileProviderReadiness
{
    public static IReadOnlyList<string> GetRequiredProviderIds(ProfileDocument profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Targets
            .Where(target => target.Enabled)
            .SelectMany(target => target.Regions.Concat(target.RemainingAreaRegion is null
                ? []
                : [target.RemainingAreaRegion]))
            .Where(region => region.Enabled)
            .SelectMany(region =>
                (region.TranslationEnabled
                    ? region.TranslationChannels
                        .Where(channel => channel.Enabled &&
                            channel.TranslationGroupId == profile.ActiveTranslationGroupId)
                        .SelectMany(channel => new[] { channel.InitialProviderId }
                            .Concat(channel.FallbackProviderIds)
                            .Concat(channel.RefinementSteps.Select(step => step.ProviderId)))
                    : [])
                .Concat(region.Ocr.UseCloudOcr ? [region.Ocr.ProviderId] : []))
            .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<CatalogProvider> GetCatalog(
        CustomRestAdapterStore? customAdapters) => customAdapters is null
            ? ProviderCatalog.Default
            : ProviderCatalog.Default.Concat(customAdapters.GetCatalogProviders()).ToArray();

    public static async Task<IReadOnlyList<ProviderReadinessStatus>> EvaluateAsync(
        IReadOnlyList<string> providerIds,
        IReadOnlyList<CatalogProvider> catalog,
        ISecretReferenceService secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(secrets);

        var statuses = new List<ProviderReadinessStatus>(providerIds.Count);
        foreach (string providerId in providerIds)
        {
            CatalogProvider? provider = catalog.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.DisplayName, providerId, StringComparison.OrdinalIgnoreCase));
            bool isLocalTranslation = providerId.StartsWith(
                EngineRuntimeComposition.LocalTranslationProviderIdPrefix,
                StringComparison.Ordinal);
            bool isReady = provider is null
                ? isLocalTranslation
                : provider.IsSelectable &&
                    (!provider.RequiresCredential || await secrets
                        .HasSecretAsync(provider.Id, cancellationToken)
                        .ConfigureAwait(false));
            statuses.Add(new ProviderReadinessStatus(
                providerId,
                provider?.DisplayName ?? providerId,
                isReady));
        }

        return statuses;
    }
}
