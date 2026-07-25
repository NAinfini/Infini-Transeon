using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeContractsTests
{
    [Fact]
    public void StrongIdentifiersRejectEmptyValues()
    {
        Assert.Throws<ArgumentException>(() => new CaptureTargetId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TargetInstanceId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new RegionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TextTrackId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SourceEventId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TranslationChannelId(Guid.Empty));
    }

    [Fact]
    public void StrongIdentifiersCannotAcquireAnImplicitEmptyStructValue()
    {
        Assert.False(typeof(CaptureTargetId).IsValueType);
        Assert.False(typeof(TargetInstanceId).IsValueType);
        Assert.False(typeof(RegionId).IsValueType);
        Assert.False(typeof(TextTrackId).IsValueType);
        Assert.False(typeof(SourceEventId).IsValueType);
        Assert.False(typeof(TranslationChannelId).IsValueType);
    }

    [Theory]
    [InlineData(-0.01, 0, 0.5, 0.5)]
    [InlineData(0, -0.01, 0.5, 0.5)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(0, 0, 0.5, 0)]
    [InlineData(0.8, 0, 0.3, 0.5)]
    [InlineData(0, 0.8, 0.5, 0.3)]
    public void NormalizedRectRejectsOutOfBoundsGeometry(
        double x,
        double y,
        double width,
        double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(x, y, width, height));
    }

    [Fact]
    public void CaptureAreaKeyRequiresRegionOnlyForUserRegion()
    {
        RegionId regionId = new(Guid.NewGuid());

        Assert.Equal(regionId, CaptureAreaKey.UserRegion(regionId).UserRegionId);
        Assert.Null(CaptureAreaKey.FullTarget.UserRegionId);
        Assert.Null(CaptureAreaKey.RemainingArea.UserRegionId);
        Assert.Throws<ArgumentException>(() =>
            new CaptureAreaKey(CaptureAreaKind.UserRegion, null));
        Assert.Throws<ArgumentException>(() =>
            new CaptureAreaKey(CaptureAreaKind.FullTarget, regionId));
    }

    [Fact]
    public void SourceTokenRequiresPositiveGenerationAndRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.FullTarget,
            new TextTrackId(Guid.NewGuid()),
            sourceGeneration: 0,
            profileRevision: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.FullTarget,
            new TextTrackId(Guid.NewGuid()),
            sourceGeneration: 1,
            profileRevision: 0));
    }

    [Fact]
    public void ExecutionTokensRejectInvalidAttemptAndSequence()
    {
        SourceGenerationToken source = ValidSourceToken();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OcrExecutionToken(source, Guid.NewGuid(), attempt: 0, resultSequence: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OcrExecutionToken(source, Guid.NewGuid(), attempt: 1, resultSequence: 0));

        ChannelExecutionToken channel = new(
            source,
            new TranslationChannelId(Guid.NewGuid()),
            Guid.NewGuid(),
            Guid.NewGuid());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StageExecutionToken(
            channel,
            Guid.NewGuid(),
            stageSequence: 0,
            attempt: 1,
            streamSequence: 1));
    }

    [Fact]
    public void ManualOcrPayloadRoundTripsAcceptedAndRejectedAcknowledgements()
    {
        byte[] request = RuntimeManualOcrPayloadCodec.EncodeRequest();
        RuntimeManualOcrPayloadCodec.ValidateRequest(request);

        var scheduled = new RuntimeManualOcrAcknowledgement(
            true,
            RuntimeManualOcrStatus.Scheduled,
            targetCount: 2,
            regionCount: 5,
            errorCode: null);
        Assert.Equal(
            scheduled,
            RuntimeManualOcrPayloadCodec.DecodeAcknowledgement(
                RuntimeManualOcrPayloadCodec.EncodeAcknowledgement(scheduled)));

        var rejected = new RuntimeManualOcrAcknowledgement(
            false,
            RuntimeManualOcrStatus.NoTargets,
            targetCount: 0,
            regionCount: 0,
            errorCode: "ocr.manual.noTargets");
        Assert.Equal(
            rejected,
            RuntimeManualOcrPayloadCodec.DecodeAcknowledgement(
                RuntimeManualOcrPayloadCodec.EncodeAcknowledgement(rejected)));

        var unavailable = new RuntimeManualOcrAcknowledgement(
            false,
            RuntimeManualOcrStatus.TargetUnavailable,
            targetCount: 0,
            regionCount: 0,
            errorCode: "ocr.manual.targetUnavailable");
        Assert.Equal(
            unavailable,
            RuntimeManualOcrPayloadCodec.DecodeAcknowledgement(
                RuntimeManualOcrPayloadCodec.EncodeAcknowledgement(unavailable)));
    }

    [Fact]
    public void ManualOcrPayloadRejectsMalformedOrInconsistentData()
    {
        byte[] request = RuntimeManualOcrPayloadCodec.EncodeRequest();
        request[7] = 1;
        Assert.Throws<InvalidDataException>(
            () => RuntimeManualOcrPayloadCodec.ValidateRequest(request));
        Assert.Throws<ArgumentException>(() => new RuntimeManualOcrAcknowledgement(
            true,
            RuntimeManualOcrStatus.Scheduled,
            targetCount: 0,
            regionCount: 1,
            errorCode: null));

        byte[] acknowledgement = RuntimeManualOcrPayloadCodec.EncodeAcknowledgement(
            new RuntimeManualOcrAcknowledgement(
                false,
                RuntimeManualOcrStatus.Busy,
                targetCount: 0,
                regionCount: 0,
                errorCode: "ocr.manual.busy"));
        acknowledgement[6] = 1;
        Assert.Throws<InvalidDataException>(
            () => RuntimeManualOcrPayloadCodec.DecodeAcknowledgement(acknowledgement));
    }

    private static SourceGenerationToken ValidSourceToken() => new(
        Guid.NewGuid(),
        new TargetInstanceId(Guid.NewGuid()),
        CaptureAreaKey.FullTarget,
        new TextTrackId(Guid.NewGuid()),
        1,
        1);
}
