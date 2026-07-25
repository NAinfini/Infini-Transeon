using System.Diagnostics.CodeAnalysis;

namespace InfiniTranseon.Core.Ocr;

/// <summary>
/// The three ONNX files one recognition language needs. Classification is optional: without it the
/// pipeline simply never flips a crop by 180°.
/// </summary>
public sealed record PaddleOcrModelSet(
    string LanguageTag,
    string DetectionModelPath,
    string RecognitionModelPath,
    string? ClassificationModelPath);

public interface IPaddleOcrModelCatalog
{
    /// <summary>Normalized tags (<c>ja</c>, <c>zh-hans</c>, <c>en</c>) that are ready to run offline.</summary>
    IReadOnlyList<string> InstalledLanguageTags { get; }

    bool TryResolve(string? languageTag, [NotNullWhen(true)] out PaddleOcrModelSet? modelSet);
}

/// <summary>
/// Finds installed PP-OCR packages inside the managed model root that
/// <see cref="Translation.Local.ModelPackageService"/> owns, which lays packages out as
/// <c>packages/{modelId}/{version}/{relativePath}</c>.
///
/// Detection and angle classification are language-independent, so they ship once as
/// <c>ppocr-v4-base</c> and every language adds only its recognizer, <c>ppocr-v4-rec-{tag}</c>.
/// Duplicating the 5 MB detector into each language package would be simpler to resolve and worse
/// for every user who reads more than one language.
///
/// When several versions of a package are present — which happens for as long as it takes a silent
/// update to retire the previous copy — the newest wins, so an update takes effect the moment its
/// directory is published and never leaves the app running the older files.
/// </summary>
public sealed class ManagedPaddleOcrModelCatalog : IPaddleOcrModelCatalog
{
    public const string BaseModelId = "ppocr-v4-base";
    public const string RecognitionModelIdPrefix = "ppocr-v4-rec-";

    private const string PackagesDirectory = "packages";
    private const string DetectionDirectory = "det";
    private const string ClassificationDirectory = "cls";
    private const string RecognitionDirectory = "rec";

    private readonly string _packagesRoot;

    public ManagedPaddleOcrModelCatalog(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _packagesRoot = Path.Combine(Path.GetFullPath(managedRoot), PackagesDirectory);
    }

