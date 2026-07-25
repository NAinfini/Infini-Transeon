using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.App.Tests;

public sealed class CustomRestAdapterStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void Import_load_catalog_and_remove_round_trip()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "InfiniTranseon.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "rest-adapters.json");
        try
        {
            var store = new CustomRestAdapterStore(path);
            DeclarativeRestAdapterDefinition definition = CreateDefinition("custom.example");
            using var source = new MemoryStream(
                JsonSerializer.SerializeToUtf8Bytes(definition, JsonOptions));

            DeclarativeRestAdapterDefinition imported = store.Import(source);

            Assert.Equal("custom.example", imported.Id);
            DeclarativeRestAdapterDefinition loaded = Assert.Single(store.Load());
            Assert.Equal(new Uri("https://api.example.test/v1/translate"), loaded.Endpoint);
            CatalogProvider catalog = Assert.Single(store.GetCatalogProviders());
            Assert.True(catalog.IsCustom);
            Assert.Equal("REST · custom", catalog.Kind);
            Assert.Equal("custom.example.api-key", Assert.Single(catalog.Credentials).Reference);

            store.Remove("custom.example");
            Assert.Empty(store.Load());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Import_rejects_built_in_provider_id()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "InfiniTranseon.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CustomRestAdapterStore(
                Path.Combine(root, "rest-adapters.json"));
            DeclarativeRestAdapterDefinition definition =
                CreateDefinition(EngineRuntimeComposition.DeepLDefinition.Id);
            using var source = new MemoryStream(
                JsonSerializer.SerializeToUtf8Bytes(definition, JsonOptions));

            Assert.Throws<InvalidDataException>(() => store.Import(source));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static DeclarativeRestAdapterDefinition CreateDefinition(string id) =>
        new(
            schemaVersion: 1,
            id,
            displayName: "Example REST",
            endpoint: new Uri("https://api.example.test/v1/translate"),
            method: RestHttpMethod.Post,
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer {{credential:custom.example.api-key}}",
            },
            bodyTemplate:
                """{"text":"{{sourceText}}","source":"{{sourceLanguage}}","target":"{{targetLanguage}}"}""",
            responseTextJsonPointer: "/translation",
            responseErrorJsonPointer: "/error",
            credentialReferences: ["custom.example.api-key"]);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
