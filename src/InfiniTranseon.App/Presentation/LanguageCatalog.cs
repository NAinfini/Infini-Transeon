using System.Globalization;

namespace InfiniTranseon.App.Presentation;

/// <summary>
/// A canonical profile language plus localized, searchable presentation metadata.
/// Provider-specific aliases are applied later at the provider boundary.
/// </summary>
public sealed record LanguageOption(
    string Code,
    string DisplayName,
    string SearchText);

/// <summary>
/// Shared language choices for profile editors. The catalog intentionally covers common game
/// languages rather than claiming every translation provider supports every BCP-47 identifier.
/// </summary>
public static class LanguageCatalog
{
    private sealed record Definition(
        string Code,
        string EnglishName,
        string ChineseName,
        string NativeName,
        string Aliases = "");

    private static readonly IReadOnlyList<Definition> Definitions =
    [
        new("zh-Hans", "Chinese (Simplified)", "简体中文", "简体中文", "chinese simplified mandarin 中文 汉语"),
        new("zh-Hant", "Chinese (Traditional)", "繁体中文", "繁體中文", "chinese traditional mandarin 中文 漢語"),
        new("en", "English", "英语", "English", "英文"),
        new("ja", "Japanese", "日语", "日本語", "japanese jp 日文"),
        new("ko", "Korean", "韩语", "한국어", "korean kr 朝鲜语"),
        new("fr", "French", "法语", "Français", "french"),
        new("de", "German", "德语", "Deutsch", "german"),
        new("es", "Spanish", "西班牙语", "Español", "spanish"),
        new("pt", "Portuguese", "葡萄牙语", "Português", "portuguese"),
        new("it", "Italian", "意大利语", "Italiano", "italian"),
        new("ru", "Russian", "俄语", "Русский", "russian"),
        new("uk", "Ukrainian", "乌克兰语", "Українська", "ukrainian"),
        new("ar", "Arabic", "阿拉伯语", "العربية", "arabic"),
        new("th", "Thai", "泰语", "ไทย", "thai"),
        new("vi", "Vietnamese", "越南语", "Tiếng Việt", "vietnamese"),
        new("id", "Indonesian", "印度尼西亚语", "Bahasa Indonesia", "indonesian bahasa"),
        new("ms", "Malay", "马来语", "Bahasa Melayu", "malay bahasa"),
        new("tr", "Turkish", "土耳其语", "Türkçe", "turkish"),
        new("pl", "Polish", "波兰语", "Polski", "polish"),
        new("nl", "Dutch", "荷兰语", "Nederlands", "dutch"),
        new("cs", "Czech", "捷克语", "Čeština", "czech"),
        new("ro", "Romanian", "罗马尼亚语", "Română", "romanian"),
        new("sv", "Swedish", "瑞典语", "Svenska", "swedish"),
        new("da", "Danish", "丹麦语", "Dansk", "danish"),
        new("fi", "Finnish", "芬兰语", "Suomi", "finnish"),
        new("no", "Norwegian", "挪威语", "Norsk", "norwegian"),
        new("hu", "Hungarian", "匈牙利语", "Magyar", "hungarian"),
        new("el", "Greek", "希腊语", "Ελληνικά", "greek"),
    ];

    public static IReadOnlyList<LanguageOption> CreateSourceOptions(string? uiLanguage = null) =>
        [CreateAutoDetect(uiLanguage), .. CreateTargetOptions(uiLanguage)];

    public static IReadOnlyList<LanguageOption> CreateTargetOptions(string? uiLanguage = null)
    {
        bool useChinese = IsChineseUi(uiLanguage);
        return Definitions.Select(definition => CreateOption(definition, useChinese)).ToArray();
    }

    public static IReadOnlyList<LanguageOption> Filter(
        IReadOnlyList<LanguageOption> options,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(options);
        string normalized = query?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return options;
        }

        return options
            .Where(option => option.SearchText.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static LanguageOption ResolveOrCreate(
        IReadOnlyList<LanguageOption> options,
        string code)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        string normalized = code.Trim();
        return options.FirstOrDefault(option =>
                   string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase))
            ?? new LanguageOption(normalized, normalized, normalized);
    }

    private static LanguageOption CreateAutoDetect(string? uiLanguage)
    {
        string displayName = IsChineseUi(uiLanguage) ? "自动检测" : "Auto-detect";
        return new LanguageOption(
            "auto",
            $"{displayName} · auto",
            $"auto autodetect auto-detect detect automatic 自动检测 自动识别 {displayName}");
    }

    private static LanguageOption CreateOption(Definition definition, bool useChinese)
    {
        string localizedName = useChinese ? definition.ChineseName : definition.EnglishName;
        string displayName = string.Equals(localizedName, definition.NativeName, StringComparison.Ordinal)
            ? $"{localizedName} · {definition.Code}"
            : $"{localizedName} / {definition.NativeName} · {definition.Code}";
        string searchText = string.Join(' ',
            definition.Code,
            definition.EnglishName,
            definition.ChineseName,
            definition.NativeName,
            definition.Aliases);
        return new LanguageOption(definition.Code, displayName, searchText);
    }

    private static bool IsChineseUi(string? uiLanguage)
    {
        string language = string.IsNullOrWhiteSpace(uiLanguage)
            ? CultureInfo.CurrentUICulture.Name
            : uiLanguage;
        return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }
}
