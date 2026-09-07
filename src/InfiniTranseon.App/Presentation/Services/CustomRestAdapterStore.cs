using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.App.Presentation.Services;

public sealed record CustomOpenAiCompatibleDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const string ProviderIdPrefix = "llm.custom.";

    public CustomOpenAiCompatibleDefinition(
        int schemaVersion,
        string id,
        string displayName,
        Uri endpoint,
        string model)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Custom OpenAI-compatible provider schema version {schemaVersion} is unsupported.");
        if (!id.StartsWith(ProviderIdPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(id[ProviderIdPrefix.Length..], "N", out _))
            throw new InvalidDataException("Custom OpenAI-compatible provider id is invalid.");

        string normalizedName = displayName?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > 80 || normalizedName.Any(char.IsControl))
            throw new InvalidDataException("Custom provider display name is invalid.");
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(endpoint.IdnHost) || endpoint.AbsoluteUri.Length > 2048 ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidDataException("Custom provider endpoint must be a safe HTTPS URL.");

        string normalizedModel = model?.Trim() ?? string.Empty;
        if (normalizedModel.Length is 0 or > 256 || normalizedModel.Any(char.IsControl))
            throw new InvalidDataException("Custom provider model name is invalid.");

        SchemaVersion = schemaVersion;
        Id = id;
        DisplayName = normalizedName;
        Endpoint = endpoint;
        Model = normalizedModel;
    }

    public int SchemaVersion { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public Uri Endpoint { get; }
    public string Model { get; }
    public string CredentialReference => $"{Id}.api-key";

    public static CustomOpenAiCompatibleDefinition Create(
        string displayName,
        Uri endpoint,
        string model) =>
        new(
            CurrentSchemaVersion,
            $"{ProviderIdPrefix}{Guid.NewGuid():N}",
            displayName,
            endpoint,
            model);
}

