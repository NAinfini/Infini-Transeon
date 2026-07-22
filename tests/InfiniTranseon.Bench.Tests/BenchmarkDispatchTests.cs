using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfiniTranseon.Bench.Tests;

/// <summary>
/// Exercises the top-level dispatch in BenchmarkRunner.cs through the compiled entry point:
/// the <c>generate-fixtures</c> and <c>--self-check</c> sub-commands must be routed before the
/// original two-positional-argument scoring contract, and anything else must print usage and
/// exit with code 2. Invoking the real entry point keeps the dispatch itself under test without
/// modifying production code.
/// </summary>
public sealed class BenchmarkDispatchTests
{
    private static readonly string ValidBenchmarkInput = """
        {
          "runId": "dispatch-test",
          "machine": "synthetic",
          "cpu": "synthetic",
          "gpu": null,
          "targetCount": 1,
          "resolution": "1920x1080",
          "samples": [
            {
              "fixtureId": "s1",
              "scenario": "clear-subtitle",
              "language": "en",
              "expectedText": "hello",
              "actualText": "hello",
              "detectionMilliseconds": 10,
              "recognitionMilliseconds": 20,
              "localPipelineMilliseconds": 35,
              "expectedDetected": true,
              "actualDetected": true,
              "cpuCommittedBytes": 1024,
              "gpuCommittedBytes": 0,
              "cacheHit": false,
              "providerFailed": false,
              "estimatedCost": 0
            }
          ]
        }
        """;

    [Fact]
    public void GenerateFixturesSubCommandIsRoutedToTheGenerator()
    {
        using var workspace = new TempWorkspace();

        int exit = InvokeProgram("generate-fixtures", workspace.Path, "--scenarios", "clear-subtitle", "--languages", "en");

        Assert.Equal(0, exit);
        Assert.True(File.Exists(workspace.Combine("manifest.json")));
    }

    [Fact]
    public void SelfCheckSubCommandIsRoutedAndSucceeds()
    {
        Assert.Equal(0, InvokeProgram("--self-check"));
    }

    [Theory]
    [InlineData("only-one-arg")]
    [InlineData("a", "b", "c")]
    public void UnrecognisedArgumentShapesReturnUsageExitCode(params string[] args)
    {
        Assert.Equal(2, InvokeProgram(args));
    }

    [Fact]
    public void OriginalTwoPositionalScoringContractRemainsIntact()
    {
        using var workspace = new TempWorkspace();
        string samplesPath = workspace.Combine("samples.json");
        string reportPath = workspace.Combine("report.json");
        // BOM-free UTF-8, matching how a real samples.json is written; a BOM would trip
        // the byte-oriented System.Text.Json reader in the positional scoring path.
        File.WriteAllText(samplesPath, ValidBenchmarkInput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        int exit = InvokeProgram(samplesPath, reportPath);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(reportPath));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal(2, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("dispatch-test", report.RootElement.GetProperty("runId").GetString());
    }

    private static int InvokeProgram(params string[] args)
    {
        MethodInfo entryPoint = typeof(BenchmarkRunner).Assembly.EntryPoint
            ?? throw new InvalidOperationException("GameOcrBench assembly exposes no entry point.");

        object?[] parameters = entryPoint.GetParameters().Length == 1
            ? new object?[] { args }
            : Array.Empty<object?>();

        object? result = entryPoint.Invoke(null, parameters);
        return result switch
        {
            int code => code,
            Task<int> task => task.GetAwaiter().GetResult(),
            null => throw new InvalidOperationException("Entry point returned void; expected an exit code."),
            _ => throw new InvalidOperationException($"Unexpected entry point return type: {result.GetType()}."),
        };
    }
}
