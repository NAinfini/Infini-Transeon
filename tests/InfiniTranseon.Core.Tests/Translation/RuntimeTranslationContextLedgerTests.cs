using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Translation;

public sealed class RuntimeTranslationContextLedgerTests
{
    [Fact]
    public void ApplyInjectsSpeakerSceneAndBoundedRecentContextWithoutPersistingIt()
    {
        Guid profileId = Guid.NewGuid();
        var ledger = new RuntimeTranslationContextLedger();
        ledger.ObserveRole(profileId, ProfileRegionContextRole.Speaker, "  Alice  ");
        ledger.ObserveRole(profileId, ProfileRegionContextRole.Scene, "Castle gate");
        for (int index = 0; index < 10; index++)
            ledger.Append(profileId, $"source-{index}", $"translation-{index}");
        TranslationRunOptions baseline = CreateOptions(profileId);

        TranslationRunOptions enriched = ledger.Apply(baseline, recentLineCount: 3);

        Assert.Equal("Alice", enriched.Context.Speaker);
        Assert.Equal("Castle gate", enriched.Context.Scene);
        Assert.Equal(["source-7", "source-8", "source-9"], enriched.Context.RecentSource);
        Assert.Equal(
            ["translation-7", "translation-8", "translation-9"],
            enriched.Context.RecentTranslation);
        Assert.Empty(baseline.Context.RecentSource);
    }

    [Fact]
    public void ClearRemovesAllTransientContextForProfile()
    {
        Guid profileId = Guid.NewGuid();
        var ledger = new RuntimeTranslationContextLedger();
        ledger.ObserveRole(profileId, ProfileRegionContextRole.Speaker, "Alice");
        ledger.Append(profileId, "source", "translation");

        ledger.Clear(profileId);

        TranslationRunOptions result = ledger.Apply(CreateOptions(profileId), recentLineCount: 8);
        Assert.Null(result.Context.Speaker);
        Assert.Empty(result.Context.RecentSource);
        Assert.Empty(result.Context.RecentTranslation);
    }

    private static TranslationRunOptions CreateOptions(Guid profileId) => new(
        profileId,
        new TranslationContext("Game", "Description", null, null, [], []),
        [],
        TimeSpan.FromSeconds(5),
        8192,
        4096,
        StrictOffline: false,
        SourceLanguage: "ja",
        TargetLanguage: "zh-Hans");
}
