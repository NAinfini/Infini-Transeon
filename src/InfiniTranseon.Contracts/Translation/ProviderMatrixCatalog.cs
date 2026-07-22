using System.Reflection;
using System.Text.Json;

namespace InfiniTranseon.Contracts.Translation;

public sealed record ProviderCapabilitySet(bool Streaming, bool Context, bool Glossary);

public sealed record ProviderMatrixEntry(
    string Id,
    string DisplayName,
    string Kind,
    IReadOnlyList<string> Regions,
    string Auth,
    string ApiVersion,
    string AdapterFamily,
    ProviderCapabilitySet Capabilities,
    string TestStrategy);

public sealed record ProviderMatrixDocument(
    int SchemaVersion,
    string MatrixVersion,
    IReadOnlyList<ProviderMatrixEntry> Providers);

public static class ProviderMatrixCatalog
{
    private const string ResourceName =
        "InfiniTranseon.Contracts.Translation.provider-matrix.json";
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "translation", "largeLanguageModel", "cloudOcr",
    };
    private static readonly HashSet<string> AdapterFamilies = new(StringComparer.Ordinal)
    {
        "declarative-rest",
        "openai-compatible",
        "handwritten-oauth",
        "handwritten-query-template",
        "handwritten-signing",
        "handwritten-sse",
        "handwritten-binary-json",
        "handwritten-token-form",
    };

    public static ProviderMatrixDocument LoadBuiltIn()
    {
        Assembly assembly = typeof(ProviderMatrixCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException("The built-in provider matrix resource is missing.");
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException("The built-in provider matrix exceeds its size limit.");
        ProviderMatrixDocument document = JsonSerializer.Deserialize<ProviderMatrixDocument>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
            throw new InvalidDataException("The built-in provider matrix is empty.");
        Validate(document);
        return document;
    }

    private static void Validate(ProviderMatrixDocument document)
    {
        if (document.SchemaVersion != 1) throw new InvalidDataException("Unknown provider matrix schema.");
        ArgumentException.ThrowIfNullOrWhiteSpace(document.MatrixVersion);
        if (document.Providers.Count is < 1 or > 128)
            throw new InvalidDataException("Provider matrix entry count is invalid.");
        if (document.Providers.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() !=
            document.Providers.Count)
        {
            throw new InvalidDataException("Provider matrix IDs must be unique.");
        }
        foreach (ProviderMatrixEntry provider in document.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || string.IsNullOrWhiteSpace(provider.DisplayName) ||
                !Kinds.Contains(provider.Kind) || !AdapterFamilies.Contains(provider.AdapterFamily) ||
                provider.Regions.Count == 0 || provider.Regions.Any(string.IsNullOrWhiteSpace) ||
                string.IsNullOrWhiteSpace(provider.Auth) || string.IsNullOrWhiteSpace(provider.ApiVersion) ||
                string.IsNullOrWhiteSpace(provider.TestStrategy))
            {
                throw new InvalidDataException($"Provider matrix entry '{provider.Id}' is invalid.");
            }
        }
    }
}
