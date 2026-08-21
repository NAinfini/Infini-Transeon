using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Ocr;

internal static class CloudOcrLanguageMapper
{
    public static string? GoogleVisionHint(string recognitionLanguage) =>
        OcrRecognitionLanguage.IsAutomatic(recognitionLanguage) ? null : recognitionLanguage;

    public static string? AzureVisionLanguage(string recognitionLanguage)
    {
        if (OcrRecognitionLanguage.IsAutomatic(recognitionLanguage)) return null;
        string primary = PrimarySubtag(recognitionLanguage);
        if (primary != "zh") return primary;
        return HasTraditionalChineseScriptOrRegion(recognitionLanguage) ? "zh-Hant" : "zh-Hans";
    }

    public static string BaiduLanguageType(string recognitionLanguage)
    {
        if (OcrRecognitionLanguage.IsAutomatic(recognitionLanguage)) return "auto_detect";
        return PrimarySubtag(recognitionLanguage) switch
        {
            "zh" => "CHN_ENG",
            "en" => "ENG",
            "pt" => "POR",
            "fr" => "FRE",
            "de" => "GER",
            "it" => "ITA",
            "es" => "SPA",
            "ru" => "RUS",
            "ja" => "JAP",
            "ko" => "KOR",
            _ => throw Unsupported("baidu", recognitionLanguage),
        };
    }

    public static string TencentLanguageType(string recognitionLanguage)
    {
        if (OcrRecognitionLanguage.IsAutomatic(recognitionLanguage)) return "auto";
        string primary = PrimarySubtag(recognitionLanguage);
        return primary switch
        {
            "zh" => HasTraditionalChineseScriptOrRegion(recognitionLanguage) ? "zh_rare" : "zh",
            // GeneralBasicOCR has no dedicated English selector; its documented automatic mode
            // recognizes English, so this is the API's explicit representation of English input.
            "en" => "auto",
            "ja" => "jap",
            "ko" => "kor",
            "es" => "spa",
            "fr" => "fre",
            "de" => "ger",
            "pt" => "por",
            "vi" => "vie",
            "ms" => "may",
            "ru" => "rus",
            "it" => "ita",
            "nl" => "hol",
            "sv" => "swe",
            "fi" => "fin",
            "da" => "dan",
            "no" => "nor",
            "nb" => "nor",
            "nn" => "nor",
            "hu" => "hun",
            "th" => "tha",
            "hi" => "hi",
            "ar" => "ara",
            _ => throw Unsupported("tencent", recognitionLanguage),
        };
    }

    private static string PrimarySubtag(string recognitionLanguage)
    {
        int separator = recognitionLanguage.IndexOf('-');
        return (separator < 0 ? recognitionLanguage : recognitionLanguage[..separator])
            .ToLowerInvariant();
    }

    private static bool HasTraditionalChineseScriptOrRegion(string recognitionLanguage) =>
        recognitionLanguage.Split('-').Skip(1).Any(part =>
            string.Equals(part, "Hant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "TW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "HK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "MO", StringComparison.OrdinalIgnoreCase));

    private static OcrRoutingException Unsupported(string provider, string language) => new(
        $"ocr.{provider}.languageUnsupported",
        $"Cloud OCR provider '{provider}' does not support recognition language '{language}'.");
}
