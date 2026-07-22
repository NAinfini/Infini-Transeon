using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Tests.Ocr;

public sealed class PostProcessingTests
{
    [Fact]
    public void KeyValueRowsPreserveUserExpectedLineBreaks()
    {
        TextLine[] lines =
        [
            Line("Attack:100", 0.1),
            Line("Defense:100", 0.2),
            Line("Health:200", 0.3),
        ];
        var policy = new LineBreakPolicy(LineBreakMode.KeyValueRows, maximumLines: 10);

        ProcessedOcrText result = new LineLayoutProcessor().Process(lines, policy);

        Assert.Equal("Attack:100\nDefense:100\nHealth:200", result.NormalizedText);
        Assert.Equal(3, result.TranslationSegments.Count);
    }

    [Fact]
    public void CjkPunctuationAndMixedScriptsArePreservedWhenJoiningWrappedLines()
    {
        TextLine[] lines =
        [
            Line("勇者は言った、", 0.1),
            Line("Attackを上げよう。", 0.2),
        ];
        var policy = new LineBreakPolicy(LineBreakMode.JoinParagraph, maximumLines: 10);

        ProcessedOcrText result = new LineLayoutProcessor().Process(lines, policy);

        Assert.Equal("勇者は言った、Attackを上げよう。", result.NormalizedText);
    }

    [Fact]
    public void DuplicateLabelsAndSymbolOnlyNoiseAreSuppressed()
    {
        TextLine[] lines =
        [
            new("Menu", new NormalizedRect(0.1, 0.1, 0.2, 0.05), 0.99),
            new("Menu", new NormalizedRect(0.101, 0.101, 0.2, 0.05), 0.98),
            Line("★★★", 0.3),
        ];

        ProcessedOcrText result = new LineLayoutProcessor().Process(
            lines,
            new LineBreakPolicy(LineBreakMode.PreserveLines, maximumLines: 10));

        Assert.Equal("Menu", result.NormalizedText);
        Assert.Single(result.Lines);
    }

    [Fact]
    public void SpeakerNameIsSeparatedWithoutDestroyingPunctuation()
    {
        ProcessedOcrText result = new LineLayoutProcessor().Process(
            [Line("Alice: Wait—now!", 0.1)],
            new LineBreakPolicy(LineBreakMode.PreserveLines, maximumLines: 10));

        Assert.Equal("Alice", result.Speaker);
        Assert.Equal("Wait—now!", result.NormalizedText);
    }

    [Fact]
    public void ConservativeCorrectionUsesGlossaryButProtectsRiskyTokens()
    {
        var correction = new ConservativeCorrectionService(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Defnese"] = "Defense",
                ["Xylphoria"] = "Sylphoria",
                ["HP"] = "Health",
                ["100"] = "one hundred",
                ["Aeris"] = "Aerith",
            },
            new HashSet<string>(["Aeris"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal("Defense", correction.Correct("Defnese", 0.99, enabled: true));
        Assert.Equal("Xylphoria", correction.Correct("Xylphoria", 0.60, enabled: true));
        Assert.Equal("HP", correction.Correct("HP", 0.99, enabled: true));
        Assert.Equal("100", correction.Correct("100", 0.99, enabled: true));
        Assert.Equal("Aeris", correction.Correct("Aeris", 0.99, enabled: true));
        Assert.Equal("Defnese", correction.Correct("Defnese", 0.99, enabled: false));
    }

    [Fact]
    public void SensitiveSourceTextNeverLeaksThroughDiagnosticsOrToString()
    {
        ProcessedOcrText result = new LineLayoutProcessor().Process(
            [Line("private dialogue", 0.1)],
            new LineBreakPolicy(LineBreakMode.PreserveLines, maximumLines: 10));

        Assert.Equal("private dialogue", result.OriginalText.Value);
        Assert.DoesNotContain("private", result.OriginalText.ToString(), StringComparison.Ordinal);
        Assert.Equal("private dialogue".Length, result.Diagnostic.Length);
        Assert.Equal(64, result.Diagnostic.Sha256.Length);
    }

    [Fact]
    public void TypewriterTextRequiresStableFramesButForcesProgressAtMaximumWait()
    {
        var stabilizer = new TextStabilizer(new TextStabilizerOptions(
            StableFrameCount: 2,
            MinimumDelay: TimeSpan.FromMilliseconds(100),
            MaximumWait: TimeSpan.FromMilliseconds(500)));
        DateTimeOffset start = DateTimeOffset.UnixEpoch;

        Assert.False(stabilizer.Observe("H", start, generation: 1).IsStable);
        Assert.False(stabilizer.Observe("He", start.AddMilliseconds(100), generation: 1).IsStable);
        StabilizedText forced = stabilizer.Observe("Hel", start.AddMilliseconds(600), generation: 1);
        Assert.True(forced.IsStable);
        Assert.True(forced.ForcedProgress);
        Assert.False(stabilizer.Observe("Hello", start.AddMilliseconds(700), generation: 2).IsStable);
    }

    private static TextLine Line(string text, double top) =>
        new(text, new NormalizedRect(0.1, top, 0.8, 0.05), 0.99);
}
