using InfiniTranseon.App.State;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Tests;

public sealed class RuntimeStateStoreTests
{
    private static readonly Guid Epoch = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly TargetInstanceId Target = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly TextTrackId Track = new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
    private static readonly TranslationChannelId Channel = new(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"));
    private static readonly Guid Slot = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private static SourceGenerationToken Source(long generation, long profileRevision = 1, Guid? epoch = null) =>
        new(epoch ?? Epoch, Target, CaptureAreaKey.FullTarget, Track, generation, profileRevision);

    private static ChannelExecutionToken ChannelRun(SourceGenerationToken source, Guid runId) =>
        new(source, Channel, runId, Slot);

    private static StageExecutionToken Stage(
        ChannelExecutionToken channel, int stageSequence, int attempt, long streamSequence, Guid? stageId = null) =>
        new(channel, stageId ?? Guid.Parse("11112222-3333-4444-5555-666677778888"), stageSequence, attempt, streamSequence);

    [Fact]
    public void First_source_generation_is_accepted()
    {
        var store = new RuntimeStateStore();
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitSourceGeneration(Source(1)));
    }

    [Fact]
    public void Newer_source_generation_supersedes_older()
    {
        var store = new RuntimeStateStore();
        store.AdmitSourceGeneration(Source(1));
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitSourceGeneration(Source(2)));
    }

    [Fact]
    public void Out_of_order_older_source_generation_is_rejected()
    {
        var store = new RuntimeStateStore();
        store.AdmitSourceGeneration(Source(2));
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleSourceGeneration,
            store.AdmitSourceGeneration(Source(1)));
    }

    [Fact]
    public void Duplicate_source_generation_is_rejected()
    {
        var store = new RuntimeStateStore();
        store.AdmitSourceGeneration(Source(3));
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleSourceGeneration,
            store.AdmitSourceGeneration(Source(3)));
    }

    [Fact]
    public void Higher_profile_revision_supersedes_current_generation()
    {
        var store = new RuntimeStateStore();
        store.AdmitSourceGeneration(Source(5, profileRevision: 1));
        Assert.Equal(
            RuntimeStateAdmission.Accepted,
            store.AdmitSourceGeneration(Source(1, profileRevision: 2)));
    }

    [Fact]
    public void Current_hierarchy_translation_is_accepted()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken source = Source(1);
        store.AdmitSourceGeneration(source);
        ChannelExecutionToken run = ChannelRun(source, Guid.NewGuid());
        store.AdmitChannelRun(run);
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(run, 1, 1, 1)));
    }

    [Fact]
    public void Translation_for_stale_generation_is_rejected()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken oldSource = Source(1);
        store.AdmitSourceGeneration(oldSource);
        store.AdmitSourceGeneration(Source(2));

        ChannelExecutionToken staleRun = ChannelRun(oldSource, Guid.NewGuid());
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleSourceGeneration,
            store.AdmitTranslation(Stage(staleRun, 1, 1, 1)));
    }

    [Fact]
    public void Late_result_from_superseded_channel_run_is_rejected()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken source = Source(1);
        store.AdmitSourceGeneration(source);

        ChannelExecutionToken runA = ChannelRun(source, Guid.NewGuid());
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(runA, 1, 1, 1)));

        // Translator-group switch establishes a new run for the same display slot.
        ChannelExecutionToken runB = ChannelRun(source, Guid.NewGuid());
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitChannelRun(runB));

        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleChannel,
            store.AdmitTranslation(Stage(runA, 1, 1, 2)));
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(runB, 1, 1, 1)));
    }

    [Fact]
    public void Out_of_order_stream_chunk_is_rejected_but_progress_is_accepted()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken source = Source(1);
        store.AdmitSourceGeneration(source);
        ChannelExecutionToken run = ChannelRun(source, Guid.NewGuid());

        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(run, 1, 1, 1)));
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(run, 1, 1, 2)));
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleStage,
            store.AdmitTranslation(Stage(run, 1, 1, 1)));
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleStage,
            store.AdmitTranslation(Stage(run, 1, 1, 2)));
    }

    [Fact]
    public void Refinement_stage_supersedes_earlier_stage_and_old_stage_is_rejected()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken source = Source(1);
        store.AdmitSourceGeneration(source);
        ChannelExecutionToken run = ChannelRun(source, Guid.NewGuid());
        Guid initialStage = Guid.NewGuid();
        Guid refineStage = Guid.NewGuid();

        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(run, 1, 1, 5, initialStage)));
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(run, 2, 1, 1, refineStage)));
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleStage,
            store.AdmitTranslation(Stage(run, 1, 1, 6, initialStage)));
    }

    [Fact]
    public void New_attempt_supersedes_prior_attempt_of_same_stage()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken source = Source(1);
        store.AdmitSourceGeneration(source);
        ChannelExecutionToken run = ChannelRun(source, Guid.NewGuid());

        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(run, 1, 1, 3)));
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitTranslation(Stage(run, 1, 2, 1)));
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleStage,
            store.AdmitTranslation(Stage(run, 1, 1, 4)));
    }

    [Fact]
    public void Events_from_old_runtime_epoch_are_rejected_after_reconnect()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken oldSource = Source(1);
        store.AdmitSourceGeneration(oldSource);

        Guid newEpoch = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        store.AdvanceEpoch(newEpoch);

        Assert.Equal(newEpoch, store.CurrentEpoch);
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleSourceGeneration,
            store.AdmitSourceGeneration(oldSource));

        ChannelExecutionToken oldRun = ChannelRun(oldSource, Guid.NewGuid());
        Assert.Equal(
            RuntimeStateAdmission.RejectedStaleSourceGeneration,
            store.AdmitTranslation(Stage(oldRun, 1, 1, 1)));
    }

    [Fact]
    public void Different_tracks_advance_independently()
    {
        var store = new RuntimeStateStore();
        SourceGenerationToken fullTarget = Source(2);
        var region = new RegionId(Guid.NewGuid());
        var regionSource = new SourceGenerationToken(
            Epoch, Target, CaptureAreaKey.UserRegion(region), Track, 1, 1);

        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitSourceGeneration(fullTarget));
        // A different capture area is a distinct track; generation 1 is fresh for it.
        Assert.Equal(RuntimeStateAdmission.Accepted, store.AdmitSourceGeneration(regionSource));
    }
}