/// <summary>
/// Versioned local store for custom translation providers. Definitions never contain credential
/// values; those remain in Windows Credential Manager.
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
        AdapterStoreDocument document = ReadDocument();
        return (document.Adapters ?? []).Select(adapter => adapter.ToDefinition()).ToArray();
    }

    public IReadOnlyList<CustomOpenAiCompatibleDefinition> LoadOpenAiCompatible()
    {
        AdapterStoreDocument document = ReadDocument();
        return (document.OpenAiCompatibleProviders ?? [])
            .Select(provider => provider.ToDefinition())
            .ToArray();
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

        AdapterStoreDocument document = ReadDocument();
        List<DeclarativeRestAdapterDefinition> adapters = (document.Adapters ?? [])
            .Select(adapter => adapter.ToDefinition())
            .ToList();
        IReadOnlyList<CustomOpenAiCompatibleDefinition> openAiProviders =
            (document.OpenAiCompatibleProviders ?? [])
            .Select(provider => provider.ToDefinition())
            .ToArray();
        if (adapters.Any(existing =>
                string.Equals(existing.Id, definition.Id, StringComparison.OrdinalIgnoreCase)) ||
            openAiProviders.Any(existing =>
                string.Equals(existing.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A REST adapter with id '{definition.Id}' already exists.");
        }
        if (adapters.Count + openAiProviders.Count >= MaximumDefinitions)
        {
            throw new InvalidOperationException(
                $"No more than {MaximumDefinitions} custom providers are supported.");
        }

        adapters.Add(definition);
        Save(adapters, openAiProviders);
        return definition;
    }

    public CustomOpenAiCompatibleDefinition AddOpenAiCompatible(
        string displayName,
        Uri endpoint,
        string model)
    {
        CustomOpenAiCompatibleDefinition definition =
            CustomOpenAiCompatibleDefinition.Create(displayName, endpoint, model);
        AdapterStoreDocument document = ReadDocument();
        IReadOnlyList<DeclarativeRestAdapterDefinition> adapters = (document.Adapters ?? [])
            .Select(adapter => adapter.ToDefinition())
            .ToArray();
        List<CustomOpenAiCompatibleDefinition> openAiProviders =
            (document.OpenAiCompatibleProviders ?? [])
            .Select(provider => provider.ToDefinition())
            .ToList();
        if (adapters.Count + openAiProviders.Count >= MaximumDefinitions)
            throw new InvalidOperationException(
                $"No more than {MaximumDefinitions} custom providers are supported.");

        openAiProviders.Add(definition);
        Save(adapters, openAiProviders);
        return definition;
    }

    public void Remove(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        AdapterStoreDocument document = ReadDocument();
        List<DeclarativeRestAdapterDefinition> adapters = (document.Adapters ?? [])
            .Select(adapter => adapter.ToDefinition())
            .ToList();
        List<CustomOpenAiCompatibleDefinition> openAiProviders =
            (document.OpenAiCompatibleProviders ?? [])
            .Select(provider => provider.ToDefinition())
            .ToList();
        int removed = adapters.RemoveAll(adapter =>
            string.Equals(adapter.Id, providerId, StringComparison.OrdinalIgnoreCase));
        removed += openAiProviders.RemoveAll(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new KeyNotFoundException($"Custom provider '{providerId}' was not found.");
        }
        Save(adapters, openAiProviders);
    }

    public IReadOnlyList<CatalogProvider> GetCatalogProviders()
    {
        AdapterStoreDocument document = ReadDocument();
        return (document.Adapters ?? [])
            .Select(adapter => ToCatalogProvider(adapter.ToDefinition()))
            .Concat((document.OpenAiCompatibleProviders ?? [])
                .Select(provider => BuiltInProviderSpecs
                    .CreateCustomOpenAiCompatibleSpec(provider.ToDefinition())
                    .Catalog))
            .ToArray();
    }

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
            "ProviderKindRestCustom",
            credentials,
            "ProviderDetailCustomRest")
        {
            IsCustom = true,
            DetailArguments = [definition.Method, definition.Endpoint.Host],
        };
    }

    private AdapterStoreDocument ReadDocument()
    {
        if (!File.Exists(_path))
            return new AdapterStoreDocument(1, [], []);

        var file = new FileInfo(_path);
        if (file.Length > MaximumFileBytes)
            throw new InvalidDataException("Custom provider store exceeds the 1 MiB safety limit.");

        AdapterStoreDocument document =
            JsonSerializer.Deserialize<AdapterStoreDocument>(File.ReadAllText(_path), JsonOptions) ??
            throw new InvalidDataException("Custom provider store is empty or malformed.");
        ValidateDocument(document);
        return document;
    }

    private void Save(
        IReadOnlyList<DeclarativeRestAdapterDefinition> adapters,
        IReadOnlyList<CustomOpenAiCompatibleDefinition> openAiProviders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(
                new AdapterStoreDocument(
                    1,
                    adapters.Select(AdapterDefinitionDocument.FromDefinition).ToArray(),
                    openAiProviders.Select(
                        OpenAiCompatibleDefinitionDocument.FromDefinition).ToArray()),
                JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static void ValidateDocument(AdapterStoreDocument document)
    {
        IReadOnlyList<AdapterDefinitionDocument> adapters = document.Adapters ?? [];
        IReadOnlyList<OpenAiCompatibleDefinitionDocument> openAiProviders =
            document.OpenAiCompatibleProviders ?? [];
        if (document.SchemaVersion != 1 ||
            adapters.Count + openAiProviders.Count > MaximumDefinitions)
        {
            throw new InvalidDataException("Custom provider store version or count is unsupported.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AdapterDefinitionDocument adapter in adapters)
        {
            DeclarativeRestAdapterDefinition definition = adapter.ToDefinition();
            ValidateDefinitionId(definition.Id);
            if (!ids.Add(definition.Id))
            {
                throw new InvalidDataException(
                    $"REST adapter store contains duplicate id '{definition.Id}'.");
            }
        }
        foreach (OpenAiCompatibleDefinitionDocument provider in openAiProviders)
        {
            CustomOpenAiCompatibleDefinition definition = provider.ToDefinition();
            ValidateDefinitionId(definition.Id);
            if (!ids.Add(definition.Id))
                throw new InvalidDataException(
                    $"Custom provider store contains duplicate id '{definition.Id}'.");
        }
    }

    private static void ValidateDefinitionId(string id)
    {
        if (ProviderCatalog.Default.Any(provider =>
                string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Custom provider id '{id}' is reserved by a built-in provider.");
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
        IReadOnlyList<AdapterDefinitionDocument>? Adapters = null,
        IReadOnlyList<OpenAiCompatibleDefinitionDocument>? OpenAiCompatibleProviders = null);

    private sealed record OpenAiCompatibleDefinitionDocument(
        int SchemaVersion,
        string Id,
        string DisplayName,
        Uri Endpoint,
        string Model)
    {
        public CustomOpenAiCompatibleDefinition ToDefinition() =>
            new(SchemaVersion, Id, DisplayName, Endpoint, Model);

        public static OpenAiCompatibleDefinitionDocument FromDefinition(
            CustomOpenAiCompatibleDefinition definition) =>
            new(
                definition.SchemaVersion,
                definition.Id,
                definition.DisplayName,
                definition.Endpoint,
                definition.Model);
    }

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
