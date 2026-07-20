using System.Text.Json;

namespace InfiniTranseon.Core.Tests.Packaging;

public sealed class TrustManifestSchemaTests
{
    [Fact]
    public void ReleaseSchemaPinsCanonicalSignedArtifactAndRotationFields()
    {
        using JsonDocument schema = Load("release-manifest.schema.json");

        AssertRequired(schema.RootElement,
            "schemaVersion", "releaseSequence", "releaseVersion", "channel", "architecture",
            "minimumWindowsBuild", "publishedAtUtc", "artifacts", "signatures");
        JsonElement artifact = schema.RootElement.GetProperty("$defs").GetProperty("artifact");
        AssertRequired(artifact, "fileName", "byteSize", "sha256");
        JsonElement signature = schema.RootElement.GetProperty("$defs").GetProperty("signature");
        AssertRequired(signature, "keyId", "algorithm", "signature");
        Assert.Contains("Ed25519", signature.GetProperty("properties").GetProperty("algorithm")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void ModelCatalogSchemaBindsEveryFileAndExecutionCompatibilityField()
    {
        using JsonDocument schema = Load("model-catalog.schema.json");

        AssertRequired(schema.RootElement,
            "schemaVersion", "catalogSequence", "publishedAtUtc", "models", "signatures");
        JsonElement model = schema.RootElement.GetProperty("$defs").GetProperty("model");
        AssertRequired(model,
            "modelId", "version", "licenseSpdx", "runtime", "opset", "architectures",
            "downloadOrigins", "files");
        AssertRequired(schema.RootElement.GetProperty("$defs").GetProperty("modelFile"),
            "relativePath", "byteSize", "sha256");
    }

    [Fact]
    public void ApacheLicenseAndProjectNoticeArePresent()
    {
        string root = FindRepositoryRoot();
        Assert.Contains("Apache License", File.ReadAllText(Path.Combine(root, "LICENSE")));
        Assert.Contains("Infini-Transeon", File.ReadAllText(Path.Combine(root, "NOTICE")));
    }

    private static JsonDocument Load(string fileName) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "packaging", fileName)));

    private static void AssertRequired(JsonElement schema, params string[] names)
    {
        string[] required = schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).OfType<string>().ToArray();
        Assert.Empty(names.Except(required, StringComparer.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InfiniTranseon.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
