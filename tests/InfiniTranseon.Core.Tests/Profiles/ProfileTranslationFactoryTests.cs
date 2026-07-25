using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Tests.Profiles;

public sealed class ProfileTranslationFactoryTests
{
    [Fact]
    public void CreatesStableOrderedChannelsFromEnabledProfileEntries()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        var region = ProfileRegion.Create("Dialogue", new NormalizedRect(0, 0, 1, 1)) with
        {
            TranslationChannels =
            [
                new ProfileTranslationChannel
                {
                    ChannelId = secondId,
                    InitialProviderId = "second",
                    DisplayLabel = "Second",
                    DisplayOrder = 2,
                    RetryCount = 0,
                    IncludeGameContext = false,
                    PersistentCacheEnabled = true,
                },
                new ProfileTranslationChannel
                {
                    ChannelId = firstId,
                    InitialProviderId = "first",
                    DisplayLabel = "First",
                    DisplayOrder = 1,
                    FallbackProviderIds = ["fallback"],
                    RefinementSteps =
                    [
                        new ProfileRefinementStep
                        {
                            StageId = Guid.NewGuid(),
                            ProviderId = "refiner",
                            PromptTemplateId = "style",
                        },
                    ],
                },
                new ProfileTranslationChannel
                {
                    Enabled = false,
                    InitialProviderId = "disabled",
                    DisplayLabel = "Disabled",
                    DisplayOrder = 0,
                },
            ],
        };

        IReadOnlyList<TranslationChannelDefinition> channels =
            ProfileTranslationFactory.CreateChannels(region);

        Assert.Equal([firstId, secondId], channels.Select(item => item.Id.Value));
        Assert.Equal([1, 2], channels.Select(item => item.DisplaySlot.Order));
        Assert.Equal(firstId, channels[0].DisplaySlot.SlotId);
        Assert.Single(channels[0].FallbackProviderIds);
        Assert.Single(channels[0].RefinementSteps);
        Assert.Equal(0, channels[1].RetryCount);
        Assert.False(channels[1].Context.IncludeGame);
        Assert.True(channels[1].Cache.PersistentEnabled);
    }

    [Fact]
    public void CreatesBoundedContextAndCarriesProfilePrivacyPolicy()
    {
        Guid profileId = Guid.NewGuid();
        ProfileDocument profile = ProfileDocument.Create("Game", "ja", "zh-Hans") with
        {
            ProfileId = profileId,
            StrictOffline = true,
            Context = new ProfileContextSettings
            {
                GameName = "Game Name",
                GameDescription = "Game Description",
                RecentLineCount = 2,
            },
            StylePrompt = new ProfileStylePromptSettings
            {
                ActiveVersion = 3,
                Versions =
                [
                    new ProfileStylePromptVersion
                    {
                        Version = 3,
                        Name = "Concise dialogue",
                        Template = "Keep dialogue brief and natural.",
                    },
                ],
            },
        };

        var options = ProfileTranslationFactory.CreateRunOptions(
            profile,
            scene: "Chapter 1",
            speaker: "Alice",
            recentSource: ["one", "two", "three"],
            recentTranslation: ["一", "二", "三"],
            attemptTimeout: TimeSpan.FromSeconds(3),
            maximumOutputCharacters: 1000,
            maximumOutputTokens: 500);

        Assert.Equal(profileId, options.ProfileId);
        Assert.True(options.StrictOffline);
        Assert.Equal("ja", options.SourceLanguage);
        Assert.Equal("zh-Hans", options.TargetLanguage);
        Assert.Equal(["two", "three"], options.Context.RecentSource);
        Assert.Equal(["二", "三"], options.Context.RecentTranslation);
        Assert.Equal("Alice", options.Context.Speaker);
        Assert.Equal("3", options.PromptVersion);
        Assert.Equal("Keep dialogue brief and natural.", options.StylePrompt);
    }
}
