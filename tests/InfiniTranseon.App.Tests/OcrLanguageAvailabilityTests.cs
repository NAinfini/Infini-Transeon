using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// Matching a profile language against an installed recognizer decides whether the user is offered a
/// language this machine cannot read. Getting it wrong in either direction is costly: too strict
/// hides a language that works, too loose promises recognition that returns nonsense.
/// </summary>
public sealed class OcrLanguageAvailabilityTests
{
    private static IOcrLanguageAvailability WithWindows(params string[] tags) =>
        new WindowsOcrLanguageAvailability(() => tags);

    [Theory]
    // Region is not part of legibility: an en-US recognizer reads "en" fine.
    [InlineData("en", "en-US", true)]
    [InlineData("en", "en-GB", true)]
    [InlineData("ja", "ja", true)]
    [InlineData("zh-Hans", "zh-Hans-CN", true)]
    [InlineData("zh-Hant", "zh-Hant-TW", true)]
    // Script is: simplified and traditional recognizers cannot substitute for each other.
    [InlineData("zh-Hans", "zh-Hant-TW", false)]
    [InlineData("zh-Hant", "zh-Hans-CN", false)]
    [InlineData("ja", "en-US", false)]
    [InlineData("ko", "zh-Hans-CN", false)]
    public void Language_and_script_decide_the_match(string code, string tag, bool expected) =>
        Assert.Equal(expected, WindowsOcrLanguageAvailability.Matches(code, tag));

    [Fact]
    public void An_installed_recognizer_is_reported_with_the_tag_that_would_run()
    {
        OcrLanguageStatus status = WithWindows("en-US", "zh-Hans-CN").StatusFor("en");

        Assert.Equal(OcrLanguageSource.WindowsRecognizer, status.Source);
        Assert.Equal("en-US", status.RecognizerTag);
    }

    [Fact]
    public void A_language_windows_cannot_read_is_not_quietly_promoted()
    {
        // The product's core case: a Chinese Windows asked to read a Japanese game.
        OcrLanguageStatus status = WithWindows("zh-Hans-CN", "en-US").StatusFor("ja");

        Assert.Equal(OcrLanguageSource.NotInstalled, status.Source);
        Assert.Null(status.RecognizerTag);
    }

    [Fact]
    public void A_managed_model_covers_what_windows_does_not()
    {
        var availability = new WindowsOcrLanguageAvailability(() => ["en-US"], () => ["ja"]);

        Assert.Equal(OcrLanguageSource.LocalModel, availability.StatusFor("ja").Source);
        // Windows still wins where it has a pack — it costs no disk and no download.
        Assert.Equal(OcrLanguageSource.WindowsRecognizer, availability.StatusFor("en").Source);
    }

    [Fact]
    public void No_recognizers_at_all_leaves_every_language_unreadable() =>
        Assert.All(
            LanguageCatalog.CreateTargetOptions(),
            option => Assert.Equal(
                OcrLanguageSource.NotInstalled,
                WithWindows().StatusFor(option.Code).Source));

    /// <summary>
    /// Every code the picker offers must be resolvable, or the annotation would throw while the
    /// dropdown is being built.
    /// </summary>
    [Fact]
    public void Every_catalog_source_language_can_be_resolved()
    {
        IOcrLanguageAvailability availability = WithWindows("en-US");
        foreach (LanguageOption option in LanguageCatalog.CreateSourceOptions())
        {
            Assert.NotNull(availability.StatusFor(option.Code));
        }
    }
}
