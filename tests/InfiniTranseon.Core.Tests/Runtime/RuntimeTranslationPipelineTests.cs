using System.Runtime.CompilerServices;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeTranslationPipelineTests
{
    [Fact]
    public async Task DisposeDoesNotHideAPipelineFailureWhenFailureReportingAlsoFails()
    {
        const string providerId = "test.provider";
        using var providers = new OnlineProviderService(
            new ProviderRegistry(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(providerId, ProviderKind.Translation, false,
                        false, false, false),
                    () => new PrefixProvider("译:")),
            ]),
            new ProviderServiceLimits());
        var reportAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            new ThrowingOverlaySink(),
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (_, _) =>
            {
                reportAttempted.TrySetResult();
                return ValueTask.FromException(
                    new InvalidOperationException("failure reporter unavailable"));
            });
        RuntimeTranslationTarget registration = Target(providerId);
        pipeline.Register(registration);
        await pipeline.EnqueueAsync(
            Result(registration, "text"), TestContext.Current.CancellationToken);
        await reportAttempted.Task.WaitAsync(
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => pipeline.DisposeAsync().AsTask());

        Assert.Contains(failure.Flatten().InnerExceptions, exception =>
            exception.Message == "failure reporter unavailable");
    }

    [Fact]
    public async Task StableOcrFlowsThroughTranslationIntoFixedOverlaySlot()
    {
        const string providerId = "test.provider";
        using var providers = new OnlineProviderService(
            new ProviderRegistry(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(
                        providerId,
                        ProviderKind.Translation,
                        RequiresNetwork: false,
                        SupportsStreaming: true,
                        SupportsContext: true,
                        SupportsGlossary: false),
                    () => new PrefixProvider("译:")),
            ]),
            new ProviderServiceLimits());
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(providers));
        var sink = new RecordingOverlaySink();
        var records = new RecordingTranslationRecordSink();
        var failures = new List<RuntimePipelineFailure>();
        await using var pipeline = new RuntimeTranslationPipeline(
            orchestrator,
            sink,
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (failure, _) =>
            {
                lock (failures) failures.Add(failure);
                return ValueTask.CompletedTask;
            },
            records);
        RuntimeTranslationTarget registration = Target(providerId);
        pipeline.Register(registration);

        await pipeline.EnqueueAsync(
            Result(registration, "攻撃:100"),
            TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(failures);
        Assert.True(sink.States.Count >= 2);
        OverlaySlotSnapshot initial = Assert.Single(sink.States.First().Regions.Single().OrderedSlots);
        OverlaySlotSnapshot final = Assert.Single(sink.States.Last().Regions.Single().OrderedSlots);
        Assert.Equal(OverlaySlotState.Waiting, initial.State);
        Assert.Equal(OverlaySlotState.Success, final.State);
        Assert.Equal("译:攻撃:100", final.Text);
        Assert.True(sink.States.Last().OverlayRevision > sink.States.First().OverlayRevision);
        Assert.Single(records.Items);
        Assert.Equal("攻撃:100", records.Items[0].Source.SourceText);
        Assert.Contains(records.Items[0].Outputs, item =>
            item.StreamCompleted && item.Text == "译:攻撃:100");
    }

    [Fact]
    public async Task EmptyOcrClearsTheOverlayAndTheSameTextCanAppearAgain()
    {
        const string providerId = "test.provider";
        using var providers = new OnlineProviderService(
            new ProviderRegistry(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(providerId, ProviderKind.Translation, false,
                        true, true, false),
                    () => new PrefixProvider("译:")),
            ]),
            new ProviderServiceLimits());
        var sink = new RecordingOverlaySink();
        var records = new RecordingTranslationRecordSink();
        await using var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            sink,
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (_, _) => ValueTask.CompletedTask,
            records);
        RuntimeTranslationTarget registration = Target(providerId);
        pipeline.Register(registration);
        OcrResultSnapshot appeared = Result(registration, "同じ文");
        SourceGenerationToken source = appeared.ExecutionToken.Source;

        await pipeline.EnqueueAsync(appeared, TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);
        var disappearedSource = WithGeneration(source, 2);
        await pipeline.EnqueueAsync(
            appeared with
            {
                ExecutionToken = new OcrExecutionToken(
                    disappearedSource, Guid.NewGuid(), 1, 1),
                Lines = Array.Empty<TextLine>(),
            },
            TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sink.States.Last().Regions);

        var returnedSource = WithGeneration(source, 3);
        await pipeline.EnqueueAsync(
            appeared with
            {
                ExecutionToken = new OcrExecutionToken(
                    returnedSource, Guid.NewGuid(), 1, 1),
            },
            TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(sink.States.Last().Regions);
        Assert.Equal(2, records.Items.Count);

        static SourceGenerationToken WithGeneration(
            SourceGenerationToken value,
            long generation) => new(
                value.RuntimeEpoch,
                value.TargetInstanceId,
                value.Area,
                value.TextTrackId,
                generation,
                value.ProfileRevision);
    }

    [Fact]
    public async Task UnknownTargetIsReportedWithoutStartingProviderWork()
    {
        using var providers = new OnlineProviderService(
            new ProviderRegistry([]),
            new ProviderServiceLimits());
        var failures = new List<RuntimePipelineFailure>();
        await using var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            new RecordingOverlaySink(),
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (failure, _) =>
            {
                failures.Add(failure);
                return ValueTask.CompletedTask;
            });
        RuntimeTranslationTarget registration = Target("missing");

        await pipeline.EnqueueAsync(
            Result(registration, "text"),
            TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);

        RuntimePipelineFailure failure = Assert.Single(failures);
        Assert.Equal("pipeline.targetNotRegistered", failure.ErrorCode);
    }

    [Fact]
    public async Task RemainingAreaUsesDetectedBoundsAndExcludesUserRegions()
    {
        const string providerId = "test.provider";
        using var providers = new OnlineProviderService(
            new ProviderRegistry(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(providerId, ProviderKind.Translation, false,
                        true, true, false),
                    () => new PrefixProvider("译:")),
            ]),
            new ProviderServiceLimits());
        var sink = new RecordingOverlaySink();
        var records = new RecordingTranslationRecordSink();
        await using var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            sink,
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (_, _) => ValueTask.CompletedTask,
            records);
        RuntimeTranslationTarget registration = RemainingTarget(providerId);
        pipeline.Register(registration);

        var source = new SourceGenerationToken(
            registration.RuntimeEpoch,
            registration.TargetInstanceId,
            CaptureAreaKey.RemainingArea,
            new TextTrackId(Guid.NewGuid()),
            1,
            registration.ProfileRevision);
        await pipeline.EnqueueAsync(
            new OcrResultSnapshot(
                new OcrExecutionToken(source, Guid.NewGuid(), 1, 1),
                [
                    new TextLine("duplicate", new NormalizedRect(0.2, 0.75, 0.2, 0.05), 0.9),
                    new TextLine("menu", new NormalizedRect(0.8, 0.1, 0.1, 0.05), 0.9),
                    new TextLine("quest", new NormalizedRect(0.55, 0.2, 0.2, 0.05), 0.9),
                ],
                "test.ocr", "1", true, null),
            TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["menu", "quest"],
            records.Items.Select(item => item.Source.SourceText).Order().ToArray());
        Assert.Equal(2, sink.States.Last().Regions.Count);
        OverlayRegionSnapshot overlay = Assert.Single(
            sink.States.Last().Regions,
            item => item.Bounds.X == 1536);
        Assert.NotEqual(source.TextTrackId.Value, overlay.RegionId);
        Assert.Equal(new OverlayPixelRect(1536, 108, 192, 54), overlay.Bounds);
    }

    [Fact]
    public async Task RemainingAreaRemovesAutomaticTracksThatDisappear()
    {
        const string providerId = "test.provider";
        using var providers = new OnlineProviderService(
            new ProviderRegistry(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(providerId, ProviderKind.Translation, false,
                        true, true, false),
                    () => new PrefixProvider("译:")),
            ]),
            new ProviderServiceLimits());
        var sink = new RecordingOverlaySink();
        await using var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            sink,
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (_, _) => ValueTask.CompletedTask);
        RuntimeTranslationTarget registration = RemainingTarget(providerId);
        pipeline.Register(registration);
        var source = new SourceGenerationToken(
            registration.RuntimeEpoch,
            registration.TargetInstanceId,
            CaptureAreaKey.RemainingArea,
            new TextTrackId(Guid.NewGuid()),
            1,
            registration.ProfileRevision);
        var first = new OcrResultSnapshot(
            new OcrExecutionToken(source, Guid.NewGuid(), 1, 1),
            [
                new TextLine("menu", new NormalizedRect(0.8, 0.1, 0.1, 0.05), 0.9),
                new TextLine("quest", new NormalizedRect(0.55, 0.2, 0.2, 0.05), 0.9),
            ],
            "test.ocr", "1", true, null);

        await pipeline.EnqueueAsync(first, TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, sink.States.Last().Regions.Count);

        var secondSource = new SourceGenerationToken(
            source.RuntimeEpoch,
            source.TargetInstanceId,
            source.Area,
            source.TextTrackId,
            2,
            source.ProfileRevision);
        await pipeline.EnqueueAsync(
            first with
            {
                ExecutionToken = new OcrExecutionToken(
                    secondSource, Guid.NewGuid(), 1, 1),
                Lines = Array.AsReadOnly(
                ([new TextLine("menu", new NormalizedRect(0.8, 0.1, 0.1, 0.05), 0.9)])),
            },
            TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);

        OverlayRegionSnapshot remaining = Assert.Single(sink.States.Last().Regions);
        Assert.Equal(source.TextTrackId.Value, remaining.RegionId);
        Assert.Equal("译:menu", Assert.Single(remaining.OrderedSlots).Text);
    }

    [Fact]
    public async Task ActivePerformancePolicyPausesOnlyOptionalRefinement()
    {
        const string providerId = "test.provider";
        int calls = 0;
        using var providers = new OnlineProviderService(
            new ProviderRegistry(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(providerId, ProviderKind.LargeLanguageModel, false,
                        true, true, false),
                    () => new CountingProvider(() => calls++)),
            ]),
            new ProviderServiceLimits());
        RuntimeTranslationTarget original = Target(providerId);
        ProfileRegion originalRegion = Assert.Single(original.ProfileTarget.Regions);
        ProfileTranslationChannel originalChannel = Assert.Single(
            originalRegion.TranslationChannels);
        ProfileRegion region = originalRegion with
        {
            TranslationChannels =
            [
                originalChannel with
                {
                    RefinementSteps =
                    [
                        new ProfileRefinementStep
                        {
                            ProviderId = providerId,
                            PromptTemplateId = "polish",
                        },
                    ],
                },
            ],
        };
        ProfileTarget profileTarget = original.ProfileTarget with { Regions = [region] };
        ProfileDocument profile = original.Profile with { Targets = [profileTarget] };
        var registration = new RuntimeTranslationTarget(
            original.RuntimeEpoch, original.TargetInstanceId, original.CaptureTargetId,
            original.ProfileRevision, profile, profileTarget,
            original.TargetPixelWidth, original.TargetPixelHeight, original.RunOptions);
        await using var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            new RecordingOverlaySink(),
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (_, _) => ValueTask.CompletedTask,
            degradationPolicy: new PausedRefinementPolicy(region.RegionId));
        pipeline.Register(registration);

        await pipeline.EnqueueAsync(
            Result(registration, "text"), TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RecentSessionDataClearsOnlyAfterLastTargetForProfileStops()
    {
        using var providers = new OnlineProviderService(
            new ProviderRegistry([]), new ProviderServiceLimits());
        var records = new RecordingTranslationRecordSink();
        await using var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            new RecordingOverlaySink(),
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (_, _) => ValueTask.CompletedTask,
            records);
        RuntimeTranslationTarget first = Target("unused");
        var second = new RuntimeTranslationTarget(
            first.RuntimeEpoch,
            new TargetInstanceId(Guid.NewGuid()),
            first.CaptureTargetId,
            first.ProfileRevision,
            first.Profile,
            first.ProfileTarget,
            first.TargetPixelWidth,
            first.TargetPixelHeight,
            first.RunOptions);
        pipeline.Register(first);
        pipeline.Register(second);

        pipeline.Unregister(first.TargetInstanceId);
        Assert.Empty(records.StoppedProfiles);
        pipeline.Unregister(second.TargetInstanceId);
        Assert.Equal([first.Profile.ProfileId], records.StoppedProfiles);
    }

    [Fact]
    public async Task Replay_reports_exact_visible_and_waiting_target_counts()
    {
        using var providers = new OnlineProviderService(
            new ProviderRegistry([]), new ProviderServiceLimits());
        await using var pipeline = new RuntimeTranslationPipeline(
            new TranslationOrchestrator(new TranslationChannelRunner(providers)),
            new RecordingOverlaySink(),
            new OcrTextGenerationGate(new TextStabilizerOptions(
                1, TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            (_, _) => ValueTask.CompletedTask);
        RuntimeTranslationTarget original = Target("unused");
        pipeline.Register(original);

        RuntimeVisibleReplayResult waiting = await pipeline.ReplayVisibleAsync(
            [original.TargetInstanceId], TestContext.Current.CancellationToken);
        Assert.Equal(0, waiting.ReplayedTargetCount);
        Assert.Equal(1, waiting.WaitingTargetCount);

        await pipeline.EnqueueAsync(Result(original, "visible"), TestContext.Current.CancellationToken);
        RuntimeTranslationTarget replacement = new(
            original.RuntimeEpoch,
            original.TargetInstanceId,
            original.CaptureTargetId,
            original.ProfileRevision + 1,
            original.Profile,
            original.ProfileTarget,
            original.TargetPixelWidth,
            original.TargetPixelHeight,
            original.RunOptions);
        await pipeline.ReplaceAsync(replacement, TestContext.Current.CancellationToken);

        RuntimeVisibleReplayResult replayed = await pipeline.ReplayVisibleAsync(
            [replacement.TargetInstanceId], TestContext.Current.CancellationToken);
        Assert.Equal(1, replayed.ReplayedTargetCount);
        Assert.Equal(0, replayed.WaitingTargetCount);
    }

    private static RuntimeTranslationTarget Target(string providerId)
    {
        Guid epoch = Guid.NewGuid();
        var instance = new TargetInstanceId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        ProfileTranslationChannel channel = ProfileTranslationChannel.Create(providerId) with
        {
            ChannelId = Guid.NewGuid(),
            DisplayOrder = 0,
            DisplayLabel = "Primary",
            RetryCount = 0,
        };
        ProfileRegion region = ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0.1, 0.7, 0.8, 0.2)) with
        {
            RegionId = regionId,
            TranslationChannels = [channel],
        };
        ProfileTarget target = ProfileTarget.Create("Game", CaptureTargetKind.Window) with
        {
            TargetId = Guid.NewGuid(),
            Regions = [region],
        };
        ProfileDocument profile = ProfileDocument.Create("Game", "ja", "zh-Hans") with
        {
            ProfileId = Guid.NewGuid(),
            Targets = [target],
        };
        return new RuntimeTranslationTarget(
            epoch,
            instance,
            new CaptureTargetId(target.TargetId),
            profileRevision: 1,
            profile,
            target,
            targetPixelWidth: 1920,
            targetPixelHeight: 1080,
            ProfileTranslationFactory.CreateRunOptions(
                profile,
                scene: null,
                speaker: null,
                recentSource: [],
                recentTranslation: [],
                attemptTimeout: TimeSpan.FromSeconds(2),
                maximumOutputCharacters: 1000,
                maximumOutputTokens: 500));
    }

    private static RuntimeTranslationTarget RemainingTarget(string providerId)
    {
        RuntimeTranslationTarget baseTarget = Target(providerId);
        ProfileRegion user = Assert.Single(baseTarget.ProfileTarget.Regions);
        ProfileTranslationChannel channel = Assert.Single(user.TranslationChannels);
        ProfileRegion automatic = ProfileRegion.Create(
            "Automatic", new NormalizedRect(0, 0, 1, 1)) with
        {
            AreaMode = CaptureAreaKind.RemainingArea,
            TranslationChannels = [channel with { ChannelId = Guid.NewGuid() }],
        };
        ProfileTarget profileTarget = baseTarget.ProfileTarget with
        {
            ScanRemainingArea = true,
            RemainingAreaRegion = automatic,
        };
        ProfileDocument profile = baseTarget.Profile with { Targets = [profileTarget] };
        return new RuntimeTranslationTarget(
            baseTarget.RuntimeEpoch,
            baseTarget.TargetInstanceId,
            baseTarget.CaptureTargetId,
            baseTarget.ProfileRevision,
            profile,
            profileTarget,
            baseTarget.TargetPixelWidth,
            baseTarget.TargetPixelHeight,
            baseTarget.RunOptions);
    }

    private static OcrResultSnapshot Result(RuntimeTranslationTarget target, string text)
    {
        ProfileRegion region = Assert.Single(target.ProfileTarget.Regions);
        var source = new SourceGenerationToken(
            target.RuntimeEpoch,
            target.TargetInstanceId,
            CaptureAreaKey.UserRegion(new RegionId(region.RegionId)),
            new TextTrackId(Guid.NewGuid()),
            1,
            target.ProfileRevision);
        return new OcrResultSnapshot(
            new OcrExecutionToken(source, Guid.NewGuid(), 1, 1),
            [new TextLine(text, new NormalizedRect(0, 0, 1, 1), 0.9)],
            "test.ocr",
            "1",
            true,
            null);
    }

    private sealed class RecordingOverlaySink : IRuntimeOverlaySink
    {
        private readonly object _gate = new();
        private readonly List<OverlayDesiredState> _states = [];
        public IReadOnlyList<OverlayDesiredState> States
        {
            get { lock (_gate) return _states.ToArray(); }
        }

        public ValueTask ApplyAsync(
            OverlayDesiredState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _states.Add(state);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingOverlaySink : IRuntimeOverlaySink
    {
        public ValueTask ApplyAsync(
            OverlayDesiredState state,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("overlay pipe unavailable"));
    }

    private sealed class PrefixProvider(string prefix) : ITranslationProvider
    {
        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = prefix + request.SourceText;
            yield return new ProviderDelta(1, text);
            await Task.Yield();
            yield return new ProviderDone(1, ProviderUsage.None);
        }
    }

    private sealed class CountingProvider(Action count) : ITranslationProvider
    {
        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count();
            yield return new ProviderDelta(1, request.SourceText);
            await Task.Yield();
            yield return new ProviderDone(1, ProviderUsage.None);
        }
    }

    private sealed class PausedRefinementPolicy(Guid regionId) : ITranslationDegradationPolicy
    {
        public bool ShouldPauseOptionalRefinement(Guid candidate) => candidate == regionId;
    }

    private sealed class RecordingTranslationRecordSink :
        IRuntimeTranslationRecordSink,
        IRuntimeTranslationSessionSink
    {
        private readonly object _gate = new();
        private readonly List<(Guid ProfileId, TextGeneration Source,
            IReadOnlyList<TranslationOutput> Outputs)> _items = [];
        public IReadOnlyList<(Guid ProfileId, TextGeneration Source,
            IReadOnlyList<TranslationOutput> Outputs)> Items
        {
            get { lock (_gate) return _items.ToArray(); }
        }
        public List<Guid> StoppedProfiles { get; } = [];

        public ValueTask SaveAsync(
            Guid profileId,
            TextGeneration source,
            IReadOnlyList<TranslationOutput> outputs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _items.Add((profileId, source, outputs));
            return ValueTask.CompletedTask;
        }

        public void ProfileStopped(Guid profileId) => StoppedProfiles.Add(profileId);
    }
}
