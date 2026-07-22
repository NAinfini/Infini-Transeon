using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GameOcrBench;

// ---- Manifest / ground-truth schema (schema version recorded in the manifest) ----

/// <summary>Integer, pixel-space tight bounding box measured from glyph outlines.</summary>
internal sealed record BoundingBox(int X, int Y, int Width, int Height);

/// <summary>One rendered text line and its measured tight box.</summary>
internal sealed record LineGroundTruth(string Text, BoundingBox BoundingBox);

/// <summary>Ground truth for a single generated image.</summary>
internal sealed record FixtureImage(
    string ImagePath,
    string Scenario,
    string Language,
    int Width,
    int Height,
    int Dpi,
    int GlyphHeightPx,
    int FrameIndex,
    IReadOnlyList<LineGroundTruth> Lines);

/// <summary>Whole-run manifest enabling later CER / line-accuracy scoring.</summary>
internal sealed record FixtureManifest(
    int SchemaVersion,
    string Generator,
    int Seed,
    string DeterminismCaveat,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Resolutions,
    IReadOnlyList<FixtureImage> Images);

/// <summary>Resolved generator options for one run.</summary>
internal sealed record GeneratorOptions(
    int Seed,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<string> Languages,
    IReadOnlyList<(int Width, int Height)> Resolutions,
    int SequenceFrames);

/// <summary>
/// Deterministic synthetic OCR fixture generator. Renders game-style text with
/// System.Drawing using only Windows-shipped fonts, and emits per-image ground
/// truth plus a versioned manifest for CER / line-accuracy scoring.
/// </summary>
internal static class FixtureGenerator
{
    public const int ManifestSchemaVersion = 1;
    public const string GeneratorName = "GameOcrBench.FixtureGenerator";
    public const int DefaultSeed = 20260720;
    public const int DefaultSequenceFrames = 6;

    private const int Dpi = 96;

    private const string DeterminismCaveat =
        "Text, layout parameters (positions, em sizes, frame counts, corpus selection) and " +
        "measured bounding boxes are byte-stable for a given seed + argument set on a given host: " +
        "regenerating with identical inputs on the same machine yields an identical manifest.json. " +
        "Rendered PNG pixel bytes and fine glyph-outline geometry depend on the installed font " +
        "version and OS text rasteriser, so image bytes are NOT part of the determinism guarantee. " +
        "Score against this recorded ground truth, never against image hashes.";

    private static readonly string[] AllScenarios =
    {
        "clear-subtitle",
        "outlined",
        "drop-shadow",
        "small-text",
        "typewriter",
        "moving",
    };

    private static readonly (int Width, int Height)[] DefaultResolutions =
    {
        (1280, 720),
        (1920, 1080),
        (3840, 2160),
    };

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    // ---- CLI entry points (invoked from BenchmarkRunner.cs top-level dispatch) ----

