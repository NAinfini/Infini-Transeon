using InfiniTranseon.Core.Ocr;

namespace InfiniTranseon.Core.Tests.Ocr;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("hello   world", "hello world")]
    [InlineData("  padded  ", "padded")]
    [InlineData("tab\tseparated", "tab separated")]
    [InlineData("trailing spaces   \nnext", "trailing spaces\nnext")]
    public void NormalizeCollapsesRunsOfWhitespaceWithoutCrossingLineBreaks(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\n\n\nb", "a\nb")]
    [InlineData("\n\nleading\n\n", "leading")]
    public void NormalizeCanonicalisesLineBreaksAndCollapsesBlankLines(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void NormalizeAppliesUnicodeFormCComposition()
    {
        // "e" + combining acute accent should compose to the precomposed "é".
        string decomposed = "é";
        string normalized = TextNormalizer.Normalize(decomposed);

        Assert.Equal("é", normalized);
        Assert.Single(normalized);
    }

    [Fact]
    public void NormalizeReturnsEmptyForWhitespaceOnlyInput()
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize("   \r\n\t  "));
    }

    [Fact]
    public void NormalizeRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => TextNormalizer.Normalize(null!));
    }

    [Theory]
    [InlineData("Attack 100", true)]
    [InlineData("勇者", true)]
    [InlineData("！？。、", false)]
    [InlineData("   ", false)]
    public void ContainsMeaningfulTextDetectsLettersOrDigits(string input, bool expected)
    {
        Assert.Equal(expected, TextNormalizer.ContainsMeaningfulText(input));
    }

    [Fact]
    public void ContainsMeaningfulTextRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => TextNormalizer.ContainsMeaningfulText(null!));
    }

    [Fact]
    public void SensitiveSourceTextHashesTheValueAndNeverLeaksItInDiagnostics()
    {
        var sensitive = new SensitiveSourceText("abc");

        // SHA-256 of the UTF-8 bytes of "abc", lower-cased hex.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            sensitive.Sha256);
        Assert.Equal(3, sensitive.Length);
        Assert.Equal("abc", sensitive.Value);
        Assert.DoesNotContain("abc", sensitive.ToString(), StringComparison.Ordinal);
        Assert.Contains("length=3", sensitive.ToString(), StringComparison.Ordinal);
        Assert.Contains("ba7816bf8f01", sensitive.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveSourceTextRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SensitiveSourceText(null!));
    }
}
