using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class EngineRuntimePipelineWiringTests
{
    [Fact]
    public async Task PausablePipelinePublishesOcrAndForwardsWhenRunning()
    {
        var inner = new RecordingPipeline();
        var publisher = new RecordingPublisher();
        var pipeline = new PausableRuntimeTranslationPipeline(inner, publisher);
        OcrResultSnapshot result = OcrResult();

        await pipeline.EnqueueAsync(result, TestContext.Current.CancellationToken);

        Assert.Same(result, Assert.Single(publisher.OcrResults));
        Assert.Same(result, Assert.Single(inner.Enqueued));
    }

    [Fact]
    public async Task PausablePipelineSuppressesForwardingButStillPublishesWhilePaused()
    {
        var inner = new RecordingPipeline();
        var publisher = new RecordingPublisher();
        var pipeline = new PausableRuntimeTranslationPipeline(inner, publisher);
        pipeline.Pause();

        await pipeline.EnqueueAsync(OcrResult(), TestContext.Current.CancellationToken);

        Assert.True(pipeline.IsPaused);
        Assert.Single(publisher.OcrResults);
        Assert.Empty(inner.Enqueued);

        pipeline.Resume();
        await pipeline.EnqueueAsync(OcrResult(), TestContext.Current.CancellationToken);
        Assert.Single(inner.Enqueued);
    }

    [Fact]
    public async Task VisibilityGatingOverlaySinkClearsRegionsWhenHidden()
    {
        var inner = new RecordingOverlaySink();
        var sink = new VisibilityGatingOverlaySink(inner);
        OverlayDesiredState visible = Overlay(withRegion: true);

        await sink.ApplyAsync(visible, TestContext.Current.CancellationToken);
        Assert.Single(inner.Applied[^1].Regions);

        sink.SetVisible(false);
        await sink.ApplyAsync(Overlay(withRegion: true), TestContext.Current.CancellationToken);

        Assert.False(sink.IsVisible);
        Assert.Empty(inner.Applied[^1].Regions);
    }

    [Fact]
    public async Task TranslationRecordSinkPublishesEveryOutput()
    {
        var publisher = new RecordingPublisher();
        var sink = new EngineRuntimeTranslationRecordSink(publisher);
        Guid profileId = Guid.NewGuid();

        await sink.SaveAsync(
            profileId,
            Generation(),
            [Output("one"), Output("two")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, publisher.TranslationOutputs.Count);
        Assert.All(publisher.TranslationOutputs, entry => Assert.Equal(profileId, entry.ProfileId));
    }

    [Fact]
    public async Task DefaultControlDrivesDecoratorsAndSchedulesManualOcr()
    {
        var pausePipeline = new PausableRuntimeTranslationPipeline(new RecordingPipeline());
        var overlaySink = new VisibilityGatingOverlaySink(new RecordingOverlaySink());
        var session = new RecordingSession();
        var control = new DefaultEngineRuntimeControl(
            pausePipeline,
            overlaySink,
            session,
            TimeSpan.FromSeconds(2));

        await control.PauseAllAsync(TestContext.Current.CancellationToken);
        Assert.True(pausePipeline.IsPaused);
        await control.ResumeAllAsync(TestContext.Current.CancellationToken);
        Assert.False(pausePipeline.IsPaused);
        await control.SetOverlayVisibleAsync(false, TestContext.Current.CancellationToken);
        Assert.False(overlaySink.IsVisible);
        await control.RequestManualOcrAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, session.ManualOcrRequests);
    }

    [Fact]
    public async Task DefaultControlSurfacesManualOcrRejection()
    {
        var session = new RecordingSession
        {
            ManualOcrAcknowledgement = new RuntimeManualOcrAcknowledgement(
                false,
                RuntimeManualOcrStatus.Busy,
                0,
                0,
                "ocr.manual.busy"),
        };
        var control = new DefaultEngineRuntimeControl(
            new PausableRuntimeTranslationPipeline(new RecordingPipeline()),
            new VisibilityGatingOverlaySink(new RecordingOverlaySink()),
            session,
            TimeSpan.FromSeconds(2));

        EngineRuntimeCommandRejectedException exception =
            await Assert.ThrowsAsync<EngineRuntimeCommandRejectedException>(
                async () => await control.RequestManualOcrAsync(
                    TestContext.Current.CancellationToken));
        Assert.Equal("manualOcr", exception.OperationKey);
        Assert.Equal("ocr.manual.busy", exception.ErrorCode);
    }

    private static OcrResultSnapshot OcrResult()
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(), new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.FullTarget, new TextTrackId(Guid.NewGuid()), 1, 1);
        return new OcrResultSnapshot(
            new OcrExecutionToken(source, Guid.NewGuid(), 1, 1),
            [new TextLine("t", new NormalizedRect(0, 0, 1, 1), 1)],
            "model", "1", true, null);
    }

    private static OverlayDesiredState Overlay(bool withRegion)
    {
        var target = new TargetInstanceId(Guid.NewGuid());
        IEnumerable<OverlayRegionSnapshot> regions = withRegion
            ? [new OverlayRegionSnapshot(
                Guid.NewGuid(),
                new OverlayPixelRect(0, 0, 200, 80),
                new OverlayRegionStyleSnapshot(
                    OverlayBackgroundTreatment.Replacement,
                    OverlayTextAlignment.Left,
                    "#FF000000", "#FFFFFFFF", 1, 0, 4, 16, 12, 3),
                [])]
            : [];
        return new OverlayDesiredState(Guid.NewGuid(), target, 1, regions);
    }

    private static TextGeneration Generation()
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(), new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.FullTarget, new TextTrackId(Guid.NewGuid()), 1, 1);
        return new TextGeneration(
            new CaptureTargetId(Guid.NewGuid()),
            source,
            new SourceEventId(Guid.NewGuid()),
            new NormalizedRect(0, 0, 1, 1),
            "source",
            [new TextLine("source", new NormalizedRect(0, 0, 1, 1), 1)],
            DateTimeOffset.UtcNow,
            1,
            1);
    }

    private static TranslationOutput Output(string text)
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(), new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.FullTarget, new TextTrackId(Guid.NewGuid()), 1, 1);
        var channelId = new TranslationChannelId(Guid.NewGuid());
        var slot = Guid.NewGuid();
        var channel = new ChannelExecutionToken(source, channelId, Guid.NewGuid(), slot);
        var stage = new StageExecutionToken(channel, Guid.NewGuid(), 1, 1, 1);
        return new TranslationOutput(
            channelId, stage, slot, stage.StageId, 0, 1,
            TranslationStage.Initial, text, "provider", TimeSpan.FromMilliseconds(5),
            null, null, false, true, null, null, null);
    }

    private sealed class RecordingPipeline : IRuntimeTranslationPipeline
    {
        public List<OcrResultSnapshot> Enqueued { get; } = [];

        public void Register(RuntimeTranslationTarget target) { }
        public void Unregister(TargetInstanceId targetInstanceId) { }
        public ValueTask EnqueueAsync(OcrResultSnapshot result, CancellationToken cancellationToken)
        {
            Enqueued.Add(result);
            return ValueTask.CompletedTask;
        }
        public Task DrainAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingOverlaySink : IRuntimeOverlaySink
    {
        public List<OverlayDesiredState> Applied { get; } = [];
        public ValueTask ApplyAsync(OverlayDesiredState state, CancellationToken cancellationToken)
        {
            Applied.Add(state);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPublisher : IEngineRuntimeEventPublisher
    {
        public List<OcrResultSnapshot> OcrResults { get; } = [];
        public List<(Guid ProfileId, TranslationOutput Output)> TranslationOutputs { get; } = [];

        public void PublishOcrResult(OcrResultSnapshot result) => OcrResults.Add(result);
        public void PublishTranslationOutput(Guid profileId, TranslationOutput output) =>
            TranslationOutputs.Add((profileId, output));
        public void PublishTargetLifecycle(TargetLifecycleEvent lifecycle) { }
        public void PublishBudget(RuntimeBudgetSnapshot snapshot) { }
        public void PublishDiagnostic(EngineDiagnostic diagnostic) { }
    }

    private sealed class RecordingSession : IRuntimeEngineHostSession
    {
        public Guid RuntimeEpoch { get; } = Guid.NewGuid();
        public int ManualOcrRequests { get; private set; }
        public RuntimeManualOcrAcknowledgement ManualOcrAcknowledgement { get; init; } =
            new(true, RuntimeManualOcrStatus.Scheduled, 1, 1, null);

        public async IAsyncEnumerable<RuntimeEngineEvent> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManualOcrRequests++;
            return ValueTask.FromResult(ManualOcrAcknowledgement);
        }

        public ValueTask<RuntimeCaptureTargetAcknowledgement> ApplyCaptureTargetAsync(
            RuntimeCaptureTargetCommand command, TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<RuntimeOverlayAcknowledgement> ApplyOverlayAsync(
            OverlayDesiredState state, TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PolicyAcknowledgement> ApplyPolicyAsync(
            PolicyRevision revision, TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<RuntimeProcessingConfigurationAcknowledgement>
            ApplyProcessingConfigurationAsync(
                RuntimeProcessingConfiguration configuration, TimeSpan timeout,
                CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<RuntimeOcrResultAcknowledgement> SubmitOcrResultAsync(
            OcrResultSnapshot result, TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
