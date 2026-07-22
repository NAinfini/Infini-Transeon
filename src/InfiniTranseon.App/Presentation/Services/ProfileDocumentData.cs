using System.Text.Json;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Reads and writes the App-owned presentation data carried in a <see cref="ProfileDocument"/>'s
/// JSON extension data: the display resolution hint and the profile's glossary. Storing the glossary
/// here keeps it inside the single profile document (persisted by the profile repository) without a
/// separate store, exactly where the runtime translation factory expects per-profile glossary data.
/// </summary>
internal static class ProfileDocumentData
{
    private const string ResolutionKey = "uiResolution";
    private const string GlossaryKey = "glossary";

    // Serialized shape of a glossary entry inside the profile document (camelCase, stable order).
    private sealed record GlossaryDto(
        string Source,
        string Target,
        string Scope,
        bool CaseSensitive,
        bool Protected,
        string Notes);

    public static string ReadResolution(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.ExtensionData.TryGetValue(ResolutionKey, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;
    }

    public static ProfileDocument WithResolution(ProfileDocument document, string resolution)
    {
        ArgumentNullException.ThrowIfNull(document);
        Dictionary<string, JsonElement> extension = Clone(document.ExtensionData);
        extension[ResolutionKey] = JsonSerializer.SerializeToElement(resolution);
        return document with { ExtensionData = extension };
    }

    public static IReadOnlyList<GlossaryEntry> ReadGlossary(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.ExtensionData.TryGetValue(GlossaryKey, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        GlossaryDto[] entries = element.Deserialize<GlossaryDto[]>() ?? [];
        return entries
            .Select(entry => new GlossaryEntry(
                entry.Source, entry.Target, entry.Scope, entry.CaseSensitive, entry.Protected, entry.Notes))
            .ToArray();
    }

    public static ProfileDocument WithGlossary(ProfileDocument document, IReadOnlyList<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entries);
        GlossaryDto[] dtos = entries
            .Select(entry => new GlossaryDto(
                entry.SourceTerm, entry.TargetTerm, entry.Scope, entry.CaseSensitive, entry.Protected, entry.Notes))
            .ToArray();
        Dictionary<string, JsonElement> extension = Clone(document.ExtensionData);
        extension[GlossaryKey] = JsonSerializer.SerializeToElement(dtos);
        return document with { ExtensionData = extension };
    }

    private static Dictionary<string, JsonElement> Clone(IReadOnlyDictionary<string, JsonElement> source) =>
        new(source, StringComparer.Ordinal);
}
