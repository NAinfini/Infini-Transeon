using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Translation;

public sealed class TranslationMemoryTests
{
    [Fact]
    public void PersistentMemoryRejectsAnUnusableByteBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationMemory(
            new TranslationMemoryOptions(
                PersistentEnabled: true,
                MaximumPersistentBytes: 0),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "memory.db")));
    }

    [Fact]
    public async Task ExactKeyNormalizesUnicodeButInvalidatesProviderPromptGlossaryAndContext()
    {
        var memory = new TranslationMemory(new TranslationMemoryOptions());
        Guid profile = Guid.NewGuid();
        TranslationCacheKey key = Key("Cafe\u0301", provider: "one", prompt: "p1", glossary: "g1", context: "c1");
        await memory.StoreAsync(profile, key, "咖啡馆", TestContext.Current.CancellationToken);

        TranslationMemoryHit? hit = await memory.FindAsync(
            profile,
            Key("Café", provider: "one", prompt: "p1", glossary: "g1", context: "c1"),
            TestContext.Current.CancellationToken);

        Assert.True(hit?.Exact);
        Assert.Null(await memory.FindAsync(profile,
            Key("Café", provider: "two", prompt: "p1", glossary: "g1", context: "c1"),
            TestContext.Current.CancellationToken));
        Assert.Null(await memory.FindAsync(profile,
            Key("Café", provider: "one", prompt: "p2", glossary: "g1", context: "c1"),
            TestContext.Current.CancellationToken));
        Assert.Null(await memory.FindAsync(profile,
            Key("Café", provider: "one", prompt: "p1", glossary: "g2", context: "c1"),
            TestContext.Current.CancellationToken));
        Assert.Null(await memory.FindAsync(profile,
            Key("Café", provider: "one", prompt: "p1", glossary: "g1", context: "c2"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisabledPersistenceWritesNoDatabaseTextOrFile()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "memory.db");
        var memory = new TranslationMemory(
            new TranslationMemoryOptions(PersistentEnabled: false),
            database);

        await memory.StoreAsync(Guid.NewGuid(), Key("private source"), "private translation",
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task PersistentExactMemorySurvivesNewInstanceAndChecksSourceAgainstDigestCollision()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "memory.db");
        Guid profile = Guid.NewGuid();
        TranslationCacheKey key = Key("persistent source");
        var first = new TranslationMemory(
            new TranslationMemoryOptions(PersistentEnabled: true), database);
        await first.StoreAsync(profile, key, "persistent result", TestContext.Current.CancellationToken);
        var second = new TranslationMemory(
            new TranslationMemoryOptions(PersistentEnabled: true), database);

        TranslationMemoryHit? hit = await second.FindAsync(profile, key, TestContext.Current.CancellationToken);

        Assert.True(hit?.Persistent);
        Assert.Equal("persistent result", hit?.Translation);
    }

    [Fact]
    public async Task PersistentEvictionKeepsTheNewestPrefixWithinTheByteBudget()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "memory.db");
        Guid profile = Guid.NewGuid();
        var options = new TranslationMemoryOptions(
            PersistentEnabled: true,
            MaximumPersistentBytes: 1_100);
        var writer = new TranslationMemory(options, database);
        TranslationCacheKey oldest = Key("oldest source");
        TranslationCacheKey middle = Key("middle source");
        TranslationCacheKey newest = Key("newest source");
        await writer.StoreAsync(profile, oldest, "oldest result",
            TestContext.Current.CancellationToken);
        await Task.Delay(5, TestContext.Current.CancellationToken);
        await writer.StoreAsync(profile, middle, "middle result",
            TestContext.Current.CancellationToken);
        await Task.Delay(5, TestContext.Current.CancellationToken);
        await writer.StoreAsync(profile, newest, "newest result",
            TestContext.Current.CancellationToken);
        var reader = new TranslationMemory(options, database);

        Assert.Null(await reader.FindAsync(
            profile, oldest, TestContext.Current.CancellationToken));
        Assert.Equal("middle result", (await reader.FindAsync(
            profile, middle, TestContext.Current.CancellationToken))?.Translation);
        Assert.Equal("newest result", (await reader.FindAsync(
            profile, newest, TestContext.Current.CancellationToken))?.Translation);
    }

    [Fact]
    public async Task MemoryCacheNeverReturnsAnotherProfilesTranslation()
    {
        var memory = new TranslationMemory(new TranslationMemoryOptions());
        Guid firstProfile = Guid.NewGuid();
        Guid secondProfile = Guid.NewGuid();
        TranslationCacheKey key = Key("shared menu text");
        await memory.StoreAsync(
            firstProfile, key, "profile one result", TestContext.Current.CancellationToken);

        TranslationMemoryHit? crossProfile = await memory.FindAsync(
            secondProfile, key, TestContext.Current.CancellationToken);

        Assert.Null(crossProfile);
        Assert.Equal("profile one result", (await memory.FindAsync(
            firstProfile, key, TestContext.Current.CancellationToken))?.Translation);
    }

    [Fact]
    public async Task FuzzyMatchingIsCautiousAndNeverAppliesToIdsOrNumericRows()
    {
        var memory = new TranslationMemory(new TranslationMemoryOptions(
            FuzzyEnabled: true,
            MinimumSimilarity: 0.85));
        Guid profile = Guid.NewGuid();
        await memory.StoreAsync(profile, Key("The road ahead is dangerous"), "前路危险",
            TestContext.Current.CancellationToken);
        await memory.StoreAsync(profile, Key("Attack:100"), "攻击：100",
            TestContext.Current.CancellationToken);
        await memory.StoreAsync(profile, Key("PLAYER_ID_001"), "玩家",
            TestContext.Current.CancellationToken);

        TranslationMemoryHit? sentence = await memory.FindAsync(profile,
            Key("The road ahead was dangerous"), TestContext.Current.CancellationToken);

        Assert.False(sentence?.Exact);
        Assert.Null(await memory.FindAsync(profile, Key("Attack:101"), TestContext.Current.CancellationToken));
        Assert.Null(await memory.FindAsync(profile, Key("PLAYER_ID_002"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EqualFuzzyMatchesPreferTheMostRecentlyUsedCandidate()
    {
        var memory = new TranslationMemory(new TranslationMemoryOptions(
            FuzzyEnabled: true,
            MinimumSimilarity: 0.8));
        Guid profile = Guid.NewGuid();
        TranslationCacheKey first = Key("前方的道路很危险");
        TranslationCacheKey second = Key("前方那道路很危险");
        await memory.StoreAsync(profile, first, "first", TestContext.Current.CancellationToken);
        await memory.StoreAsync(profile, second, "second", TestContext.Current.CancellationToken);
        Assert.True((await memory.FindAsync(
            profile, first, TestContext.Current.CancellationToken))?.Exact);

        TranslationMemoryHit? hit = await memory.FindAsync(
            profile, Key("前方这道路很危险"), TestContext.Current.CancellationToken);

        Assert.Equal("first", hit?.Translation);
        Assert.False(hit?.Exact);
    }

    [Fact]
    public void GlossaryPlaceholdersRestoreExactTargetsAndRejectProviderDamage()
    {
        ProtectedGlossaryText protectedText = GlossaryProcessor.Protect(
            "Equip Excalibur now",
            [new GlossaryEntry("Excalibur", "誓约胜利之剑")]);
        string placeholder = Assert.Single(protectedText.Replacements).Key;

        Assert.Equal("装备誓约胜利之剑", GlossaryProcessor.Restore("装备" + placeholder, protectedText));
        Assert.Throws<InvalidDataException>(() => GlossaryProcessor.Restore("装备丢失占位符", protectedText));
    }

    [Fact]
    public async Task RegionCorrectionPrecedesProfileCorrectionAndUndoRestoresPreviousValue()
    {
        using var temp = new TempDirectory();
        var store = new CorrectionStore(Path.Combine(temp.Path, "corrections.db"));
        Guid profile = Guid.NewGuid();
        Guid region = Guid.NewGuid();
        var profileScope = new CorrectionScope(profile, null, "en", "zh", "g1");
        var regionScope = profileScope with { RegionId = region };
        await store.AddAsync(profileScope, "Mana", "魔力", TestContext.Current.CancellationToken);
        TranslationCorrection regional = await store.AddAsync(
            regionScope, "Mana", "法力", TestContext.Current.CancellationToken);

        Assert.Equal("法力", (await store.FindAsync(
            regionScope, "Mana", TestContext.Current.CancellationToken))?.Corrected);
        Assert.True(await store.UndoAsync(regional.CorrectionId, TestContext.Current.CancellationToken));
        Assert.Equal("魔力", (await store.FindAsync(
            regionScope, "Mana", TestContext.Current.CancellationToken))?.Corrected);
    }

    private static TranslationCacheKey Key(
        string source,
        string provider = "provider",
        string prompt = "prompt",
        string glossary = "glossary",
        string? context = null) => TranslationCacheKey.Create(
            provider, "model", "en", "zh", source, "style", prompt, glossary, "policy", context);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InfiniTranseon.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
