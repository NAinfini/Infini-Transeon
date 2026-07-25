using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// Direct unit coverage of <see cref="RuntimeEventHub"/>: publish/raise wiring, admission-rejection
/// counters by reason, and the bounded ring buffer. <see cref="RealRuntimeControlServiceTests"/>
/// covers the end-to-end engine-event -> admission -> hub path; this file exercises the hub itself
/// so its ring-buffer bound and counters are pinned independent of the engine facade.
/// </summary>
public sealed class RuntimeEventHubTests
{
    private static readonly TargetInstanceId Target = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly TextTrackId Track = new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));

    private static LiveOcrRecognized Ocr(long sourceGeneration) => new(
        RuntimeEpoch: Guid.NewGuid(),
        TargetInstanceId: Target,
        Area: CaptureAreaKey.FullTarget,
        TextTrackId: Track,
        SourceGeneration: sourceGeneration,
        ProfileRevision: 1,
        Lines: [],
        ModelId: "paddle-ocr",
        ModelVersion: "1.0",
        IsStable: true,
        TerminalErrorCode: null,
        OccurredAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void PublishOcrRecognized_raises_the_event_and_appends_to_the_snapshot()
    {
        var hub = new RuntimeEventHub();
        LiveOcrRecognized? received = null;
        hub.OcrRecognized += (_, payload) => received = payload;

        LiveOcrRecognized published = Ocr(1);
        hub.PublishOcrRecognized(published);

        Assert.Same(published, received);
        RuntimeHubEvent entry = Assert.Single(hub.Snapshot());
        Assert.Equal(RuntimeHubEventKind.OcrRecognized, entry.Kind);
        Assert.Same(published, entry.Payload);
    }

    [Fact]
    public void RecordAdmissionRejected_tallies_by_reason_and_does_not_touch_the_ring_buffer()
    {
        var hub = new RuntimeEventHub();

        hub.RecordAdmissionRejected(RuntimeStateAdmission.RejectedStaleSourceGeneration);
        hub.RecordAdmissionRejected(RuntimeStateAdmission.RejectedStaleSourceGeneration);
        hub.RecordAdmissionRejected(RuntimeStateAdmission.RejectedStaleChannel);
        hub.RecordAdmissionRejected(RuntimeStateAdmission.RejectedStaleStage);

        Assert.Equal(
            2, hub.GetAdmissionRejectedCount(RuntimeStateAdmission.RejectedStaleSourceGeneration));
        Assert.Equal(1, hub.GetAdmissionRejectedCount(RuntimeStateAdmission.RejectedStaleChannel));
        Assert.Equal(1, hub.GetAdmissionRejectedCount(RuntimeStateAdmission.RejectedStaleStage));
        Assert.Equal(4, hub.TotalAdmissionRejectedCount);
        Assert.Empty(hub.Snapshot());

        IReadOnlyDictionary<RuntimeStateAdmission, long> counts = hub.AdmissionRejectedCounts();
        Assert.Equal(2, counts[RuntimeStateAdmission.RejectedStaleSourceGeneration]);
    }

    [Fact]
    public void RecordAdmissionRejected_rejects_the_accepted_reason()
    {
        var hub = new RuntimeEventHub();
        Assert.Throws<ArgumentException>(
            () => hub.RecordAdmissionRejected(RuntimeStateAdmission.Accepted));
    }

    [Fact]
    public void Ring_buffer_caps_at_its_bound_and_evicts_oldest_first()
    {
        var hub = new RuntimeEventHub();
        int overflow = 50;
        int totalPublished = RuntimeEventHub.RingBufferCapacity + overflow;

        for (int i = 0; i < totalPublished; i++)
        {
            hub.PublishOcrRecognized(Ocr(i));
        }

        IReadOnlyList<RuntimeHubEvent> snapshot = hub.Snapshot();
        Assert.Equal(RuntimeEventHub.RingBufferCapacity, snapshot.Count);

        // The oldest surviving entry is the (overflow)-th published (0-indexed); everything before
        // it was evicted, and ordering is oldest-first.
        var oldest = Assert.IsType<LiveOcrRecognized>(snapshot[0].Payload);
        Assert.Equal(overflow, oldest.SourceGeneration);

        var newest = Assert.IsType<LiveOcrRecognized>(snapshot[^1].Payload);
        Assert.Equal(totalPublished - 1, newest.SourceGeneration);
    }
}
