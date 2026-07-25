using Windows.Media.Ocr;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>Where recognition for a language would come from, if anywhere.</summary>
public enum OcrLanguageSource
{
    /// <summary>Windows has a recognizer installed for it — nothing to download.</summary>
    WindowsRecognizer,

    /// <summary>An OCR model package this application manages is installed for it.</summary>
    LocalModel,

    /// <summary>Neither is present, so choosing this language cannot produce text.</summary>
    NotInstalled,
}

/// <summary>
/// What a language resolves to. <paramref name="RecognizerTag"/> is the exact tag that would run,
/// which is not always the requested code: Windows may hold <c>en-US</c> for a request of <c>en</c>.
/// </summary>
public sealed record OcrLanguageStatus(
    string Code,
    OcrLanguageSource Source,
    string? RecognizerTag);

public interface IOcrLanguageAvailability
{
    /// <summary>Every recognizer tag Windows currently holds, e.g. <c>["en-US", "zh-Hans-CN"]</c>.</summary>
    IReadOnlyList<string> WindowsRecognizerTags { get; }

    /// <summary>Resolves one profile language code. Never throws; an unknown code is NotInstalled.</summary>
    OcrLanguageStatus StatusFor(string languageCode);
}

/// <summary>
/// Answers "can this machine read that language?" before the user commits to it.
///
/// This exists because the failure it prevents is invisible. Windows only ships recognizers for the
/// languages in the account's language list, so a Chinese Windows reading a Japanese game has no
/// Japanese recognizer — and the source-language picker offers all 28 catalog languages regardless.
/// Picking one Windows cannot read produces either a hard failure deep in the run, or (with "auto")
/// fluent nonsense from whatever recognizer Windows substitutes.
///
/// Locally managed OCR model packages are consulted through <paramref name="installedModelLanguages"/>.
/// Until that catalog ships, the caller supplies an empty set and every language Windows lacks is
/// reported NotInstalled — deliberately, rather than claiming a download that does not yet exist.
/// </summary>
public sealed class WindowsOcrLanguageAvailability(
    Func<IReadOnlyList<string>>? windowsRecognizerTags = null,
    Func<IReadOnlyList<string>>? installedModelLanguages = null) : IOcrLanguageAvailability
{
    private readonly Func<IReadOnlyList<string>> _windowsTags = windowsRecognizerTags ??
        (() => [.. OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag)]);

    private readonly Func<IReadOnlyList<string>> _modelLanguages = installedModelLanguages ?? (() => []);

    public IReadOnlyList<string> WindowsRecognizerTags => _windowsTags();

    public OcrLanguageStatus StatusFor(string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        string code = languageCode.Trim();

        string? windows = _windowsTags().FirstOrDefault(tag => Matches(code, tag));
        if (windows is not null)
        {
            return new OcrLanguageStatus(code, OcrLanguageSource.WindowsRecognizer, windows);
        }

        string? model = _modelLanguages().FirstOrDefault(tag => Matches(code, tag));
        return model is not null
            ? new OcrLanguageStatus(code, OcrLanguageSource.LocalModel, model)
            : new OcrLanguageStatus(code, OcrLanguageSource.NotInstalled, null);
    }

    /// <summary>
    /// Compares a profile language code against a recognizer tag on primary language plus script.
    /// Region is ignored — <c>en</c> is served by <c>en-US</c> — but script is not: <c>zh-Hans</c>
    /// and <c>zh-Hant</c> are different writing systems and a recognizer for one cannot read the other.
    /// </summary>
    internal static bool Matches(string languageCode, string recognizerTag)
    {
        (string leftLanguage, string? leftScript) = Split(languageCode);
        (string rightLanguage, string? rightScript) = Split(recognizerTag);
        if (!string.Equals(leftLanguage, rightLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return leftScript is null ||
            rightScript is null ||
            string.Equals(leftScript, rightScript, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the primary language and the script from a BCP-47 tag. A script subtag is four letters
    /// ("Hans"); a two-letter or three-digit subtag is a region and carries no bearing on legibility.
    /// </summary>
    private static (string Language, string? Script) Split(string tag)
    {
        string[] parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return (string.Empty, null);
        }

        string? script = parts
            .Skip(1)
            .FirstOrDefault(part => part.Length == 4 && part.All(char.IsAsciiLetter));
        return (parts[0], script);
    }
}
