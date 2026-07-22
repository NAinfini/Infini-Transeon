using System.Text.RegularExpressions;

namespace InfiniTranseon.Core.Diagnostics;

public static partial class LogRedactor
{
    private static readonly string[] SensitiveNames =
    [
        "authorization", "apiKey", "api-key", "secret", "token", "password",
        "sourceText", "translatedText", "translation", "ocrText", "screenshot",
    ];

    public static IReadOnlyDictionary<string, object?> RedactArguments(
        IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.ToDictionary(
            item => item.Key,
            item => IsSensitiveName(item.Key) ? (object?)"[REDACTED]" : RedactValue(item.Value),
            StringComparer.Ordinal);
    }

    public static string RedactText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string redacted = BearerRegex().Replace(value, "$1[REDACTED]");
        redacted = ApiKeyRegex().Replace(redacted, "$1[REDACTED]");
        redacted = WindowsPathRegex().Replace(redacted, "[PATH]");
        return redacted;
    }

    public static bool IsSensitiveName(string name) => SensitiveNames.Any(
        sensitive => name.Contains(sensitive, StringComparison.OrdinalIgnoreCase));

    internal static bool IsStableIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':' or '/');
    }

    private static object? RedactValue(object? value) => value switch
    {
        StatusIdentifier identifier => identifier.Value,
        string => "[REDACTED_TEXT]",
        null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal => value,
        Guid or DateTimeOffset or TimeSpan => value,
        Enum enumeration => enumeration.ToString(),
        _ => "[UNSUPPORTED]",
    };

    [GeneratedRegex("(?i)(Bearer\\s+)[A-Za-z0-9._~+/-]{8,}")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)((?:api[-_ ]?key|secret|token)\\s*[:=]\\s*)[^\\s,;]+")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex("(?i)(?:[A-Z]:\\\\|\\\\\\\\)[^\\r\\n\"']+")]
    private static partial Regex WindowsPathRegex();
}
