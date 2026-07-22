using System.Security.Cryptography;
using System.Text;
using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation;

public sealed record ProtectedGlossaryText(
    string Text,
    IReadOnlyDictionary<string, string> Replacements);

public static class GlossaryProcessor
{
    public static ProtectedGlossaryText Protect(
        string source,
        IReadOnlyList<GlossaryEntry> glossary)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(glossary);
        string text = source;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (GlossaryEntry entry in glossary
                     .Where(item => !string.IsNullOrEmpty(item.Source))
                     .OrderByDescending(item => item.Source.Length))
        {
            if (!text.Contains(entry.Source, StringComparison.Ordinal)) continue;
            string digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(entry.Source + "\u001f" + entry.Target)))[..12];
            string placeholder = $"__ITG_{replacements.Count:D3}_{digest}__";
            text = text.Replace(entry.Source, placeholder, StringComparison.Ordinal);
            replacements.Add(placeholder, entry.Target);
        }
        return new ProtectedGlossaryText(text, replacements);
    }

    public static string Restore(string translated, ProtectedGlossaryText protectedText)
    {
        ArgumentNullException.ThrowIfNull(translated);
        ArgumentNullException.ThrowIfNull(protectedText);
        string result = translated;
        foreach ((string placeholder, string replacement) in protectedText.Replacements)
        {
            if (!result.Contains(placeholder, StringComparison.Ordinal))
                throw new InvalidDataException("A glossary placeholder was changed or removed by the provider.");
            result = result.Replace(placeholder, replacement, StringComparison.Ordinal);
        }
        return result;
    }
}
