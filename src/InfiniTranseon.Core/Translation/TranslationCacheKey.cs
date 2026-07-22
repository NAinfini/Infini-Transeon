using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InfiniTranseon.Core.Translation;

public sealed record TranslationCacheKey(
    string ProviderId,
    string ModelId,
    string SourceLanguage,
    string TargetLanguage,
    string NormalizedSource,
    string StyleVersion,
    string PromptVersion,
    string GlossaryVersion,
    string ProfilePolicyVersion,
    string? ContextDigest)
{
    public static TranslationCacheKey Create(
        string providerId,
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string source,
        string styleVersion,
        string promptVersion,
        string glossaryVersion,
        string profilePolicyVersion,
        string? contextWhenRelevant)
    {
        foreach (string value in new[]
        {
            providerId, modelId, sourceLanguage, targetLanguage, source,
            styleVersion, promptVersion, glossaryVersion, profilePolicyVersion,
        }) ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new TranslationCacheKey(
            providerId,
            modelId,
            sourceLanguage,
            targetLanguage,
            NormalizeSource(source),
            styleVersion,
            promptVersion,
            glossaryVersion,
            profilePolicyVersion,
            contextWhenRelevant is null ? null : Digest(contextWhenRelevant.Normalize(NormalizationForm.FormC)));
    }

    public byte[] ToDigest()
    {
        string canonical = string.Join('\u001f', new[]
        {
            ProviderId, ModelId, SourceLanguage, TargetLanguage, NormalizedSource,
            StyleVersion, PromptVersion, GlossaryVersion, ProfilePolicyVersion,
            ContextDigest ?? string.Empty,
        });
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    public static string NormalizeSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string normalized = source.Normalize(NormalizationForm.FormC).Trim();
        var builder = new StringBuilder(normalized.Length);
        bool whitespace = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                whitespace = builder.Length > 0;
                continue;
            }
            if (whitespace) builder.Append(' ');
            builder.Append(rune.ToString());
            whitespace = false;
        }
        return builder.ToString();
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
