using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Tests.Ocr;

public sealed class OcrTextGenerationGateTests
{
    [Fact]
    public void RequiresStableFramesDeduplicatesAndMapsCropGeometryToTarget()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        var targetId = new CaptureTargetId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        var region = ProfileRegion.Create(
            "Dialogue",
            new NormalizedRect(0.1, 0.6, 0.8, 0.3)) with
        {
            RegionId = regionId,
            LineBreakMode = LineBreakMode.PreserveLines,
        };
        var gate = new OcrTextGenerationGate(new TextStabilizerOptions(
            StableFrameCount: 2,
            MinimumDelay: TimeSpan.Zero,
            MaximumWait: TimeSpan.FromMilliseconds(500)));
        DateTimeOffset start = DateTimeOffset.UnixEpoch;

        Assert.False(gate.TryCreate(
            Result(epoch, target, regionId, generation: 1, "攻撃:100"),
            targetId,
            region,
            start,
            capturedAtQpc: 10,
            frameSequence: 1,
            out _));
        Assert.True(gate.TryCreate(
            Result(epoch, target, regionId, generation: 2, "攻撃:100"),
            targetId,
            region,
            start.AddMilliseconds(100),
            capturedAtQpc: 20,
            frameSequence: 2,
            out TextGeneration? generation));

        Assert.NotNull(generation);
        Assert.Equal("攻撃:100", generation.SourceText);
        Assert.Equal(2, generation.SourceToken.SourceGeneration);
        Assert.Equal(0.18, generation.SourceBounds.X, 12);
        Assert.Equal(0.66, generation.SourceBounds.Y, 12);
        Assert.Equal(0.4, generation.SourceBounds.Width, 12);
        Assert.Equal(0.06, generation.SourceBounds.Height, 12);
        Assert.False(gate.TryCreate(
            Result(epoch, target, regionId, generation: 3, "攻撃:100"),
            targetId,
            region,
            start.AddMilliseconds(200),
            capturedAtQpc: 30,
            frameSequence: 3,
            out _));
    }

    [Fact]
    public void ChangedTextCreatesNewSourceEventOnlyAfterItStabilizes()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        var targetId = new CaptureTargetId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        ProfileRegion region = ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0, 0, 1, 1)) with { RegionId = regionId };
        var gate = new OcrTextGenerationGate(new TextStabilizerOptions(
            2, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        DateTimeOffset start = DateTimeOffset.UnixEpoch;

        gate.TryCreate(Result(epoch, target, regionId, 1, "A"), targetId, region, start, 1, 1, out _);
        Assert.True(gate.TryCreate(Result(epoch, target, regionId, 2, "A"), targetId, region,
            start.AddMilliseconds(10), 2, 2, out TextGeneration? first));
        Assert.False(gate.TryCreate(Result(epoch, target, regionId, 3, "B"), targetId, region,
            start.AddMilliseconds(20), 3, 3, out _));
        Assert.True(gate.TryCreate(Result(epoch, target, regionId, 4, "B"), targetId, region,
            start.AddMilliseconds(30), 4, 4, out TextGeneration? second));

        Assert.NotEqual(first!.SourceEventId, second!.SourceEventId);
        Assert.Equal("B", second.SourceText);
    }

    [Fact]
    public void ManualResultBypassesStabilizationAndDuplicateSuppression()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        var targetId = new CaptureTargetId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        ProfileRegion region = ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0, 0, 1, 1)) with { RegionId = regionId };
        var gate = new OcrTextGenerationGate(new TextStabilizerOptions(
            2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        DateTimeOffset start = DateTimeOffset.UnixEpoch;

        Assert.True(gate.TryCreate(
            Result(epoch, target, regionId, 1, "same", isManual: true),
            targetId,
            region,
            start,
            1,
            1,
            out TextGeneration? first));
        Assert.True(gate.TryCreate(
            Result(epoch, target, regionId, 2, "same", isManual: true),
            targetId,
            region,
            start.AddMilliseconds(10),
            2,
            2,
            out TextGeneration? second));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.SourceEventId, second.SourceEventId);
    }

    [Fact]
    public void RejectsTerminalOcrFailuresAndMismatchedRegions()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        var targetId = new CaptureTargetId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        ProfileRegion region = ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0, 0, 1, 1)) with { RegionId = regionId };
        var gate = new OcrTextGenerationGate(new TextStabilizerOptions(
            1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        OcrResultSnapshot failed = Result(epoch, target, regionId, 1, "") with
        {
            TerminalErrorCode = "ocr.windows.runtimeFailed",
            Lines = [],
        };

        OcrTextGenerationException error = Assert.Throws<OcrTextGenerationException>(() =>
            gate.TryCreate(failed, targetId, region, DateTimeOffset.UtcNow, 1, 1, out _));
        Assert.Equal("ocr.windows.runtimeFailed", error.Code);
        Assert.Throws<ArgumentException>(() => gate.TryCreate(
            Result(epoch, target, Guid.NewGuid(), 2, "A"),
            targetId,
            region,
            DateTimeOffset.UtcNow,
            2,
            2,
            out _));
    }

    private static OcrResultSnapshot Result(
        Guid epoch,
        TargetInstanceId target,
        Guid regionId,
        long generation,
        string text,
        bool isManual = false)
    {
        var source = new SourceGenerationToken(
            epoch,
            target,
            CaptureAreaKey.UserRegion(new RegionId(regionId)),
            new TextTrackId(Guid.Parse("6ca36f09-ccfb-44d5-9be8-084be7ab00af")),
            generation,
            1);
        return new OcrResultSnapshot(
            new OcrExecutionToken(source, Guid.NewGuid(), 1, 1, isManual),
            [new TextLine(text, new NormalizedRect(0.1, 0.2, 0.5, 0.2), 0.9)],
            "windows.media.ocr",
            "windows-11",
            true,
            null);
    }
}
