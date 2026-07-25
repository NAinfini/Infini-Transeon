using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Versioned local store for user-authored declarative REST translation adapters.
/// Definitions are validated by <see cref="DeclarativeRestAdapterDefinition"/> during
/// deserialization and never contain credential values.
/// </summary>
public sealed class CustomRestAdapterStore
{
    private const int MaximumDefinitions = 64;
    private const long MaximumFileBytes = 1 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _path;

    public CustomRestAdapterStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<DeclarativeRestAdapterDefinition> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var file = new FileInfo(_path);
        if (file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("REST adapter store exceeds the 1 MiB safety limit.");
        }

        AdapterStoreDocument document =
            JsonSerializer.Deserialize<AdapterStoreDocument>(
                File.ReadAllText(_path),
                JsonOptions) ??
            throw new InvalidDataException("REST adapter store is empty or malformed.");
        ValidateDocument(document);
        return document.Adapters.Select(adapter => adapter.ToDefinition()).ToArray();
    }

    public DeclarativeRestAdapterDefinition Import(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.CanSeek && source.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("REST adapter file exceeds the 1 MiB safety limit.");
        }

        AdapterDefinitionDocument serialized =
            JsonSerializer.Deserialize<AdapterDefinitionDocument>(source, JsonOptions) ??
            throw new InvalidDataException("REST adapter file is empty or malformed.");
        DeclarativeRestAdapterDefinition definition = serialized.ToDefinition();
        ValidateDefinitionId(definition.Id);

        List<DeclarativeRestAdapterDefinition> current = Load().ToList();
        if (current.Any(existing =>
                string.Equals(existing.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A REST adapter with id '{definition.Id}' already exists.");
        }
        if (current.Count >= MaximumDefinitions)
        {
            throw new InvalidOperationException(
                $"No more than {MaximumDefinitions} custom REST adapters are supported.");
        }

        current.Add(definition);
        Save(current);
        return definition;
    }

    public void Remove(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        List<DeclarativeRestAdapterDefinition> current = Load().ToList();
        int removed = current.RemoveAll(adapter =>
            string.Equals(adapter.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new KeyNotFoundException($"REST adapter '{providerId}' was not found.");
        }
        Save(current);
    }

    public IReadOnlyList<CatalogProvider> GetCatalogProviders() =>
        Load().Select(ToCatalogProvider).ToArray();

    public static CatalogProvider ToCatalogProvider(
        DeclarativeRestAdapterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        CatalogCredential[] credentials = definition.CredentialReferences
            .Select(reference => new CatalogCredential(
                reference,
                reference,
                DeclarativeRestProvider.CreateBinding(definition, reference)))
            .ToArray();
        return new CatalogProvider(
            definition.Id,
            definition.DisplayName,
            "REST · custom",
            credentials,
            $"{definition.Method} · {definition.Endpoint.Host}")
        {
            IsCustom = true,
        };
    }

    private void Save(IReadOnlyList<DeclarativeRestAdapterDefinition> adapters)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(
                new AdapterStoreDocument(
                    1,
                    adapters.Select(AdapterDefinitionDocument.FromDefinition).ToArray()),
                JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static void ValidateDocument(AdapterStoreDocument document)
    {
        if (document.SchemaVersion != 1 ||
            document.Adapters.Count > MaximumDefinitions)
        {
            throw new InvalidDataException("REST adapter store version or count is unsupported.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AdapterDefinitionDocument adapter in document.Adapters)
        {
            DeclarativeRestAdapterDefinition definition = adapter.ToDefinition();
            ValidateDefinitionId(definition.Id);
            if (!ids.Add(definition.Id))
            {
                throw new InvalidDataException(
                    $"REST adapter store contains duplicate id '{definition.Id}'.");
            }
        }
    }

    private static void ValidateDefinitionId(string id)
    {
        if (ProviderCatalog.Default.Any(provider =>
                string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"REST adapter id '{id}' is reserved by a built-in provider.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record AdapterStoreDocument(
        int SchemaVersion,
        IReadOnlyList<AdapterDefinitionDocument> Adapters);

    private sealed record AdapterDefinitionDocument(
        int SchemaVersion,
        string Id,
        string DisplayName,
        Uri Endpoint,
        RestHttpMethod Method,
        IReadOnlyDictionary<string, string> Headers,
        string? BodyTemplate,
        string ResponseTextJsonPointer,
        string? ResponseErrorJsonPointer,
        IReadOnlyList<string> CredentialReferences,
        RestBodyFormat BodyFormat = RestBodyFormat.JsonUtf8,
        RestResponseFormat ResponseFormat = RestResponseFormat.Json,
        RestResponseLimits? ResponseLimits = null,
        IReadOnlyDictionary<int, RestStatusMapping>? StatusMappings = null,
        string SseDoneMarker = "[DONE]",
        RestLanguageCodeStyle LanguageCodeStyle = RestLanguageCodeStyle.Bcp47)
    {
        public DeclarativeRestAdapterDefinition ToDefinition() =>
            new(
                SchemaVersion,
                Id,
                DisplayName,
                Endpoint,
                Method,
                Headers,
                BodyTemplate,
                ResponseTextJsonPointer,
                ResponseErrorJsonPointer,
                CredentialReferences,
                BodyFormat,
                ResponseFormat,
                ResponseLimits,
                StatusMappings,
                SseDoneMarker,
                LanguageCodeStyle);

        public static AdapterDefinitionDocument FromDefinition(
            DeclarativeRestAdapterDefinition definition) =>
            new(
                definition.SchemaVersion,
                definition.Id,
                definition.DisplayName,
                definition.Endpoint,
                definition.Method,
                definition.Headers,
                definition.BodyTemplate,
                definition.ResponseTextJsonPointer,
                definition.ResponseErrorJsonPointer,
                definition.CredentialReferences,
                definition.BodyFormat,
                definition.ResponseFormat,
                definition.ResponseLimits,
                definition.StatusMappings,
                definition.SseDoneMarker,
                definition.LanguageCodeStyle);
    }
}
