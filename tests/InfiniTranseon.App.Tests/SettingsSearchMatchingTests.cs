using InfiniTranseon.App.Presentation.ViewModels;

namespace InfiniTranseon.App.Tests;

public sealed class SettingsSearchMatchingTests
{
    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        Assert.True(SettingsViewModel.MatchesSearch(string.Empty, "Theme", "Light or dark"));
    }

    [Fact]
    public void WhitespaceQueryMatchesEverything()
    {
        Assert.True(SettingsViewModel.MatchesSearch("   ", "Theme", "Light or dark"));
    }

    [Fact]
    public void QueryMatchesWhenAnyFieldContainsItCaseInsensitively()
    {
        Assert.True(SettingsViewModel.MatchesSearch("THEME", "Theme", "Light or dark"));
    }

    [Fact]
    public void QueryMatchesWhenOnlyTheDescriptionFieldContainsIt()
    {
        Assert.True(SettingsViewModel.MatchesSearch("dark", "Theme", "Light or dark"));
    }

    [Fact]
    public void QueryDoesNotMatchWhenNoFieldContainsIt()
    {
        Assert.False(SettingsViewModel.MatchesSearch("hotkey", "Theme", "Light or dark"));
    }

    [Fact]
    public void ThrowsOnNullFields()
    {
        Assert.Throws<ArgumentNullException>(
            () => SettingsViewModel.MatchesSearch("query", null!));
    }
}
