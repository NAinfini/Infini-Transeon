using System.IO;
using System.Linq;
using GameOcrBench;

namespace InfiniTranseon.Bench.Tests;

public sealed class FixtureGeneratorTests
{
    private static readonly string[] AllScenarios =
    {
        "clear-subtitle", "outlined", "drop-shadow", "small-text", "typewriter", "moving",
    };

    [Fact]
    public void GenerateHonoursTheSixScenarioByFiveLanguageShapeContract()
    {
        using var workspace = new TempWorkspace();
        var options = new GeneratorOptions(
            Seed: 314,
            Scenarios: AllScenarios,
            Languages: FixtureCorpus.Languages,
            Resolutions: new[] { (640, 360) },
            SequenceFrames: FixtureGenerator.DefaultSequenceFrames);

        FixtureManifest manifest = FixtureGenerator.Generate(workspace.Path, options);

        Assert.Equal(6, manifest.Scenarios.Count);
        Assert.Equal(5, manifest.Languages.Count);
        Assert.Single(manifest.Resolutions);

        // Single-frame scenarios contribute 1 image; typewriter and moving contribute
        // SequenceFrames each: (4 * 1) + (2 * 6) = 16 images per language per resolution.
        int perLanguage = 4 + (2 * FixtureGenerator.DefaultSequenceFrames);
        Assert.Equal(perLanguage * 5, manifest.Images.Count);
        Assert.Equal(
            AllScenarios.OrderBy(name => name, StringComparer.Ordinal),
            manifest.Images.Select(image => image.Scenario).Distinct().OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            FixtureCorpus.Languages.OrderBy(name => name, StringComparer.Ordinal),
            manifest.Images.Select(image => image.Language).Distinct().OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ManifestRecordsSchemaVersionOneAndTheResolvedRunHeader()
    {
        using var workspace = new TempWorkspace();
        var options = new GeneratorOptions(
            Seed: 99,
            Scenarios: new[] { "clear-subtitle" },
            Languages: new[] { "en" },
            Resolutions: new[] { (1280, 720) },
            SequenceFrames: 2);

        FixtureManifest manifest = FixtureGenerator.Generate(workspace.Path, options);

        Assert.Equal(1, FixtureGenerator.ManifestSchemaVersion);
        Assert.Equal(FixtureGenerator.ManifestSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(FixtureGenerator.GeneratorName, manifest.Generator);
        Assert.Equal(99, manifest.Seed);
        Assert.False(string.IsNullOrWhiteSpace(manifest.DeterminismCaveat));
        Assert.Equal(new[] { "1280x720" }, manifest.Resolutions.ToArray());
        Assert.NotEmpty(manifest.Images);
    }

    [Fact]
    public void RegeneratingWithIdenticalInputsYieldsByteIdenticalManifests()
    {
        using var runA = new TempWorkspace();
        using var runB = new TempWorkspace();
        var options = new GeneratorOptions(
            Seed: 4242,
            Scenarios: new[] { "clear-subtitle", "typewriter" },
            Languages: new[] { "en", "ja" },
            Resolutions: new[] { (1280, 720) },
            SequenceFrames: 2);

        FixtureGenerator.Generate(runA.Path, options);
        FixtureGenerator.Generate(runB.Path, options);

        byte[] bytesA = File.ReadAllBytes(runA.Combine("manifest.json"));
        byte[] bytesB = File.ReadAllBytes(runB.Combine("manifest.json"));
        Assert.Equal(bytesA, bytesB);
    }

    [Fact]
    public void EveryGroundTruthBoxIsNonDegenerateAndInsideItsImage()
    {
        using var workspace = new TempWorkspace();
        var options = new GeneratorOptions(
            Seed: 20260720,
            Scenarios: AllScenarios,
            Languages: new[] { "en", "ja" },
            Resolutions: new[] { (1280, 720) },
            SequenceFrames: FixtureGenerator.DefaultSequenceFrames);

        FixtureManifest manifest = FixtureGenerator.Generate(workspace.Path, options);

        Assert.All(manifest.Images, image =>
        {
            Assert.NotEmpty(image.Lines);
            string imageFile = workspace.Combine(image.ImagePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(imageFile), $"Missing rendered image: {image.ImagePath}");

            foreach (LineGroundTruth line in image.Lines)
            {
                BoundingBox box = line.BoundingBox;
                Assert.False(string.IsNullOrEmpty(line.Text));
                Assert.True(box.Width > 0 && box.Height > 0, $"Degenerate box in {image.ImagePath}.");
                Assert.True(box.X >= 0 && box.Y >= 0, $"Negative origin in {image.ImagePath}.");
                Assert.True(box.X + box.Width <= image.Width, $"Box escapes width in {image.ImagePath}.");
                Assert.True(box.Y + box.Height <= image.Height, $"Box escapes height in {image.ImagePath}.");
                if (image.Scenario == "small-text")
                    Assert.True(box.Height <= 16, $"small-text glyph height exceeds cap in {image.ImagePath}.");
            }
        });
    }

    [Fact]
    public void GenerateFailsLoudlyWhenAnUnknownLanguageFontMappingIsRequested()
    {
        // Font-missing is not directly reproducible without uninstalling a system font,
        // but the same loud-failure guard rejects an unmapped language up front. This
        // proves the generator throws rather than silently substituting a fallback.
        using var workspace = new TempWorkspace();
        var options = new GeneratorOptions(
            Seed: 1,
            Scenarios: new[] { "clear-subtitle" },
            Languages: new[] { "xx-Unknown" },
            Resolutions: new[] { (1280, 720) },
            SequenceFrames: 1);

        Assert.Throws<InvalidOperationException>(() => FixtureGenerator.Generate(workspace.Path, options));
    }

    [Fact]
    public void GenerateRejectsANullOptionsArgument()
    {
        using var workspace = new TempWorkspace();
        Assert.Throws<ArgumentNullException>(() => FixtureGenerator.Generate(workspace.Path, null!));
    }

    [Fact]
    public void SelfCheckVerifiesSchemaBoxSanityAndDeterminism()
    {
        Assert.Equal(0, FixtureGenerator.SelfCheck());
    }
}
