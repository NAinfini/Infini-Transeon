using InfiniTranseon.Core.Ocr;

namespace InfiniTranseon.Core.Tests.Ocr;

public sealed class PaddleOcrModelCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "infini-ppocr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ja", "ja")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("zh", "zh-hans")]
    [InlineData("zh-CN", "zh-hans")]
    [InlineData("zh-Hans-CN", "zh-hans")]
    [InlineData("zh-TW", "zh-hant")]
    [InlineData("zh-Hant-HK", "zh-hant")]
    [InlineData("KO-kr", "ko")]
    public void NormalizesTagsToPackageNames(string tag, string expected) =>
        Assert.Equal(expected, ManagedPaddleOcrModelCatalog.NormalizeLanguageTag(tag));

    [Fact]
    public void OrdersVersionsNumericallyRatherThanAlphabetically() =>
        Assert.True(ManagedPaddleOcrModelCatalog.CompareVersions("1.10.0", "1.9.0") > 0);

    [Fact]
    public void ReportsNothingInstalledWhenTheRootIsAbsent()
    {
        var catalog = new ManagedPaddleOcrModelCatalog(_root);
        Assert.Empty(catalog.InstalledLanguageTags);
        Assert.False(catalog.TryResolve("ja", out _));
    }

    /// <summary>
    /// Detection lives in its own package, so a language recognizer alone must not be reported as
    /// usable: loading it would fail at the first frame instead of at the point the user chose it.
    /// </summary>
    [Fact]
    public void IgnoresLanguagePackagesWhileTheSharedDetectorIsMissing()
    {
        Install("ppocr-v4-rec-ja", "1.0.0", "rec");
        var catalog = new ManagedPaddleOcrModelCatalog(_root);
        Assert.Empty(catalog.InstalledLanguageTags);
        Assert.False(catalog.TryResolve("ja-JP", out _));
    }

    [Fact]
    public void ResolvesTheSharedDetectorAndClassifierAlongsideTheLanguage()
    {
        Install("ppocr-v4-base", "1.0.0", "det", "cls");
        Install("ppocr-v4-rec-ja", "1.0.0", "rec");

        var catalog = new ManagedPaddleOcrModelCatalog(_root);
        Assert.Equal(["ja"], catalog.InstalledLanguageTags);
        Assert.True(catalog.TryResolve("ja-JP", out PaddleOcrModelSet? resolved));
        Assert.Equal("ja", resolved!.LanguageTag);
        Assert.EndsWith(Path.Combine("det", "model.onnx"), resolved.DetectionModelPath);
        Assert.EndsWith(Path.Combine("cls", "model.onnx"), resolved.ClassificationModelPath!);
        Assert.EndsWith(Path.Combine("rec", "model.onnx"), resolved.RecognitionModelPath);
    }

    /// <summary>Classification is optional; without it the pipeline just never flips a crop.</summary>
    [Fact]
    public void ResolvesWithoutAClassifier()
    {
        Install("ppocr-v4-base", "1.0.0", "det");
        Install("ppocr-v4-rec-en", "1.0.0", "rec");

        Assert.True(new ManagedPaddleOcrModelCatalog(_root).TryResolve("en", out PaddleOcrModelSet? resolved));
        Assert.Null(resolved!.ClassificationModelPath);
    }

    /// <summary>
    /// A silent update publishes the new version before retiring the old one, so both are on disk
    /// for a moment. The app must already be running the new files by then.
    /// </summary>
    [Fact]
    public void PrefersTheNewestVersionWhileAnOldCopyIsStillPresent()
    {
        Install("ppocr-v4-base", "1.0.0", "det");
        Install("ppocr-v4-base", "1.10.0", "det");
        Install("ppocr-v4-rec-en", "2.0.0", "rec");

        Assert.True(new ManagedPaddleOcrModelCatalog(_root).TryResolve("en", out PaddleOcrModelSet? resolved));
        Assert.Contains(Path.Combine("ppocr-v4-base", "1.10.0"), resolved!.DetectionModelPath);
    }

    /// <summary>A profile saying "zh-CN" must find the package the catalog names "zh-hans".</summary>
    [Fact]
    public void MapsAChineseRegionToTheInstalledScript()
    {
        Install("ppocr-v4-base", "1.0.0", "det");
        Install("ppocr-v4-rec-zh-hans", "1.0.0", "rec");

        Assert.True(new ManagedPaddleOcrModelCatalog(_root).TryResolve("zh-CN", out PaddleOcrModelSet? resolved));
        Assert.Equal("zh-hans", resolved!.LanguageTag);
    }

    /// <summary>
    /// The local models are per-language, so "auto" has no answer here. Returning some arbitrary
    /// installed language would read Japanese with an English model and produce confident nonsense.
    /// </summary>
    [Fact]
    public void RefusesAutomaticLanguageSelection()
    {
        Install("ppocr-v4-base", "1.0.0", "det");
        Install("ppocr-v4-rec-ja", "1.0.0", "rec");

        var catalog = new ManagedPaddleOcrModelCatalog(_root);
        Assert.False(catalog.TryResolve("auto", out _));
        Assert.False(catalog.TryResolve(null, out _));
    }

    [Fact]
    public void ReportsAPackageThatHoldsMoreModelsThanTheCatalogDeclared()
    {
        Install("ppocr-v4-base", "1.0.0", "det");
        File.WriteAllText(
            Path.Combine(_root, "packages", "ppocr-v4-base", "1.0.0", "det", "extra.onnx"), "x");

        Assert.Throws<InvalidDataException>(
            () => new ManagedPaddleOcrModelCatalog(_root).TryResolve("en", out _));
    }

    private void Install(string modelId, string version, params string[] slots)
    {
        foreach (string slot in slots)
        {
            string directory = Path.Combine(_root, "packages", modelId, version, slot);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "model.onnx"), "not a real model");
        }
    }
}
