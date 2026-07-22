using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeFlowContractTests
{
    [Fact]
    public void ReconnectSnapshotOwnsCompleteStateAndRejectsDuplicateTargets()
    {
        Guid epoch = Guid.NewGuid();
        TargetInstanceId target = new(Guid.NewGuid());
        var snapshot = new RuntimeReconnectSnapshot(
            epoch,
            profileRevision: 4,
            policyRevision: 7,
            [new TargetSnapshot(target, new CaptureTargetId(Guid.NewGuid()), TargetLifecycleState.Running, 1920, 1080, 96)],
            RuntimeCapabilities.VersionOne,
            new RuntimeBudgetSnapshot(1, epoch, []));

        Assert.Equal(4, snapshot.ProfileRevision);
        Assert.Throws<ArgumentException>(() => new RuntimeReconnectSnapshot(
            epoch,
            4,
            7,
            [snapshot.Targets[0], snapshot.Targets[0]],
            RuntimeCapabilities.VersionOne,
            snapshot.Budget));
    }

    [Fact]
    public void CumulativeTranslationStreamRequiresStrictlyIncreasingSequence()
    {
        StageExecutionToken first = StageToken(streamSequence: 1);
        var gate = new RuntimeStreamSequenceGate();

        gate.Accept(first);

        Assert.Throws<RuntimeContractException>(() => gate.Accept(first));
        Assert.Throws<RuntimeContractException>(() => gate.Accept(WithSequence(first, 3)));
        gate.Accept(WithSequence(first, 2));
    }

    [Fact]
    public void PolicyAcknowledgementsCannotMoveBackwardOrAcknowledgeUnknownRevision()
    {
        var gate = new RuntimePolicyAcknowledgementGate();
        gate.RecordSent(3);
        gate.RecordSent(4);
        gate.Accept(new PolicyAcknowledgement(3, true, null));

        Assert.Throws<RuntimeContractException>(() =>
            gate.Accept(new PolicyAcknowledgement(2, true, null)));
        Assert.Throws<RuntimeContractException>(() =>
            gate.Accept(new PolicyAcknowledgement(5, true, null)));

        gate.Accept(new PolicyAcknowledgement(4, false, "runtime.policy.capacity"));
    }

    [Fact]
    public void CloudCropRequiresExplicitConsentAndOwnedImmutableBytes()
    {
        byte[] bytes = [1, 2, 3, 4];
        var request = new CloudOcrCropRequest(
            OcrToken(),
            "image/png",
            bytes,
            pixelWidth: 1,
            pixelHeight: 1,
            explicitCloudConsent: true);
        bytes[0] = 99;

        Assert.Equal(1, request.EncodedCrop.Span[0]);
        Assert.Throws<ArgumentException>(() => new CloudOcrCropRequest(
            OcrToken(),
            "image/png",
            [1, 2, 3, 4],
            pixelWidth: 1,
            pixelHeight: 1,
            explicitCloudConsent: false));
    }

    [Fact]
    public void DiagnosticEventsCarryLocalizationDataButNoDisplaySentence()
    {
        var diagnostic = new RuntimeDiagnosticEvent(
            "capture.target.closed",
            "runtime.capture.targetClosed",
            new Dictionary<string, string> { ["targetId"] = Guid.NewGuid().ToString("D") },
            RuntimeDiagnosticSeverity.Warning,
            DateTimeOffset.UtcNow);

        Assert.Equal("runtime.capture.targetClosed", diagnostic.MessageKey);
        Assert.DoesNotContain("DisplayMessage", diagnostic.GetType().GetProperties().Select(item => item.Name));
    }

    [Fact]
    public void OverlaySnapshotOwnsOrderedUniqueSlots()
    {
        TargetInstanceId target = new(Guid.NewGuid());
        Guid slot = Guid.NewGuid();
        Guid region = Guid.NewGuid();
        var style = new OverlayRegionStyleSnapshot(
            OverlayBackgroundTreatment.Translucent,
            OverlayTextAlignment.Left,
            "#CC000000",
            "#FFFFFFFF",
            0.8,
            12,
            8,
            24,
            12,
            4);
        var state = new OverlayDesiredState(
            Guid.NewGuid(),
            target,
            overlayRevision: 2,
            [new OverlayRegionSnapshot(region, new OverlayPixelRect(10, 20, 300, 100), style,
                [new OverlaySlotSnapshot(slot, 0, OverlaySlotState.Success, "你好", "primary")])]);

        Assert.Equal(slot, state.Regions[0].OrderedSlots[0].SlotId);
        Assert.Throws<ArgumentException>(() => new OverlayDesiredState(
            state.RuntimeEpoch,
            target,
            3,
            [state.Regions[0], state.Regions[0]]));
        Assert.Throws<ArgumentException>(() => new OverlayRegionSnapshot(
            Guid.NewGuid(),
            new OverlayPixelRect(0, 0, 100, 100),
            style with { BlurRadius = 65 },
            []));
    }

    private static OcrExecutionToken OcrToken() => new(
        SourceToken(),
        Guid.NewGuid(),
        attempt: 1,
        resultSequence: 1);

    private static StageExecutionToken StageToken(long streamSequence) => new(
        new ChannelExecutionToken(
            SourceToken(),
            new TranslationChannelId(Guid.NewGuid()),
            Guid.NewGuid(),
            Guid.NewGuid()),
        Guid.NewGuid(),
        stageSequence: 1,
        attempt: 1,
        streamSequence);

    private static StageExecutionToken WithSequence(StageExecutionToken token, long sequence) => new(
        token.Channel,
        token.StageId,
        token.StageSequence,
        token.Attempt,
        sequence);

    private static SourceGenerationToken SourceToken() => new(
        Guid.NewGuid(),
        new TargetInstanceId(Guid.NewGuid()),
        CaptureAreaKey.FullTarget,
        new TextTrackId(Guid.NewGuid()),
        sourceGeneration: 1,
        profileRevision: 1);
}
