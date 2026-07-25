using System.Text.Json;
using System.Diagnostics;
using System.Text;
using InfiniTranseon.Contracts.Security;
using InfiniTranseon.Core.Translation.Local;
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
        AssertRequired(artifact, "fileName", "byteSize", "sha256", "codeSigning");
        Assert.Contains("unsigned", artifact.GetProperty("properties")
            .GetProperty("codeSigning").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("authenticode", artifact.GetProperty("properties")
            .GetProperty("codeSigning").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
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

    [Fact]
    public async Task UnsignedReleaseManifestIsSignedAndVerifiedWithoutAuthenticodeInputs()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(
            Path.GetTempPath(), "infini-unsigned-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "Infini-Transeon.msi"),
                "msi"u8.ToArray(),
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "Infini-Transeon-portable.zip"),
                "portable"u8.ToArray(),
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "Infini-Transeon-source.zip"),
                "source"u8.ToArray(),
                TestContext.Current.CancellationToken);
            string trustRoot = Path.Combine(directory, "TestTrustRoot.cs");
            await File.WriteAllTextAsync(
                trustRoot,
                """
                public static class TestTrustRoot
                {
                    public const string CurrentKeyId = "rfc8032";
                    public static byte[] Key = Convert.FromHexString(
                        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
                }
                """,
                TestContext.Current.CancellationToken);
            const string privateKey = """
                -----BEGIN PRIVATE KEY-----
                MC4CAQAwBQYDK2VwBCIEIJ1hsZ3v/VpguoRK9JLsLMREScVpezJpGXA7rAMcrn9g
                -----END PRIVATE KEY-----
                """;
            var environment = new Dictionary<string, string?>
            {
                ["RELEASE_ED25519_PRIVATE_KEY"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(privateKey)),
                ["RELEASE_ED25519_KEY_ID"] = "rfc8032",
                ["RUNNER_TEMP"] = directory,
                ["AUTHENTICODE_CERTIFICATE"] = null,
                ["AUTHENTICODE_PASSWORD"] = null,
                ["AUTHENTICODE_PUBLISHER"] = null,
            };
            (int buildExitCode, string buildError) = await RunPwshAsync(
                Path.Combine(root, "scripts", "build-signed-release.ps1"),
                [
                    "-ReleaseDirectory", directory,
                    "-Version", "v1.2.3",
                    "-ReleaseSequence", "7",
                ],
                environment);
            Assert.True(buildExitCode == 0, buildError);
            string manifestPath = Path.Combine(directory, "release-manifest.json");
            (int verifyExitCode, string verifyError) = await RunPwshAsync(
                Path.Combine(root, "scripts", "verify-release-signing-key.ps1"),
                [
                    "-ManifestPath", manifestPath,
                    "-TrustRootSource", trustRoot,
                ],
                environment);
            Assert.True(verifyExitCode == 0, verifyError);

            using JsonDocument manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    manifestPath, TestContext.Current.CancellationToken));
            JsonElement msi = Assert.Single(
                manifest.RootElement.GetProperty("artifacts").EnumerateArray(),
                artifact => artifact.GetProperty("fileName").GetString() ==
                    "Infini-Transeon.msi");
            Assert.Equal(
                "unsigned",
                msi.GetProperty("codeSigning").GetString());
            Assert.False(msi.TryGetProperty("authenticodePublisher", out _));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShippedModelCatalogTemplateBuildsIntoAVerifiedDataOnlyCatalog()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(
            Path.GetTempPath(), "infini-model-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string privateKey = """
                -----BEGIN PRIVATE KEY-----
                MC4CAQAwBQYDK2VwBCIEIJ1hsZ3v/VpguoRK9JLsLMREScVpezJpGXA7rAMcrn9g
                -----END PRIVATE KEY-----
                """;
            var environment = new Dictionary<string, string?>
            {
                ["RELEASE_ED25519_PRIVATE_KEY"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(privateKey)),
                ["RELEASE_ED25519_KEY_ID"] = "rfc8032",
                ["RUNNER_TEMP"] = directory,
            };
            string output = Path.Combine(directory, "model-catalog.json");
            (int exitCode, string error) = await RunPwshAsync(
                Path.Combine(root, "scripts", "build-signed-model-catalog.ps1"),
                [
                    "-TemplatePath",
                    Path.Combine(root, "packaging", "model-catalog.template.json"),
                    "-OutputPath",
                    output,
                    "-CatalogSequence",
                    "17",
                ],
                environment);
            Assert.True(exitCode == 0, error);

            var service = new ModelCatalogService(
                new SignatureVerifier(new Ed25519TrustRootSet(
                    new Ed25519PublicKey(
                        "rfc8032",
                        Convert.FromHexString(
                            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")),
                    null,
                    [])),
                new SignedSequenceState());
            VerifiedModelCatalog catalog = service.LoadVerified(
                await File.ReadAllBytesAsync(
                    output,
                    TestContext.Current.CancellationToken));
            ModelCatalogEntry model = Assert.Single(
                catalog.Models,
                entry => entry.ModelId == "madlad");

            Assert.Equal(17, catalog.CatalogSequence);
            Assert.Equal("ctranslate2-madlad-v1", model.Runtime);
            Assert.Equal(4, model.Files.Count);
            Assert.All(
                catalog.Models.SelectMany(entry => entry.Files),
                file => Assert.DoesNotContain(
                    Path.GetExtension(file.RelativePath),
                    new[] { ".dll", ".exe", ".ps1", ".py" },
                    StringComparer.OrdinalIgnoreCase));
            Assert.Contains(model.Files, file =>
                file.RelativePath == "model.bin" &&
                file.ByteSize == 2_950_208_290L &&
                file.Sha256 == "890ed3b7e4654dcf1b9e7f2ce6ce641447462e782881e81aac443568eb1ca702");

            // OCR ships as a shared detector plus one recognizer per language, so a user who reads
            // two languages downloads the 5 MB detector once rather than twice.
            ModelCatalogEntry detector = Assert.Single(
                catalog.Models,
                entry => entry.ModelId == "ppocr-v4-base");
            Assert.Equal("ppocr-onnx-v4", detector.Runtime);
            Assert.Equal(
                ["det/ch_PP-OCRv4_det_mobile.onnx", "cls/ch_ppocr_mobile_v2.0_cls_mobile.onnx"],
                detector.Files.Select(file => file.RelativePath));
            Assert.Equal(
                ["ppocr-v4-rec-en", "ppocr-v4-rec-ja", "ppocr-v4-rec-zh-hans"],
                catalog.Models
                    .Where(entry => entry.Runtime == "ppocr-onnx-v4" && entry.ModelId != "ppocr-v4-base")
                    .Select(entry => entry.ModelId)
                    .Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Error)> RunPwshAsync(
        string script,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach ((string name, string? value) in environment)
        {
            if (value is null)
                start.Environment.Remove(name);
            else
                start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("pwsh did not start.");
        string error = await process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, error);
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
