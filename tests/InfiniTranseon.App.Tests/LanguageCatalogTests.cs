using InfiniTranseon.App.Presentation;

namespace InfiniTranseon.App.Tests;

public sealed class LanguageCatalogTests
{
    [Fact]
    public void Source_options_include_auto_detect_but_target_options_do_not()
    {
        IReadOnlyList<LanguageOption> source = LanguageCatalog.CreateSourceOptions("zh-CN");
        IReadOnlyList<LanguageOption> target = LanguageCatalog.CreateTargetOptions("zh-CN");

        LanguageOption auto = Assert.Single(source, option => option.Code == "auto");
        Assert.Contains("自动检测", auto.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(target, option => option.Code == "auto");
    }

    [Theory]
    [InlineData("Japanese", "ja")]
    [InlineData("日语", "ja")]
    [InlineData("日本語", "ja")]
    [InlineData("ja", "ja")]
    public void Search_matches_english_chinese_native_names_and_codes(
        string query,
        string expectedCode)
    {
        IReadOnlyList<LanguageOption> options = LanguageCatalog.CreateSourceOptions("en-US");

        IReadOnlyList<LanguageOption> matches = LanguageCatalog.Filter(options, query);

        Assert.Contains(matches, option => option.Code == expectedCode);
    }

    [Fact]
    public void Existing_unlisted_profile_language_is_preserved()
    {
        IReadOnlyList<LanguageOption> options = LanguageCatalog.CreateTargetOptions("en-US");

        LanguageOption custom = LanguageCatalog.ResolveOrCreate(options, "tlh");

        Assert.Equal("tlh", custom.Code);
        Assert.Equal("tlh", custom.DisplayName);
    }
}
