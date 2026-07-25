using System.Runtime.CompilerServices;
using System.Threading.Channels;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Settings;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeBackendCoordinatorTests
{
    [Fact]
    public async Task StartsCaptureBeforeProcessingAndRemovesTargetOnDispose()
    {
        ProfileDocument profile = Profile();
        ProfileTarget target = profile.Targets[0];
        var session = new FakeSession();
        var pipeline = new FakePipeline();
        var performance = new FakePerformanceController();
        RuntimeTargetBinding binding = Binding(profile, target);
        await using var coordinator = new RuntimeBackendCoordinator(
            session,
            profile,
            3,
            [binding],
            pipeline,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            performanceController: performance,
            reducedMotion: true);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["capture:Upsert", "processing"], session.Calls);
        Assert.Equal(binding.TargetInstanceId, Assert.Single(pipeline.Registered));
        Assert.True(Assert.Single(pipeline.RegisteredTargets).ReducedMotion);
        Assert.Equal(1, performance.StartCount);

        await coordinator.DisposeAsync();

        Assert.Equal("capture:Remove", session.Calls[^1]);
        Assert.Equal(binding.TargetInstanceId, Assert.Single(pipeline.Unregistered));
        Assert.True(session.Disposed);
        Assert.True(pipeline.Disposed);
        Assert.Equal(1, performance.DisposeCount);
    }

    [Fact]
    public async Task OneRuntimeSessionStartsTargetsFromTwoIndependentProfiles()
    {
        ProfileDocument firstProfile = Profile();
        ProfileDocument secondProfile = Profile();
        var session = new FakeSession();
        var pipeline = new FakePipeline();
        var firstPerformance = new FakePerformanceController();
        var secondPerformance = new FakePerformanceController();
        RuntimeTargetBinding first = Binding(firstProfile, firstProfile.Targets[0]);
        RuntimeTargetBinding second = Binding(secondProfile, secondProfile.Targets[0]);
        await using var coordinator = new RuntimeBackendCoordinator(
            session,
            [
                new RuntimeProfileBinding(firstProfile, 3, [first]),
                new RuntimeProfileBinding(secondProfile, 7, [second]),
            ],
            pipeline,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1),
            performanceControllers: [firstPerformance, secondPerformance]);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, pipeline.RegisteredTargets.Count);
        Assert.Equal(2, pipeline.RegisteredTargets
            .Select(target => target.Profile.ProfileId).Distinct().Count());
        Assert.Equal(2, session.Calls.Count(call => call == "capture:Upsert"));
        Assert.Equal(2, session.Calls.Count(call => call == "processing"));
        Assert.Equal(1, session.ReadEventCalls);
        Assert.Equal(1, firstPerformance.StartCount);
        Assert.Equal(1, secondPerformance.StartCount);

        await coordinator.DisposeAsync();

        Assert.Equal(1, firstPerformance.DisposeCount);
        Assert.Equal(1, secondPerformance.DisposeCount);
    }

    [Fact]
    public async Task OneRuntimeEventPumpRoutesOcrResultsForTwoIndependentProfiles()
    {
        ProfileDocument firstProfile = Profile();
        ProfileDocument secondProfile = Profile();
        var session = new FakeSession();
        var pipeline = new FakePipeline();
        RuntimeTargetBinding first = Binding(firstProfile, firstProfile.Targets[0]);
        RuntimeTargetBinding second = Binding(secondProfile, secondProfile.Targets[0]);
        await using var coordinator = new RuntimeBackendCoordinator(
            session,
            [
                new RuntimeProfileBinding(firstProfile, 3, [first]),
                new RuntimeProfileBinding(secondProfile, 7, [second]),
            ],
            pipeline,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1));
        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        session.PublishOcrResult(OcrResult(
            session.RuntimeEpoch, first, firstProfile.Targets[0].Regions[0], 3, "first"));
        session.PublishOcrResult(OcrResult(
            session.RuntimeEpoch, second, secondProfile.Targets[0].Regions[0], 7, "second"));
        OcrResultSnapshot[] received = await pipeline.WaitForResultsAsync(
            2, TestContext.Current.CancellationToken);

        Assert.Equal(1, session.ReadEventCalls);
        Assert.Equal(
            [first.TargetInstanceId, second.TargetInstanceId],
            received.Select(result => result.ExecutionToken.Source.TargetInstanceId));
        Assert.Equal([3L, 7L],
            received.Select(result => result.ExecutionToken.Source.ProfileRevision));
    }

    [Fact]
    public async Task ProcessingRejectionRollsBackRegisteredCapture()
    {
        ProfileDocument profile = Profile();
        ProfileTarget target = profile.Targets[0];
        var session = new FakeSession { RejectProcessing = true };
        var pipeline = new FakePipeline();
        RuntimeTargetBinding binding = Binding(profile, target);
        await using var coordinator = new RuntimeBackendCoordinator(
            session,
            profile,
            3,
            [binding],
            pipeline,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1));

        RuntimeBackendStartException error = await Assert.ThrowsAsync<RuntimeBackendStartException>(
            () => coordinator.StartAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("runtime.processing.rejectedForTest", error.ErrorCode);
        Assert.Equal(["capture:Upsert", "processing", "capture:Remove"], session.Calls);
        Assert.Equal(binding.TargetInstanceId, Assert.Single(pipeline.Unregistered));
    }

    [Fact]
    public async Task HotApplyUpdatesProcessingAndPipelineWithoutRestartingCapture()
    {
        ProfileDocument profile = Profile();
        RuntimeTargetBinding current = Binding(profile, profile.Targets[0]);
        var session = new FakeSession();
        var pipeline = new FakePipeline();
        await using var coordinator = new RuntimeBackendCoordinator(
            session,
            profile,
            3,
            [current],
            pipeline,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1));
        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        ProfileRegion changedRegion = profile.Targets[0].Regions[0] with
        {
            RecognitionInterval = TimeSpan.FromMilliseconds(900),
            Bounds = new NormalizedRect(0.2, 0.6, 0.7, 0.25),
        };
        ProfileTarget changedTarget = profile.Targets[0] with
        {
            Regions = [changedRegion],
        };
        ProfileDocument changedProfile = profile with
        {
            Targets = [changedTarget],
        };
        RuntimeTargetBinding changedBinding = new(
            changedTarget,
            current.TargetInstanceId,
            current.NativeHandle,
            current.DesktopRegion,
            current.TargetPixelWidth,
            current.TargetPixelHeight,
            current.CommandRevision,
            configurationRevision: 2,
            current.RunOptions);

        await coordinator.ApplyProfileAsync(
            new RuntimeProfileBinding(changedProfile, 4, [changedBinding]),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, session.Calls.Count(call => call == "capture:Upsert"));
        Assert.Equal(2, session.Calls.Count(call => call == "processing"));
        RuntimeTranslationTarget replacement = Assert.Single(pipeline.ReplacedTargets);
        Assert.Equal(4, replacement.ProfileRevision);
        Assert.Equal(900, replacement.ProfileTarget.Regions[0]
            .RecognitionInterval.TotalMilliseconds);
    }

    [Fact]
    public async Task SecondProfileFailureRollsBackBothProfilesInReverseOrder()
    {
        ProfileDocument firstProfile = Profile();
        ProfileDocument secondProfile = Profile();
        var session = new FakeSession { RejectProcessingCall = 2 };
        var pipeline = new FakePipeline();
        RuntimeTargetBinding first = Binding(firstProfile, firstProfile.Targets[0]);
        RuntimeTargetBinding second = Binding(secondProfile, secondProfile.Targets[0]);
        await using var coordinator = new RuntimeBackendCoordinator(
            session,
            [
                new RuntimeProfileBinding(firstProfile, 3, [first]),
                new RuntimeProfileBinding(secondProfile, 7, [second]),
            ],
            pipeline,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1));

        RuntimeBackendStartException error = await Assert.ThrowsAsync<RuntimeBackendStartException>(
            () => coordinator.StartAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("runtime.processing.rejectedForTest", error.ErrorCode);
        Assert.Equal(
            ["capture:Upsert", "processing", "capture:Upsert", "processing",
                "capture:Remove", "capture:Remove"],
            session.Calls);
        Assert.Equal(
            [second.TargetInstanceId, first.TargetInstanceId],
            pipeline.Unregistered);
    }

    [Fact]
    public async Task DisposeKeepsEventReaderAliveUntilTargetRemovalCompletes()
    {
        ProfileDocument profile = Profile();
        var session = new FakeSession();
        var pipeline = new FakePipeline();
        RuntimeTargetBinding binding = Binding(profile, profile.Targets[0]);
        var coordinator = new RuntimeBackendCoordinator(
            session,
            profile,
            3,
            [binding],
            pipeline,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1));
        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        await coordinator.DisposeAsync();

        Assert.False(session.EventReadCancellationObservedDuringRemoval);
    }

    [Fact]
    public async Task StartupRollbackPreservesPerformanceAndCaptureCleanupFailures()
    {
        ProfileDocument profile = Profile();
        ProfileTarget target = profile.Targets[0];
        var session = new FakeSession { RejectFirstRemoval = true };
        var pipeline = new FakePipeline();
        var performance = new FakePerformanceController
        {
            ThrowOnStart = true,
            ThrowOnFirstDispose = true,
        };
        RuntimeTargetBinding binding = Binding(profile, target);
        var coordinator = new RuntimeBackendCoordinator(
            session,
            profile,
            3,
            [binding],
            pipeline,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1),
            performance);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => coordinator.StartAsync(TestContext.Current.CancellationToken).AsTask());
        string[] messages = failure.Flatten().InnerExceptions
            .Select(exception => exception.Message).ToArray();

        Assert.Contains("performance start failed", messages);
        Assert.Contains("performance dispose failed", messages);
        Assert.Contains("runtime.capture.removeRejectedForTest", messages);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task PerformanceFactoryMapsProfileAndAppliesPolicyThroughRuntimeSession()
    {
        ProfileDocument profile = Profile();
        ProfileRegion original = profile.Targets[0].Regions[0];
        profile.Targets[0].Regions[0] = original with
        {
            Priority = RegionPriority.P3,
            LockDegradation = false,
        };
        var session = new FakeSession();
        DegradationEvent? reported = null;
        var settings = new PerformanceRuntimeSettings
        {
            Preset = PerformancePreset.Eco,
            OverloadSamples = 1,
            RecoverySamples = 1,
            MinimumDwell = TimeSpan.Zero,
        };
        await using RuntimePerformanceController controller = RuntimePerformanceFactory.Create(
            session,
            profile,
            profileRevision: 3,
            settings,
            new StubPerformanceSource(new PerformanceSnapshot(
                95, 700_000_000, 20, 30, 900, 60, false)),
            (change, _) =>
            {
                reported = change;
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        DegradationEvent? observed = await controller.ObserveOnceAsync(
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.Equal(observed, reported);
        Assert.Contains("policy", session.Calls);
    }

    private static ProfileDocument Profile()
    {
        ProfileDocument profile = ProfileDocument.Create("Game", "ja", "en");
        ProfileTarget target = ProfileTarget.Create("Window", CaptureTargetKind.Window);
        target.Regions.Add(ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0.1, 0.7, 0.8, 0.2)));
        profile.Targets.Add(target);
        return profile;
    }

    private static RuntimeTargetBinding Binding(ProfileDocument profile, ProfileTarget target) => new(
        target,
        new TargetInstanceId(Guid.NewGuid()),
        nativeHandle: 1,
        desktopRegion: null,
        targetPixelWidth: 1920,
        targetPixelHeight: 1080,
        commandRevision: 1,
        configurationRevision: 1,
        new TranslationRunOptions(
            profile.ProfileId,
            new TranslationContext(null, null, null, null, [], []),
            [],
            TimeSpan.FromSeconds(10),
            4096,
            2048,
            false));

    private static OcrResultSnapshot OcrResult(
        Guid runtimeEpoch,
        RuntimeTargetBinding binding,
        ProfileRegion region,
        long profileRevision,
        string text)
    {
        var source = new SourceGenerationToken(
            runtimeEpoch,
            binding.TargetInstanceId,
            CaptureAreaKey.UserRegion(new RegionId(region.RegionId)),
            new TextTrackId(Guid.NewGuid()),
            1,
            profileRevision);
        return new OcrResultSnapshot(
            new OcrExecutionToken(source, Guid.NewGuid(), 1, 1),
            [new TextLine(text, new NormalizedRect(0, 0, 1, 1), 1)],
            "test",
            "1",
            true,
            null);
    }

    private sealed class FakePipeline : IRuntimeTranslationPipeline
    {
        private readonly Channel<OcrResultSnapshot> _results =
            Channel.CreateUnbounded<OcrResultSnapshot>();
        public List<RuntimeTranslationTarget> RegisteredTargets { get; } = [];
        public IReadOnlyList<TargetInstanceId> Registered =>
            RegisteredTargets.Select(target => target.TargetInstanceId).ToArray();
        public List<TargetInstanceId> Unregistered { get; } = [];
        public List<RuntimeTranslationTarget> ReplacedTargets { get; } = [];
        public bool Disposed { get; private set; }

        public void Register(RuntimeTranslationTarget target) => RegisteredTargets.Add(target);
        public ValueTask ReplaceAsync(
            RuntimeTranslationTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplacedTargets.Add(target);
            return ValueTask.CompletedTask;
        }
        public void Unregister(TargetInstanceId targetInstanceId) => Unregistered.Add(targetInstanceId);
        public ValueTask EnqueueAsync(OcrResultSnapshot result, CancellationToken cancellationToken) =>
            _results.Writer.WriteAsync(result, cancellationToken);
        public async Task<OcrResultSnapshot[]> WaitForResultsAsync(
            int count,
            CancellationToken cancellationToken)
        {
            var results = new OcrResultSnapshot[count];
            for (int index = 0; index < count; index++)
                results[index] = await _results.Reader.ReadAsync(cancellationToken);
            return results;
        }
        public Task DrainAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePerformanceController : IRuntimePerformanceController
    {
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ThrowOnStart { get; init; }
        public bool ThrowOnFirstDispose { get; init; }
        public Task Completion => Task.CompletedTask;
        public void Start()
        {
            StartCount++;
            if (ThrowOnStart) throw new InvalidOperationException("performance start failed");
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (ThrowOnFirstDispose && DisposeCount == 1)
                throw new InvalidOperationException("performance dispose failed");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubPerformanceSource(PerformanceSnapshot snapshot)
        : IPerformanceSnapshotSource
    {
        public ValueTask<PerformanceSnapshot> SampleAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class FakeSession : IRuntimeEngineHostSession
    {
        private readonly Channel<RuntimeEngineEvent> _events = Channel.CreateUnbounded<RuntimeEngineEvent>();

        public Guid RuntimeEpoch { get; } = Guid.NewGuid();
        public List<string> Calls { get; } = [];
        public bool RejectProcessing { get; init; }
        public int RejectProcessingCall { get; init; } = int.MaxValue;
        public bool RejectFirstRemoval { get; init; }
        private bool _removalRejected;
        private int _processingCalls;
        private CancellationToken _eventReadCancellation;
        public bool Disposed { get; private set; }
        public int ReadEventCalls { get; private set; }
        public bool EventReadCancellationObservedDuringRemoval { get; private set; }

        public void PublishOcrResult(OcrResultSnapshot result)
        {
            byte[] payload = RuntimeOcrResultPayloadCodec.Encode(result);
            if (!_events.Writer.TryWrite(new RuntimeEngineEvent(
                    RuntimeMessageKind.OcrResult,
                    Guid.NewGuid(),
                    RuntimeEpoch,
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    payload)))
                throw new InvalidOperationException("runtime.test.eventRejected");
        }

        public async IAsyncEnumerable<RuntimeEngineEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReadEventCalls++;
            _eventReadCancellation = cancellationToken;
            await foreach (RuntimeEngineEvent runtimeEvent in
                _events.Reader.ReadAllAsync(cancellationToken))
                yield return runtimeEvent;
        }

        public ValueTask<RuntimeCaptureTargetAcknowledgement> ApplyCaptureTargetAsync(
            RuntimeCaptureTargetCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"capture:{command.Operation}");
            if (command.Operation == RuntimeCaptureTargetOperation.Remove)
                EventReadCancellationObservedDuringRemoval =
                    _eventReadCancellation.IsCancellationRequested;
            bool reject = command.Operation == RuntimeCaptureTargetOperation.Remove &&
                RejectFirstRemoval && !_removalRejected;
            if (reject) _removalRejected = true;
            return ValueTask.FromResult(new RuntimeCaptureTargetAcknowledgement(
                command.CommandRevision,
                command.TargetId,
                command.TargetInstanceId,
                !reject,
                command.Operation == RuntimeCaptureTargetOperation.Upsert
                    ? TargetLifecycleState.Running
                    : TargetLifecycleState.Closed,
                reject ? "runtime.capture.removeRejectedForTest" : null));
        }

        public ValueTask<RuntimeProcessingConfigurationAcknowledgement>
            ApplyProcessingConfigurationAsync(
                RuntimeProcessingConfiguration configuration,
                TimeSpan timeout,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("processing");
            _processingCalls++;
            bool reject = RejectProcessing || _processingCalls == RejectProcessingCall;
            return ValueTask.FromResult(new RuntimeProcessingConfigurationAcknowledgement(
                configuration.TargetInstanceId,
                configuration.ConfigurationRevision,
                !reject,
                reject
                    ? RuntimeProcessingConfigurationStatus.InvalidConfiguration
                    : RuntimeProcessingConfigurationStatus.Applied,
                reject ? "runtime.processing.rejectedForTest" : null));
        }

        public ValueTask<RuntimeOverlayAcknowledgement> ApplyOverlayAsync(
            OverlayDesiredState state, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<PolicyAcknowledgement> ApplyPolicyAsync(
            PolicyRevision revision, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("policy");
            return ValueTask.FromResult(new PolicyAcknowledgement(revision.Revision, true, null));
        }
        public ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
            TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<RuntimeOcrResultAcknowledgement> SubmitOcrResultAsync(
            OcrResultSnapshot result, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
