using System.Collections.Generic;

namespace InfiniTranseon.Bench.Tests;

/// <summary>
/// Scoring and validation contract for <see cref="BenchmarkRunner.Run"/>: rune-based CER and
/// Levenshtein aggregation, detection/false-positive rates, percentile distributions, and the
/// input-validation error paths that guard the report schema.
/// </summary>
public sealed class BenchmarkScoringTests
{
    private static BenchmarkSample Sample(
        string fixtureId,
        string expected,
        string actual,
        double detectionMs = 10,
        double recognitionMs = 10,
        double localMs = 10,
        bool expectedDetected = true,
        bool actualDetected = true,
        long cpuBytes = 1024,
        long gpuBytes = 512,
        double? providerMs = null,
        bool cacheHit = false,
        bool providerFailed = false,
        decimal cost = 0m) => new(
            fixtureId,
            "clear-subtitle",
            "en",
            expected,
            actual,
            detectionMs,
            recognitionMs,
            localMs,
            expectedDetected,
            actualDetected,
            cpuBytes,
            gpuBytes,
            providerMs,
            cacheHit,
            providerFailed,
            cost);

    private static BenchmarkInput Input(params BenchmarkSample[] samples) =>
        new("run-1", "machine", "cpu", null, 1, "1920x1080", samples);

    [Fact]
    public void RunRejectsANullInput()
    {
        Assert.Throws<ArgumentNullException>(() => BenchmarkRunner.Run(null!));
    }

    [Fact]
    public void AggregatesCharacterErrorRateAndLineAccuracyAcrossSamples()
    {
        BenchmarkReport report = BenchmarkRunner.Run(Input(
            Sample("a", "abc", "abd", detectionMs: 12),
            Sample("b", "hi", "hi", detectionMs: 10, actualDetected: false)));

        Assert.Equal(2, report.SchemaVersion);
        Assert.Equal(2, report.SampleCount);
        // 1 edit over 5 expected runes.
        Assert.Equal(0.2, report.CharacterErrorRate, 10);
        // Only the second sample matches exactly.
        Assert.Equal(0.5, report.LineAccuracy, 10);
        // One of two expected detections was missed.
        Assert.Equal(0.5, report.MissedDetectionRate, 10);
        Assert.Equal(0.0, report.FalseDetectionRate, 10);
        // Sorted detection latencies [10, 12].
        Assert.Equal(10, report.DetectionMilliseconds.P50, 10);
        Assert.Equal(12, report.DetectionMilliseconds.P95, 10);
        Assert.Equal(12, report.DetectionMilliseconds.P99, 10);
    }

    [Fact]
    public void CharacterErrorRateIsZeroWhenThereAreNoExpectedCharacters()
    {
        BenchmarkReport report = BenchmarkRunner.Run(Input(
            Sample("blank", string.Empty, string.Empty, expectedDetected: false, actualDetected: false)));

        Assert.Equal(0.0, report.CharacterErrorRate, 10);
        Assert.Equal(1.0, report.LineAccuracy, 10);
        Assert.Equal(0.0, report.MissedDetectionRate, 10);
        Assert.Equal(0.0, report.FalseDetectionRate, 10);
    }

    [Fact]
    public void CharacterErrorRateCountsRunesNotUtf16CodeUnits()
    {
        // A single supplementary-plane emoji deleted: one rune of one expected rune.
        BenchmarkReport report = BenchmarkRunner.Run(Input(
            Sample("emoji", "\U0001F600", string.Empty)));

        Assert.Equal(1.0, report.CharacterErrorRate, 10);
    }

    [Fact]
    public void FalseDetectionRateUsesTheExpectedBlankDenominator()
    {
        BenchmarkReport report = BenchmarkRunner.Run(Input(
            Sample("blank", string.Empty, "ghost", expectedDetected: false, actualDetected: true)));

        Assert.Equal(1.0, report.FalseDetectionRate, 10);
        Assert.Equal(0.0, report.MissedDetectionRate, 10);
    }

    [Fact]
    public void CacheProviderCostAndPeakMemoryAreAggregated()
    {
        BenchmarkReport report = BenchmarkRunner.Run(Input(
            Sample("a", "x", "x", cpuBytes: 100, gpuBytes: 900, cacheHit: true, cost: 0.5m),
            Sample("b", "y", "y", cpuBytes: 800, gpuBytes: 200, providerFailed: true, cost: 0.25m)));

        Assert.Equal(0.5, report.CacheHitRate, 10);
        Assert.Equal(0.5, report.ProviderErrorRate, 10);
        Assert.Equal(0.75m, report.TotalEstimatedCost);
        Assert.Equal(800, report.PeakCpuCommittedBytes);
        Assert.Equal(900, report.PeakGpuCommittedBytes);
    }

    [Fact]
    public void ProviderNetworkDistributionIsNullWhenNoSampleReportsIt()
    {
        BenchmarkReport report = BenchmarkRunner.Run(Input(Sample("a", "x", "x")));

        Assert.Null(report.ProviderNetworkMilliseconds);
    }

    [Fact]
    public void ProviderNetworkDistributionIsComputedFromReportingSamplesOnly()
    {
        BenchmarkReport report = BenchmarkRunner.Run(Input(
            Sample("a", "x", "x", providerMs: 40),
            Sample("b", "y", "y", providerMs: null)));

        Assert.NotNull(report.ProviderNetworkMilliseconds);
        Assert.Equal(40, report.ProviderNetworkMilliseconds!.P50, 10);
    }

    [Fact]
    public void DuplicateFixtureIdsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => BenchmarkRunner.Run(Input(
            Sample("dup", "a", "a"),
            Sample("dup", "b", "b"))));
    }

    [Fact]
    public void EmptySampleSetIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => BenchmarkRunner.Run(Input()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void OutOfRangeTargetCountIsRejected(int targetCount)
    {
        var input = new BenchmarkInput(
            "run", "machine", "cpu", null, targetCount, "1920x1080",
            new List<BenchmarkSample> { Sample("a", "x", "x") });

        Assert.Throws<InvalidDataException>(() => BenchmarkRunner.Run(input));
    }

    [Fact]
    public void BlankRunMetadataIsRejected()
    {
        var input = new BenchmarkInput(
            "   ", "machine", "cpu", null, 1, "1920x1080",
            new List<BenchmarkSample> { Sample("a", "x", "x") });

        Assert.Throws<InvalidDataException>(() => BenchmarkRunner.Run(input));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void NonFiniteOrNegativeLatencyIsRejected(double detectionMs)
    {
        Assert.Throws<InvalidDataException>(() => BenchmarkRunner.Run(Input(
            Sample("a", "x", "x", detectionMs: detectionMs))));
    }

    [Fact]
    public void NegativeEstimatedCostIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => BenchmarkRunner.Run(Input(
            Sample("a", "x", "x", cost: -0.01m))));
    }
}
