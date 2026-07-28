using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Tests.Profiles;

public sealed class TranslationGroupProfileTests
{
    [Fact]
    public void Version_one_document_migrates_to_the_deterministic_default_group()
    {
        const string json = """
            {"schemaVersion":1,"profileId":"11111111-1111-1111-1111-111111111111","name":"Game","sourceLanguage":"ja","targetLanguage":"en","targets":[{"targetId":"22222222-2222-2222-2222-222222222222","name":"Game","kind":"window","regions":[{"regionId":"33333333-3333-3333-3333-333333333333","name":"Dialog","translationChannels":[{"channelId":"44444444-4444-4444-4444-444444444444","initialProviderId":"translation.deepl"}]}],"remainingAreaRegion":{"regionId":"55555555-5555-5555-5555-555555555555","name":"Scan","translationChannels":[{"channelId":"66666666-6666-6666-6666-666666666666","initialProviderId":"translation.google"}]}}],"hotkeys":[],"history":{"enabled":false}}
            """;

        ProfileDocument migrated = new ProfileMigrator().Migrate(json);

        Assert.Equal(ProfileDocument.CurrentVersion, migrated.SchemaVersion);
        Assert.Equal(ProfileDocument.DefaultTranslationGroupId, migrated.ActiveTranslationGroupId);
        Assert.Equal(ProfileDocument.DefaultTranslationGroupId,
            migrated.Targets[0].Regions[0].TranslationChannels[0].TranslationGroupId);
        Assert.Equal(ProfileDocument.DefaultTranslationGroupId,
            migrated.Targets[0].RemainingAreaRegion!.TranslationChannels[0].TranslationGroupId);
    }

    [Fact]
    public void Factory_uses_only_the_active_group()
    {
        Guid alternate = Guid.NewGuid();
        ProfileRegion region = ProfileRegion.Create("Dialog", new NormalizedRect(0, 0, 1, 1)) with
        {
            TranslationChannels =
            [
                ProfileTranslationChannel.Create("translation.deepl") with
                {
                    TranslationGroupId = ProfileDocument.DefaultTranslationGroupId,
                },
                ProfileTranslationChannel.Create("translation.google") with
                {
                    TranslationGroupId = alternate,
                },
            ],
        };
        ProfileDocument profile = ProfileDocument.Create("Game", "ja", "en") with
        {
            TranslationGroups =
            [
                new ProfileTranslationGroup { TranslationGroupId = ProfileDocument.DefaultTranslationGroupId, Name = "A" },
                new ProfileTranslationGroup { TranslationGroupId = alternate, Name = "B" },
            ],
            ActiveTranslationGroupId = alternate,
        };

        var channels = ProfileTranslationFactory.CreateChannels(profile, region);

        Assert.Single(channels);
        Assert.Equal("translation.google", channels[0].InitialProviderId);
    }

    [Fact]
    public void Validator_applies_the_channel_limit_to_each_translation_group()
    {
        Guid alternate = Guid.NewGuid();
        List<ProfileTranslationChannel> channels =
        [
            .. Enumerable.Range(0, 4).Select(index =>
                ProfileTranslationChannel.Create("translation.deepl") with
                {
                    TranslationGroupId = ProfileDocument.DefaultTranslationGroupId,
                    DisplayOrder = index,
                }),
            .. Enumerable.Range(0, 4).Select(index =>
                ProfileTranslationChannel.Create("translation.deepl") with
                {
                    TranslationGroupId = alternate,
                    DisplayOrder = index,
                }),
        ];
        ProfileRegion region = ProfileRegion.Create(
            "Dialog",
            new NormalizedRect(0, 0, 1, 1)) with
        {
            TranslationChannels = channels,
        };
        ProfileDocument profile = ProfileDocument.Create("Game", "ja", "en") with
        {
            TranslationGroups =
            [
                new ProfileTranslationGroup
                {
                    TranslationGroupId = ProfileDocument.DefaultTranslationGroupId,
                    Name = "A",
                },
                new ProfileTranslationGroup
                {
                    TranslationGroupId = alternate,
                    Name = "B",
                },
            ],
            Targets =
            [
                ProfileTarget.Create("Game", CaptureTargetKind.Window) with
                {
                    Regions = [region],
                },
            ],
        };

        ProfileValidationResult result = new ProfileValidator().Validate(
            profile,
            RuntimeCapabilities.VersionOne,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "translation.deepl",
            });

        Assert.True(result.IsValid);
        Assert.All(
            Assert.Single(result.Document.Targets).Regions[0].TranslationChannels,
            channel => Assert.True(channel.Enabled));
    }
}
