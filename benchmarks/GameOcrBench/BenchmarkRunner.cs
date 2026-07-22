using System.Text.Json;
using System.Text;
using GameOcrBench;

// Generator / verification modes are dispatched before the original positional-args
// contract so the CI smoke invocation (`<samples.json> <report.json>`) is untouched.
if (args.Length >= 1 && args[0] == "generate-fixtures")
    return FixtureGenerator.RunCli(args[1..]);
if (args.Length >= 1 && args[0] == "--self-check")
    return FixtureGenerator.SelfCheck();

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  GameOcrBench <samples.json> <report.json>");
    Console.Error.WriteLine("  GameOcrBench generate-fixtures <output-dir> [--seed N] [--scenarios a,b] [--languages a,b]");
    Console.Error.WriteLine("  GameOcrBench --self-check");
    return 2;
}

BenchmarkInput input = JsonSerializer.Deserialize<BenchmarkInput>(
    await File.ReadAllBytesAsync(args[0]),
    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
    throw new InvalidDataException("Benchmark input is empty.");
BenchmarkReport report = BenchmarkRunner.Run(input);
await File.WriteAllBytesAsync(args[1], JsonSerializer.SerializeToUtf8Bytes(
    report,
    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
return 0;

public sealed record BenchmarkSample(
    string FixtureId,
    string Scenario,
    string Language,
    string ExpectedText,
    string ActualText,
    double DetectionMilliseconds,
    double RecognitionMilliseconds,
    double LocalPipelineMilliseconds,
    bool ExpectedDetected,
    bool ActualDetected,
    long CpuCommittedBytes,
    long GpuCommittedBytes,
    double? ProviderNetworkMilliseconds = null,
    bool CacheHit = false,
    bool ProviderFailed = false,
    decimal EstimatedCost = 0);

public sealed record BenchmarkInput(
    string RunId,
    string Machine,
    string Cpu,
    string? Gpu,
    int TargetCount,
    string Resolution,
    IReadOnlyList<BenchmarkSample> Samples);

public sealed record MetricDistribution(double P50, double P95, double P99);

public sealed record BenchmarkReport(
    int SchemaVersion,
    string RunId,
    int SampleCount,
    double CharacterErrorRate,
    double LineAccuracy,
    double MissedDetectionRate,
    double FalseDetectionRate,
    MetricDistribution DetectionMilliseconds,
    MetricDistribution RecognitionMilliseconds,
    MetricDistribution LocalPipelineMilliseconds,
    MetricDistribution? ProviderNetworkMilliseconds,
    long PeakCpuCommittedBytes,
    long PeakGpuCommittedBytes,
    double CacheHitRate,
    double ProviderErrorRate,
    decimal TotalEstimatedCost,
    string Machine,
    string Cpu,
    string? Gpu,
    int TargetCount,
    string Resolution);

public static class BenchmarkRunner
{
    public static BenchmarkReport Run(BenchmarkInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.RunId) || input.RunId.Length > 128 ||
            string.IsNullOrWhiteSpace(input.Machine) || input.Machine.Length > 256 ||
            string.IsNullOrWhiteSpace(input.Cpu) || input.Cpu.Length > 256 ||
            input.Gpu?.Length > 256 || input.TargetCount is < 1 or > 16 ||
            string.IsNullOrWhiteSpace(input.Resolution) || input.Resolution.Length > 64 ||
            input.Samples.Count is < 1 or > 1_000_000)
            throw new InvalidDataException("Benchmark run metadata is invalid.");
        if (input.Samples.Select(item => item.FixtureId).Distinct(StringComparer.Ordinal).Count() != input.Samples.Count)
            throw new InvalidDataException("Fixture IDs must be unique.");
        if (input.Samples.Any(item =>
                string.IsNullOrWhiteSpace(item.FixtureId) || item.FixtureId.Length > 256 ||
                string.IsNullOrWhiteSpace(item.Scenario) || item.Scenario.Length > 128 ||
                string.IsNullOrWhiteSpace(item.Language) || item.Language.Length > 64 ||
                item.ExpectedText.Length > 65_536 || item.ActualText.Length > 65_536 ||
                !FiniteNonnegative(item.DetectionMilliseconds) ||
                !FiniteNonnegative(item.RecognitionMilliseconds) ||
                !FiniteNonnegative(item.LocalPipelineMilliseconds) ||
                item.ProviderNetworkMilliseconds is double provider && !FiniteNonnegative(provider) ||
                item.CpuCommittedBytes < 0 || item.GpuCommittedBytes < 0 ||
                item.EstimatedCost < 0))
            throw new InvalidDataException("Benchmark sample is invalid.");
        long characters = input.Samples.Sum(item => item.ExpectedText.EnumerateRunes().LongCount());
        long edits = input.Samples.Sum(item => Levenshtein(item.ExpectedText, item.ActualText));
        int expectedDetections = input.Samples.Count(item => item.ExpectedDetected);
        int expectedBlanks = input.Samples.Count - expectedDetections;
        int misses = input.Samples.Count(item => item.ExpectedDetected && !item.ActualDetected);
        int falseDetections = input.Samples.Count(item => !item.ExpectedDetected && item.ActualDetected);
        return new BenchmarkReport(
            2,
            input.RunId,
            input.Samples.Count,
            characters == 0 ? 0 : (double)edits / characters,
            (double)input.Samples.Count(item => item.ExpectedText == item.ActualText) / input.Samples.Count,
            expectedDetections == 0 ? 0 : (double)misses / expectedDetections,
            expectedBlanks == 0 ? 0 : (double)falseDetections / expectedBlanks,
            Distribution(input.Samples.Select(item => item.DetectionMilliseconds)),
            Distribution(input.Samples.Select(item => item.RecognitionMilliseconds)),
            Distribution(input.Samples.Select(item => item.LocalPipelineMilliseconds)),
            DistributionOrNull(input.Samples
                .Where(item => item.ProviderNetworkMilliseconds.HasValue)
                .Select(item => item.ProviderNetworkMilliseconds!.Value)),
            input.Samples.Max(item => item.CpuCommittedBytes),
            input.Samples.Max(item => item.GpuCommittedBytes),
            (double)input.Samples.Count(item => item.CacheHit) / input.Samples.Count,
            (double)input.Samples.Count(item => item.ProviderFailed) / input.Samples.Count,
            input.Samples.Sum(item => item.EstimatedCost),
            input.Machine,
            input.Cpu,
            input.Gpu,
            input.TargetCount,
            input.Resolution);
    }

    private static MetricDistribution Distribution(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        return new MetricDistribution(Percentile(values, 0.50), Percentile(values, 0.95), Percentile(values, 0.99));
    }

    private static MetricDistribution? DistributionOrNull(IEnumerable<double> source)
    {
        double[] values = source.ToArray();
        return values.Length == 0 ? null : Distribution(values);
    }

    private static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        int index = Math.Clamp((int)Math.Ceiling(values.Count * fraction) - 1, 0, values.Count - 1);
        return values[index];
    }

    private static int Levenshtein(string left, string right)
    {
        Rune[] leftRunes = left.EnumerateRunes().ToArray();
        Rune[] rightRunes = right.EnumerateRunes().ToArray();
        int[] previous = Enumerable.Range(0, rightRunes.Length + 1).ToArray();
        int[] current = new int[rightRunes.Length + 1];
        for (int row = 1; row <= leftRunes.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= rightRunes.Length; column++)
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (leftRunes[row - 1] == rightRunes[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[rightRunes.Length];
    }


    private static bool FiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;
}