    public static int RunCli(string[] rest)
    {
        string? outputDir = null;
        int seed = DefaultSeed;
        IReadOnlyList<string> scenarios = AllScenarios;
        IReadOnlyList<string> languages = FixtureCorpus.Languages;

        for (int i = 0; i < rest.Length; i++)
        {
            string arg = rest[i];
            switch (arg)
            {
                case "--seed":
                    if (++i >= rest.Length || !int.TryParse(rest[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                        return Fail("--seed requires an integer value.");
                    break;
                case "--scenarios":
                    if (++i >= rest.Length)
                        return Fail("--scenarios requires a comma-separated list.");
                    scenarios = SplitList(rest[i]);
                    break;
                case "--languages":
                    if (++i >= rest.Length)
                        return Fail("--languages requires a comma-separated list.");
                    languages = SplitList(rest[i]);
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        return Fail($"Unknown option '{arg}'.");
                    if (outputDir is not null)
                        return Fail("Only one output directory may be specified.");
                    outputDir = arg;
                    break;
            }
        }

        if (outputDir is null)
            return Fail("Usage: generate-fixtures <output-dir> [--seed N] [--scenarios a,b] [--languages a,b]");

        string? scenarioError = ValidateScenarios(scenarios);
        if (scenarioError is not null)
            return Fail(scenarioError);
        string? languageError = ValidateLanguages(languages);
        if (languageError is not null)
            return Fail(languageError);

        var options = new GeneratorOptions(seed, scenarios, languages, DefaultResolutions, DefaultSequenceFrames);
        try
        {
            FixtureManifest manifest = Generate(outputDir, options);
            Console.Out.WriteLine(
                $"Generated {manifest.Images.Count} fixtures into '{Path.GetFullPath(outputDir)}' (seed {seed}).");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
    }

    public static int SelfCheck()
    {
        string root = Path.Combine(Path.GetTempPath(), "GameOcrBench-selfcheck-" + Guid.NewGuid().ToString("N"));
        string runA = Path.Combine(root, "a");
        string runB = Path.Combine(root, "b");
        var options = new GeneratorOptions(
            Seed: 4242,
            Scenarios: new[] { "clear-subtitle", "small-text", "typewriter" },
            Languages: new[] { "en", "ja" },
            Resolutions: new[] { (1280, 720) },
            SequenceFrames: 2);

        var failures = new List<string>();
        try
        {
            FixtureManifest manifest;
            byte[] bytesA;
            byte[] bytesB;
            try
            {
                manifest = Generate(runA, options);
                Generate(runB, options);
                bytesA = File.ReadAllBytes(Path.Combine(runA, "manifest.json"));
                bytesB = File.ReadAllBytes(Path.Combine(runB, "manifest.json"));
            }
            catch (InvalidOperationException ex)
            {
                return Fail("Self-check generation failed: " + ex.Message);
            }

            // Determinism: same seed + args -> byte-identical manifest on this host.
            if (!bytesA.AsSpan().SequenceEqual(bytesB))
                failures.Add("Manifest bytes differ between two identical runs (determinism violation).");

            // Schema header.
            if (manifest.SchemaVersion != ManifestSchemaVersion)
                failures.Add($"Manifest schemaVersion {manifest.SchemaVersion} != {ManifestSchemaVersion}.");
            if (manifest.Seed != options.Seed)
                failures.Add($"Manifest seed {manifest.Seed} != {options.Seed}.");
            if (string.IsNullOrWhiteSpace(manifest.DeterminismCaveat))
                failures.Add("Manifest determinism caveat is missing.");
            if (manifest.Images.Count == 0)
                failures.Add("Manifest recorded zero images.");

            foreach (FixtureImage image in manifest.Images)
            {
                string label = image.ImagePath;
                if (!File.Exists(Path.Combine(runA, image.ImagePath.Replace('/', Path.DirectorySeparatorChar))))
                    failures.Add($"{label}: image file is missing.");
                if (image.Lines.Count == 0)
                    failures.Add($"{label}: no ground-truth lines.");

                foreach (LineGroundTruth line in image.Lines)
                {
                    BoundingBox box = line.BoundingBox;
                    if (string.IsNullOrEmpty(line.Text))
                        failures.Add($"{label}: empty line text.");
                    if (box.Width <= 0 || box.Height <= 0)
                        failures.Add($"{label}: non-positive box {box.Width}x{box.Height}.");
                    if (box.X < 0 || box.Y < 0 || box.X + box.Width > image.Width || box.Y + box.Height > image.Height)
                        failures.Add($"{label}: box {box.X},{box.Y},{box.Width},{box.Height} escapes image {image.Width}x{image.Height}.");
                    if (image.Scenario == "small-text" && box.Height > 16)
                        failures.Add($"{label}: small-text line height {box.Height}px exceeds 16px.");
                }
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("Self-check FAILED:");
            foreach (string failure in failures)
                Console.Error.WriteLine("  - " + failure);
            return 1;
        }

        Console.Out.WriteLine("Self-check passed: schema, box sanity, small-text height and determinism verified.");
        return 0;
    }

    // ---- Core generation ----

    public static FixtureManifest Generate(string outputDir, GeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(outputDir);

        // Fail loudly and up front if any required font family is missing.
        foreach (string language in options.Languages)
            using (ResolveFontFamily(language)) { }

        var images = new List<FixtureImage>();
        foreach (string scenario in options.Scenarios)
        {
            foreach (string language in options.Languages)
            {
                foreach ((int width, int height) in options.Resolutions)
                {
                    int frames = scenario is "typewriter" or "moving" ? options.SequenceFrames : 1;
                    for (int frame = 0; frame < frames; frame++)
                        images.Add(RenderImage(outputDir, options.Seed, scenario, language, width, height, frame));
                }
            }
        }

        var manifest = new FixtureManifest(
            ManifestSchemaVersion,
            GeneratorName,
            options.Seed,
            DeterminismCaveat,
            options.Scenarios.ToArray(),
            options.Languages.ToArray(),
            options.Resolutions.Select(r => $"{r.Width}x{r.Height}").ToArray(),
            images);

        File.WriteAllBytes(
            Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson));
        return manifest;
    }

    private static FixtureImage RenderImage(
        string outputDir, int seed, string scenario, string language, int width, int height, int frame)
    {
        var rng = new DeterministicRandom(SeedFor(seed, scenario, language, width, height, frame));
        int glyphEmPx = GlyphEmPx(scenario, height);
        IReadOnlyList<string> lineTexts = LineTexts(scenario, language, rng, frame);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(Dpi, Dpi);
        var groundTruth = new List<LineGroundTruth>();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawBackground(graphics, rng, width, height, scenario);

            using FontFamily family = ResolveFontFamily(language);

            float margin = Math.Max(24f, width * 0.05f);
            float lineGap = glyphEmPx * 0.45f;

            // Stack lines in a lower-third subtitle band; measure tight glyph bounds.
            var paths = new List<(GraphicsPath Path, RectangleF Raw)>();
            float totalHeight = 0f;
            try
            {
                foreach (string text in lineTexts)
                {
                    var path = new GraphicsPath();
                    using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
                    format.FormatFlags |= StringFormatFlags.NoClip;
                    path.AddString(text, family, (int)FontStyle.Regular, glyphEmPx, new PointF(0f, 0f), format);
                    RectangleF raw = path.GetBounds();
                    paths.Add((path, raw));
                    totalHeight += raw.Height;
                }
                totalHeight += lineGap * Math.Max(0, paths.Count - 1);

                float startY = scenario == "small-text"
                    ? margin
                    : Math.Max(margin, height - margin - totalHeight);
                float baseX = scenario == "moving"
                    ? margin + frame * (glyphEmPx * 0.9f)
                    : margin;

                float cursorY = startY;
                Color textColor = Color.FromArgb(255, 245, 245, 245);
                Color outlineColor = Color.FromArgb(255, 12, 12, 16);

                foreach ((GraphicsPath path, RectangleF raw) in paths)
                {
                    float desiredX = scenario is "clear-subtitle" or "outlined" or "drop-shadow"
                        ? (width - raw.Width) / 2f
                        : baseX;
                    desiredX = Clamp(desiredX, margin, Math.Max(margin, width - margin - raw.Width));
                    float desiredY = Clamp(cursorY, margin, Math.Max(margin, height - margin - raw.Height));

                    using (var move = new Matrix())
                    {
                        move.Translate(desiredX - raw.X, desiredY - raw.Y);
                        path.Transform(move);
                    }

                    if (scenario == "drop-shadow")
                    {
                        float offset = Math.Max(2f, glyphEmPx * 0.06f);
                        using var shadow = (GraphicsPath)path.Clone();
                        using var shift = new Matrix();
                        shift.Translate(offset, offset);
                        shadow.Transform(shift);
                        using var shadowBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                        graphics.FillPath(shadowBrush, shadow);
                    }

                    if (scenario == "outlined")
                    {
                        float penWidth = Math.Max(2f, glyphEmPx * 0.10f);
                        using var pen = new Pen(outlineColor, penWidth) { LineJoin = LineJoin.Round };
                        graphics.DrawPath(pen, path);
                    }

                    using (var brush = new SolidBrush(textColor))
                        graphics.FillPath(brush, path);

                    RectangleF tight = path.GetBounds();
                    groundTruth.Add(new LineGroundTruth(
                        lineTexts[groundTruth.Count],
                        ToIntegerBox(tight, width, height)));

                    cursorY = desiredY + raw.Height + lineGap;
                }
            }
            finally
            {
                foreach ((GraphicsPath path, _) in paths)
                    path.Dispose();
            }
        }

        // Graphics is disposed above before the bitmap is serialised to PNG.
        string relativePath = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1}/{2}x{3}/{0}_{1}_{2}x{3}_{4:D3}.png",
            scenario, language, width, height, frame);
        string absolutePath = Path.Combine(outputDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        bitmap.Save(absolutePath, ImageFormat.Png);

        return new FixtureImage(
            relativePath, scenario, language, width, height, Dpi, glyphEmPx, frame, groundTruth);
    }

    // ---- Rendering helpers ----

    private static void DrawBackground(Graphics graphics, DeterministicRandom rng, int width, int height, string scenario)
    {
        // Procedural game-UI-like gradient panel; deterministic colours from the PRNG.
        Color top = Color.FromArgb(255, 24 + rng.NextInt(40), 30 + rng.NextInt(40), 48 + rng.NextInt(60));
        Color bottom = Color.FromArgb(255, 8 + rng.NextInt(24), 10 + rng.NextInt(24), 18 + rng.NextInt(32));
        using (var background = new LinearGradientBrush(
            new Rectangle(0, 0, width, height), top, bottom, LinearGradientMode.Vertical))
            graphics.FillRectangle(background, 0, 0, width, height);

        // Semi-transparent subtitle band behind the lower third (skipped for top-anchored small text).
        if (scenario != "small-text")
        {
            int bandHeight = Math.Max(80, height / 5);
            using var band = new SolidBrush(Color.FromArgb(96, 0, 0, 0));
            graphics.FillRectangle(band, 0, height - bandHeight, width, bandHeight);
        }
    }

    private static FontFamily ResolveFontFamily(string language)
    {
        string familyName = language switch
        {
            "en" => "Segoe UI",
            "zh-Hans" => "Microsoft YaHei",
            "zh-Hant" => "Microsoft YaHei",
            "ja" => "Yu Gothic UI",
            "ko" => "Malgun Gothic",
            _ => throw new InvalidOperationException($"No font mapping is defined for language '{language}'."),
        };

        try
        {
            return new FontFamily(familyName);
        }
        catch (ArgumentException)
        {
            // Debug-first: no silent substitution. The run must fail loudly.
            throw new InvalidOperationException(
                $"Required Windows font family '{familyName}' for language '{language}' is not installed. " +
                "Install it or run on a Windows host that ships it; no fallback font is used.");
        }
    }

    private static int GlyphEmPx(string scenario, int height)
    {
        if (scenario == "small-text")
            return 16; // Absolute cap: physical glyph height stays <= 16px regardless of resolution.

        // Regular 1080p subtitle size, scaled linearly by frame height.
        double scaled = 40.0 * (height / 1080.0);
        return (int)Math.Round(Math.Clamp(scaled, 20.0, 96.0));
    }

    private static IReadOnlyList<string> LineTexts(
        string scenario, string language, DeterministicRandom rng, int frame)
    {
        IReadOnlyList<string> corpus = FixtureCorpus.ForLanguage(language);
        int primaryIndex = rng.NextInt(corpus.Count);
        string primary = corpus[primaryIndex];

        switch (scenario)
        {
            case "typewriter":
            {
                // Same line at increasing prefix lengths; last frame is complete.
                Rune[] runes = primary.EnumerateRunes().ToArray();
                int take = (int)Math.Ceiling(runes.Length * (frame + 1) / (double)DefaultSequenceFrames);
                take = Math.Clamp(take, 1, runes.Length);
                var builder = new StringBuilder();
                for (int i = 0; i < take; i++)
                    builder.Append(runes[i].ToString());
                return new[] { builder.ToString() };
            }
            case "small-text":
            case "moving":
                return new[] { primary };
            default:
            {
                // Two-line dialogue for clear / outlined / drop-shadow.
                int secondIndex = rng.NextInt(corpus.Count);
                if (secondIndex == primaryIndex)
                    secondIndex = (primaryIndex + 1) % corpus.Count;
                return new[] { primary, corpus[secondIndex] };
            }
        }
    }

    private static BoundingBox ToIntegerBox(RectangleF rect, int width, int height)
    {
        int x = (int)Math.Floor(rect.X);
        int y = (int)Math.Floor(rect.Y);
        int right = (int)Math.Ceiling(rect.Right);
        int bottom = (int)Math.Ceiling(rect.Bottom);
        x = Math.Clamp(x, 0, width);
        y = Math.Clamp(y, 0, height);
        right = Math.Clamp(right, x, width);
        bottom = Math.Clamp(bottom, y, height);
        return new BoundingBox(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    // ---- Argument helpers ----

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ValidateScenarios(IReadOnlyList<string> scenarios)
    {
        if (scenarios.Count == 0)
            return "No scenarios selected.";
        foreach (string scenario in scenarios)
            if (Array.IndexOf(AllScenarios, scenario) < 0)
                return $"Unknown scenario '{scenario}'. Known: {string.Join(", ", AllScenarios)}.";
        return null;
    }

    private static string? ValidateLanguages(IReadOnlyList<string> languages)
    {
        if (languages.Count == 0)
            return "No languages selected.";
        foreach (string language in languages)
            if (!FixtureCorpus.IsKnownLanguage(language))
                return $"Unknown language '{language}'. Known: {string.Join(", ", FixtureCorpus.Languages)}.";
        return null;
    }

    private static ulong SeedFor(int seed, string scenario, string language, int width, int height, int frame)
    {
        // Stable, OS-independent mix (FNV-1a over the tuple, folded into the seed).
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            void Mix(string text)
            {
                foreach (char c in text)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
                hash ^= '|';
                hash *= 1099511628211UL;
            }
            Mix(seed.ToString(CultureInfo.InvariantCulture));
            Mix(scenario);
            Mix(language);
            Mix(width.ToString(CultureInfo.InvariantCulture));
            Mix(height.ToString(CultureInfo.InvariantCulture));
            Mix(frame.ToString(CultureInfo.InvariantCulture));
            return hash;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only; leftover temp files must not fail the check.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Small SplitMix64 PRNG: fully deterministic and OS-independent so seed-driven
/// selection and jitter reproduce byte-for-byte across hosts.
/// </summary>
internal sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        return (int)(NextUInt64() % (ulong)maxExclusive);
    }
}
