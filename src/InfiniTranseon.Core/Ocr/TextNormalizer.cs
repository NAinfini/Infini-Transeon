using System.Security.Cryptography;
using System.Text;

namespace InfiniTranseon.Core.Ocr;

public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string canonical = text.Normalize(NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var output = new StringBuilder(canonical.Length);
        bool pendingSpace = false;
        foreach (char character in canonical)
        {
            if (character == '\n')
            {
                while (output.Length > 0 && output[^1] == ' ') output.Length--;
                if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                pendingSpace = output.Length > 0 && output[^1] != '\n';
            }
            else
            {
                if (pendingSpace) output.Append(' ');
                output.Append(character);
                pendingSpace = false;
            }
        }
        return output.ToString().Trim();
    }

    public static bool ContainsMeaningfulText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Any(char.IsLetterOrDigit);
    }
}

public sealed class SensitiveSourceText
{
    public SensitiveSourceText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public string Value { get; }
    public int Length => Value.Length;
    public string Sha256 { get; }

    public override string ToString() => $"[redacted length={Length} sha256={Sha256[..12]}]";
}

public sealed record TextDiagnosticSummary(int Length, string Sha256);
