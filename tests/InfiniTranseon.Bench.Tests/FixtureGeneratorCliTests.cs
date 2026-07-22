using System.IO;
using System.Text.Json;
using GameOcrBench;

namespace InfiniTranseon.Bench.Tests;

/// <summary>
/// Argument-parsing contract for <see cref="FixtureGenerator.RunCli"/>, the target of the
/// <c>generate-fixtures</c> dispatch branch. Failure paths return usage exit code 2; a valid
/// invocation returns 0 and records the requested seed in the manifest.
/// </summary>
public sealed class FixtureGeneratorCliTests
{
    [Fact]
    public void MissingOutputDirectoryReturnsUsageExitCode()
    {
        Assert.Equal(2, FixtureGenerator.RunCli(Array.Empty<string>()));
    }

    [Fact]
    public void ASecondPositionalArgumentIsRejected()
    {
        Assert.Equal(2, FixtureGenerator.RunCli(new[] { "dir-one", "dir-two" }));
    }

    [Theory]
    [InlineData("--seed", "not-an-int")]
    [InlineData("--seed")]
    [InlineData("--scenarios")]
    [InlineData("--languages")]
    [InlineData("--unknown-flag")]
    public void MalformedOptionsReturnUsageExitCode(params string[] trailing)
    {
        string[] args = new[] { "some-output-dir" }.Concat(trailing).ToArray();

        Assert.Equal(2, FixtureGenerator.RunCli(args));
    }

    [Fact]
    public void UnknownScenarioIsRejectedBeforeAnythingIsGenerated()
    {
        using var workspace = new TempWorkspace();

        Assert.Equal(2, FixtureGenerator.RunCli(new[] { workspace.Path, "--scenarios", "bogus-scenario" }));
        Assert.False(File.Exists(workspace.Combine("manifest.json")));
    }

    [Fact]
    public void UnknownLanguageIsRejectedBeforeAnythingIsGenerated()
    {
        using var workspace = new TempWorkspace();

        Assert.Equal(2, FixtureGenerator.RunCli(new[] { workspace.Path, "--languages", "xx" }));
        Assert.False(File.Exists(workspace.Combine("manifest.json")));
    }

    [Fact]
    public void ValidInvocationGeneratesFixturesAndEchoesTheSeed()
    {
        using var workspace = new TempWorkspace();

        int exit = FixtureGenerator.RunCli(new[]
        {
            workspace.Path, "--scenarios", "clear-subtitle", "--languages", "en", "--seed", "7",
        });

        Assert.Equal(0, exit);
        string manifestPath = workspace.Combine("manifest.json");
        Assert.True(File.Exists(manifestPath));

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(7, manifest.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal(
            FixtureGenerator.ManifestSchemaVersion,
            manifest.RootElement.GetProperty("schemaVersion").GetInt32());
    }
}
