using System.Linq;
using GameOcrBench;

namespace InfiniTranseon.Bench.Tests;

public sealed class FixtureCorpusTests
{
    [Fact]
    public void LanguagesAreTheFiveContractLanguagesInFixedOrder()
    {
        Assert.Equal(
            new[] { "zh-Hans", "zh-Hant", "ja", "ko", "en" },
            FixtureCorpus.Languages.ToArray());
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("en")]
    public void EveryContractLanguageExposesExactlySixNonEmptyStrings(string language)
    {
        Assert.True(FixtureCorpus.IsKnownLanguage(language));

        IReadOnlyList<string> strings = FixtureCorpus.ForLanguage(language);

        Assert.Equal(6, strings.Count);
        Assert.All(strings, value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void ForLanguageReturnsTheSameOrderedInstanceOnRepeatedCalls()
    {
        // Order stability is the determinism contract for seed-driven selection.
        Assert.Equal(
            FixtureCorpus.ForLanguage("en").ToArray(),
            FixtureCorpus.ForLanguage("en").ToArray());
    }

    [Theory]
    [InlineData("de")]
    [InlineData("")]
    [InlineData("EN")]
    public void UnknownLanguagesAreRejected(string language)
    {
        Assert.False(FixtureCorpus.IsKnownLanguage(language));
        Assert.Throws<InvalidOperationException>(() => FixtureCorpus.ForLanguage(language));
    }
}
