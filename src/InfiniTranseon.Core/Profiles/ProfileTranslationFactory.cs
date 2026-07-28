using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Profiles;

public static class ProfileTranslationFactory
{
    public static IReadOnlyList<TranslationChannelDefinition> CreateChannels(
        ProfileDocument profile,
        ProfileRegion region)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(region);
        ProfileTranslationChannel[] enabled = region.TranslationChannels
            .Where(channel => channel.Enabled &&
                channel.TranslationGroupId == profile.ActiveTranslationGroupId)
            .OrderBy(channel => channel.DisplayOrder)
            .ToArray();
        if (enabled.Length is < 1 or > 4)
            throw new ArgumentException(
                "A translated region requires one to four enabled channels.", nameof(region));
        if (enabled.Select(channel => channel.ChannelId).Distinct().Count() != enabled.Length ||
            enabled.Select(channel => channel.DisplayOrder).Distinct().Count() != enabled.Length)
            throw new ArgumentException(
                "Enabled channels require unique identities and display orders.", nameof(region));

        return Array.AsReadOnly(enabled.Select(channel =>
            new TranslationChannelDefinition(
                new TranslationChannelId(channel.ChannelId),
                channel.InitialProviderId,
                channel.FallbackProviderIds.ToArray(),
                channel.RefinementSteps.Select(step => new RefinementStepDefinition(
                    step.StageId,
                    step.ProviderId,
                    step.PromptTemplateId)).ToArray(),
                new ContextPolicy(
                    channel.IncludeGameContext,
                    IncludeScene: true,
                    channel.IncludeRecentContext),
                new CachePolicy(
                    channel.MemoryCacheEnabled,
                    channel.PersistentCacheEnabled,
                    FuzzyEnabled: false),
                new DisplaySlotDefinition(
                    channel.ChannelId,
                    channel.DisplayOrder,
                    channel.DisplayLabel))
            {
                RetryCount = channel.RetryCount,
                AttemptTimeout = TimeSpan.FromMilliseconds(channel.AttemptTimeoutMilliseconds),
            }).ToArray());
    }

    // Compatibility seam for callers that have not yet adopted grouped profiles. It is deliberately
    // limited to a synthetic single group and should not be used by the runtime.
    public static IReadOnlyList<TranslationChannelDefinition> CreateChannels(ProfileRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        Guid groupId = region.TranslationChannels
            .Select(channel => channel.TranslationGroupId)
            .FirstOrDefault(id => id != Guid.Empty);
        return CreateChannels(new ProfileDocument
        {
            TranslationGroups = [new ProfileTranslationGroup { TranslationGroupId = groupId }],
            ActiveTranslationGroupId = groupId,
        }, region);
    }

    public static TranslationRunOptions CreateRunOptions(
        ProfileDocument profile,
        string? scene,
        string? speaker,
        IReadOnlyList<string> recentSource,
        IReadOnlyList<string> recentTranslation,
        TimeSpan attemptTimeout,
        int maximumOutputCharacters,
        int maximumOutputTokens,
        IReadOnlyList<GlossaryEntry>? glossary = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(recentSource);
        ArgumentNullException.ThrowIfNull(recentTranslation);
        if (profile.Context.RecentLineCount < 0)
            throw new ArgumentOutOfRangeException(nameof(profile));
        if (attemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputTokens, 1);

        int count = profile.Context.RecentLineCount;
        return new TranslationRunOptions(
            profile.ProfileId,
            new TranslationContext(
                EmptyToNull(profile.Context.GameName),
                EmptyToNull(profile.Context.GameDescription),
                EmptyToNull(scene),
                EmptyToNull(speaker),
                recentSource.TakeLast(count).ToArray(),
                recentTranslation.TakeLast(count).ToArray()),
            glossary?.ToArray() ?? [],
            attemptTimeout,
            maximumOutputCharacters,
            maximumOutputTokens,
            profile.StrictOffline,
            SourceLanguage: profile.SourceLanguage,
            TargetLanguage: profile.TargetLanguage,
            StyleVersion: profile.StylePrompt.ActiveVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            PromptVersion: profile.StylePrompt.ActiveVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            StylePrompt: profile.StylePrompt.Active?.Template);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
