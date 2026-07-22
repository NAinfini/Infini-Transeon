using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class OverlayStateCoordinatorTests
{
    [Fact]
    public void ReservesStableSlotsAndRejectsLateSourceGeneration()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        var coordinator = new OverlayStateCoordinator(epoch, target);
        TranslationChannelDefinition[] channels = [Channel(0, "Primary"), Channel(1, "Compare")];
        SourceGenerationToken first = Source(epoch, target, regionId, 1);

        OverlayDesiredState initial = coordinator.BeginRegion(
            regionId, first, new OverlayPixelRect(10, 20, 300, 100), Style(), channels);

        OverlayRegionSnapshot initialRegion = Assert.Single(initial.Regions);
        Assert.Equal(2, initialRegion.OrderedSlots.Count);
        Assert.All(initialRegion.OrderedSlots, slot => Assert.Equal(OverlaySlotState.Waiting, slot.State));
        Assert.True(coordinator.TryApply(Output(first, channels[0], "你", completed: false), out OverlayDesiredState? streaming));
        Assert.Equal(OverlaySlotState.Streaming, streaming!.Regions[0].OrderedSlots[0].State);

        SourceGenerationToken second = Source(epoch, target, regionId, 2);
        OverlayDesiredState replacement = coordinator.BeginRegion(
            regionId, second, new OverlayPixelRect(10, 20, 300, 100), Style(), channels);
        Assert.True(replacement.OverlayRevision > streaming.OverlayRevision);
        Assert.False(coordinator.TryApply(
            Output(first, channels[0], "旧译文", completed: true), out _));
        Assert.True(coordinator.TryApply(
            Output(second, channels[0], "新译文", completed: true), out OverlayDesiredState? completed));
        OverlaySlotSnapshot slot = completed!.Regions[0].OrderedSlots[0];
        Assert.Equal("新译文", slot.Text);
        Assert.Equal(OverlaySlotState.Success, slot.State);
        Assert.Equal(1, slot.StageIndex);
    }

    [Fact]
    public void RejectsOutOfOrderStageEventsAndTracksOnlyValidAcknowledgements()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        var coordinator = new OverlayStateCoordinator(epoch, target);
        TranslationChannelDefinition channel = Channel(0, "Primary");
        SourceGenerationToken source = Source(epoch, target, regionId, 1);
        coordinator.BeginRegion(
            regionId, source, new OverlayPixelRect(0, 0, 200, 80), Style(), [channel]);
        TranslationOutput newer = Output(source, channel, "润色", completed: true, stageIndex: 2);
        Assert.True(coordinator.TryApply(newer, out OverlayDesiredState? state));
        Assert.False(coordinator.TryApply(
            Output(source, channel, "迟到初译", completed: true, stageIndex: 1), out _));

        Assert.False(coordinator.TryAcknowledge(new RuntimeOverlayAcknowledgement(
            target, state!.OverlayRevision + 1, true, RuntimeOverlayApplyStatus.Applied, null, null)));
        Assert.True(coordinator.TryAcknowledge(new RuntimeOverlayAcknowledgement(
            target, state.OverlayRevision, true, RuntimeOverlayApplyStatus.Applied, null, null)));
        Assert.Equal(state.OverlayRevision, coordinator.LastAcknowledgedRevision);
    }

    [Fact]
    public void RefinementWaitsForConfiguredInitialTranslationDwell()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new OverlayStateCoordinator(epoch, target, clock);
        TranslationChannelDefinition channel = Channel(0, "Primary");
        SourceGenerationToken source = Source(epoch, target, regionId, 1);
        Guid runId = Guid.NewGuid();
        coordinator.BeginRegion(
            regionId, source, new OverlayPixelRect(0, 0, 200, 80), Style(), [channel]);
        Assert.True(coordinator.TryApply(
            Output(source, channel, "初译", completed: true, stageIndex: 1, channelRunId: runId), out _));

        clock.Advance(TimeSpan.FromMilliseconds(200));
        TranslationOutput refinement = Output(
            source, channel, "润色", completed: true, stageIndex: 2, channelRunId: runId);

        Assert.Equal(
            TimeSpan.FromMilliseconds(450),
            coordinator.GetRefinementDelay(refinement, TimeSpan.FromMilliseconds(650)));
        clock.Advance(TimeSpan.FromMilliseconds(450));
        Assert.Equal(TimeSpan.Zero,
            coordinator.GetRefinementDelay(refinement, TimeSpan.FromMilliseconds(650)));
    }

    private static TranslationChannelDefinition Channel(int order, string label)
    {
        Guid slotId = Guid.NewGuid();
        return new TranslationChannelDefinition(
            new TranslationChannelId(Guid.NewGuid()),
            "provider",
            [],
            [],
            new ContextPolicy(true, true, true),
            new CachePolicy(true, false, false),
            new DisplaySlotDefinition(slotId, order, label));
    }

    private static SourceGenerationToken Source(
        Guid epoch,
        TargetInstanceId target,
        Guid regionId,
        long generation) => new(
            epoch,
            target,
            CaptureAreaKey.UserRegion(new RegionId(regionId)),
            new TextTrackId(Guid.NewGuid()),
            generation,
            1);

    private static OverlayRegionStyleSnapshot Style() => new(
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

    private static TranslationOutput Output(
        SourceGenerationToken source,
        TranslationChannelDefinition channel,
        string text,
        bool completed,
        int stageIndex = 1,
        Guid? channelRunId = null)
    {
        var channelToken = new ChannelExecutionToken(
            source, channel.Id, channelRunId ?? Guid.NewGuid(), channel.DisplaySlot.SlotId);
        var stage = new StageExecutionToken(channelToken, Guid.NewGuid(), stageIndex, 1, 1);
        return new TranslationOutput(
            channel.Id,
            stage,
            channel.DisplaySlot.SlotId,
            stage.StageId,
            stageIndex,
            1,
            stageIndex == 1 ? TranslationStage.Initial : TranslationStage.Refinement,
            text,
            "provider",
            TimeSpan.Zero,
            null,
            null,
            false,
            completed,
            null,
            null,
            null);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
