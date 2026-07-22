using System.Text.Json;
using System.Diagnostics;
using InfiniTranseon.Core.Updates;

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
        Assert.Equal(0, model.GetProperty("properties").GetProperty("opset")
            .GetProperty("minimum").GetInt32());
    }

    [Fact]
    public void ApacheLicenseAndProjectNoticeArePresent()
    {
        string root = FindRepositoryRoot();
        Assert.Contains("Apache License", File.ReadAllText(Path.Combine(root, "LICENSE")));
        Assert.Contains("Infini-Transeon", File.ReadAllText(Path.Combine(root, "NOTICE")));
    }

    [Fact]
    public async Task ReleaseSigningScriptCanonicalizerMatchesRuntimeForUnicodeAndPropertyOrder()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(Path.GetTempPath(), "infini-canonical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "input.json");
        string output = Path.Combine(directory, "output.json");
        try
        {
            const string json = """
                {"z":"测试<&","signatures":[{"signature":"ignored","keyId":"k","algorithm":"Ed25519"}],"a":{"y":2,"x":"é"}}
                """;
            await File.WriteAllTextAsync(input, json, TestContext.Current.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            byte[] expected = SignatureVerifier.CanonicalizeWithoutSignatures(document.RootElement);
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(Path.Combine(root, "scripts", "convert-to-canonical-json.ps1"));
            start.ArgumentList.Add("-InputPath");
            start.ArgumentList.Add(input);
            start.ArgumentList.Add("-OutputPath");
            start.ArgumentList.Add(output);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            string error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(process.ExitCode == 0, error);
            Assert.Equal(expected, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
