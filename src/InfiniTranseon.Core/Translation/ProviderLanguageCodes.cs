namespace InfiniTranseon.Core.Translation;

/// <summary>
/// Converts the profile's BCP-47 language identifiers at provider boundaries. Profiles and cache
/// keys keep canonical identifiers; provider-specific aliases never leak back into persisted data.
/// Unknown identifiers pass through unchanged so providers can add languages without an app update.
/// </summary>
public static class ProviderLanguageCodes
{
    public static string ForBaidu(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        string normalized = language.Trim().ToLowerInvariant();
        return normalized switch
        {
            "zh" or "zh-cn" or "zh-sg" or "zh-hans" => "zh",
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" or "cht" => "cht",
            "ja" or "ja-jp" or "jp" => "jp",
            "ko" or "ko-kr" or "kor" => "kor",
            "fr" or "fr-fr" or "fra" => "fra",
            "es" or "es-es" or "spa" => "spa",
            "ar" or "ara" => "ara",
            "bg" or "bul" => "bul",
            "et" or "est" => "est",
            "da" or "dan" => "dan",
            "fi" or "fin" => "fin",
            "ro" or "rom" => "rom",
            "sl" or "slo" => "slo",
            "sv" or "swe" => "swe",
            "vi" or "vie" => "vie",
            _ => normalized,
        };
    }

    public static string ForAlibaba(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        string normalized = language.Trim().ToLowerInvariant();
        if (normalized is "zh" or "zh-cn" or "zh-sg" or "zh-hans") return "zh";
        if (normalized is "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant") return "zh-tw";
        if (normalized == "auto") return normalized;
        int separator = normalized.IndexOf('-');
        return separator < 0 ? normalized : normalized[..separator];
    }

    public static string ForYoudao(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        string normalized = language.Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => "auto",
            "zh" or "zh-cn" or "zh-sg" or "zh-hans" => "zh-CHS",
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "zh-CHT",
            "en" or "en-us" or "en-gb" => "en",
            "ja" or "ja-jp" or "jp" => "ja",
            "ko" or "ko-kr" or "kor" => "ko",
            "fr" or "fr-fr" => "fr",
            "es" or "es-es" => "es",
            "pt" or "pt-br" or "pt-pt" => "pt",
            "ru" or "ru-ru" => "ru",
            "vi" or "vi-vn" => "vi",
            "de" or "de-de" => "de",
            "ar" => "ar",
            "id" or "id-id" => "id",
            "it" or "it-it" => "it",
            _ => normalized,
        };
    }
}