    public IReadOnlyList<string> InstalledLanguageTags
    {
        get
        {
            if (!Directory.Exists(_packagesRoot) || ResolveNewestVersionDirectory(BaseModelId) is null)
            {
                return [];
            }

            var tags = new List<string>();
            foreach (string modelDirectory in Directory.EnumerateDirectories(_packagesRoot))
            {
                string modelId = Path.GetFileName(modelDirectory);
                if (!modelId.StartsWith(RecognitionModelIdPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string tag = modelId[RecognitionModelIdPrefix.Length..];
                if (tag.Length != 0 &&
                    ResolveNewestVersionDirectory(modelId) is { } version &&
                    FindSingleModel(version, RecognitionDirectory) is not null)
                {
                    tags.Add(tag);
                }
            }

            tags.Sort(StringComparer.Ordinal);
            return tags;
        }
    }

    public bool TryResolve(string? languageTag, [NotNullWhen(true)] out PaddleOcrModelSet? modelSet)
    {
        modelSet = null;
        if (string.IsNullOrWhiteSpace(languageTag) ||
            string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ResolveNewestVersionDirectory(BaseModelId) is not { } baseDirectory ||
            FindSingleModel(baseDirectory, DetectionDirectory) is not { } detection)
        {
            return false;
        }

        if (MatchInstalledTag(NormalizeLanguageTag(languageTag)) is not { } tag ||
            ResolveNewestVersionDirectory(RecognitionModelIdPrefix + tag) is not { } recognitionDirectory ||
            FindSingleModel(recognitionDirectory, RecognitionDirectory) is not { } recognition)
        {
            return false;
        }

        modelSet = new PaddleOcrModelSet(
            tag,
            detection,
            recognition,
            FindSingleModel(baseDirectory, ClassificationDirectory));
        return true;
    }

    /// <summary>
    /// Reduces a BCP-47 tag to the <c>language</c> or <c>language-script</c> form the packages are
    /// named with. Region is dropped except for Chinese, where it is the only thing that says which
    /// script is meant; the mapping is the usual one and anything unrecognised is left as simplified,
    /// which is what a game shipped without a script subtag overwhelmingly is.
    /// </summary>
    public static string NormalizeLanguageTag(string languageTag)
    {
        string[] parts = languageTag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        string primary = parts[0].ToLowerInvariant();
        string? script = null;
        string? region = null;
        for (int index = 1; index < parts.Length; index++)
        {
            string part = parts[index];
            if (script is null && part.Length == 4 && part.All(char.IsAsciiLetter))
            {
                script = part.ToLowerInvariant();
            }
            else if (region is null && part.Length == 2 && part.All(char.IsAsciiLetter))
            {
                region = part.ToUpperInvariant();
            }
        }

        if (script is null && primary is "zh")
        {
            script = region is "TW" or "HK" or "MO" ? "hant" : "hans";
        }

        return script is null ? primary : $"{primary}-{script}";
    }

    /// <summary>
    /// Exact tag first, then the bare primary language, then any installed variant of it. The last
    /// leg lets a profile that says <c>ja</c> use a package that a future catalog names <c>ja-jpan</c>
    /// without the user being told to reinstall anything.
    /// </summary>
    private string? MatchInstalledTag(string normalized)
    {
        IReadOnlyList<string> installed = InstalledLanguageTags;
        if (installed.Contains(normalized, StringComparer.Ordinal))
        {
            return normalized;
        }

        string primary = normalized.Split('-')[0];
        if (installed.Contains(primary, StringComparer.Ordinal))
        {
            return primary;
        }

        return installed.FirstOrDefault(
            tag => tag.StartsWith(primary + "-", StringComparison.Ordinal));
    }

    private string? ResolveNewestVersionDirectory(string modelId)
    {
        string modelDirectory = Path.Combine(_packagesRoot, modelId);
        if (!Directory.Exists(modelDirectory))
        {
            return null;
        }

        string? newest = null;
        foreach (string candidate in Directory.EnumerateDirectories(modelDirectory))
        {
            if (newest is null ||
                CompareVersions(Path.GetFileName(candidate), Path.GetFileName(newest)) > 0)
            {
                newest = candidate;
            }
        }

        return newest;
    }

    /// <summary>Dotted numeric segments compare numerically; anything else falls back to ordinal.</summary>
    public static int CompareVersions(string left, string right)
    {
        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');
        int count = Math.Max(leftParts.Length, rightParts.Length);
        for (int index = 0; index < count; index++)
        {
            int leftValue = 0;
            int rightValue = 0;
            bool leftNumeric = index < leftParts.Length && int.TryParse(leftParts[index], out leftValue);
            bool rightNumeric = index < rightParts.Length && int.TryParse(rightParts[index], out rightValue);
            if (!leftNumeric || !rightNumeric)
            {
                return string.CompareOrdinal(left, right);
            }

            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    /// <summary>
    /// A package directory holds exactly the files the signed catalog listed, so more than one model
    /// in a slot means the directory was tampered with or a partial install was published. That is
    /// reported rather than resolved by picking one.
    /// </summary>
    private static string? FindSingleModel(string versionDirectory, string slot)
    {
        string directory = Path.Combine(versionDirectory, slot);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string[] models = Directory.GetFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly);
        return models.Length switch
        {
            0 => null,
            1 => models[0],
            _ => throw new InvalidDataException(
                $"The OCR package directory '{directory}' holds {models.Length} models where the " +
                "catalog declares one."),
        };
    }
}
