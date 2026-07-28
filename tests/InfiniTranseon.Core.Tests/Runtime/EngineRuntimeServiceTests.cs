using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class EngineRuntimeServiceTests
{
    private static readonly RuntimeEngineHostRestartPolicy FastRestart =
        new(maxAttempts: 3, window: TimeSpan.FromMinutes(1),
            initialDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero);

    private static EngineHostLocateResult Found() =>
        new(@"C:\engine\InfiniTranseon.EngineHost.exe", [@"C:\engine\InfiniTranseon.EngineHost.exe"]);

    [Fact]
    public async Task StartTransitionsThroughStartingToRunning()
    {
        var harness = new Harness();
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);
        var recorder = new StatusRecorder(service);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineRuntimeStatus.Running, service.Status);
        Assert.Equal(
            [EngineRuntimeStatus.Locating, EngineRuntimeStatus.Starting, EngineRuntimeStatus.Running],
            recorder.Statuses);
        Assert.Equal(1, harness.Backends.Single().StartCount);
    }

    [Fact]
    public async Task StartWithoutExecutableEntersExecutableNotFoundStateCarryingPaths()
    {
        var harness = new Harness();
        string[] searched = [@"C:\a\host.exe", @"C:\b\host.exe"];
        await using var service = new EngineRuntimeService(
            new EngineHostLocateResult(null, searched),
            harness.SessionFactory, harness.BackendFactory, FastRestart);
        var recorder = new StatusRecorder(service);

        EngineHostNotFoundException exception =
            await Assert.ThrowsAsync<EngineHostNotFoundException>(
                async () => await service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(searched, exception.SearchedPaths);
        Assert.Equal(EngineRuntimeStatus.ExecutableNotFound, service.Status);
        EngineRuntimeStatusChange change = Assert.Single(recorder.Changes);
        Assert.Equal(EngineRuntimeStatus.ExecutableNotFound, change.Status);
        Assert.Equal(searched, change.SearchedPaths);
        Assert.Empty(harness.Backends);
    }

    [Fact]
    public async Task LaunchFailureFaultsAndPropagates()
    {
        var harness = new Harness { SessionLaunch = _ => throw new InvalidOperationException("boom") };
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EngineRuntimeStatus.Faulted, service.Status);
    }

    [Fact]
    public async Task FansBackendEventsOutToSubscribersAndTracksTargets()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        var harness = new Harness
        {
            RuntimeEpoch = epoch,
            OnBackendCreated = (publisher, _) =>
            {
                publisher.PublishTargetLifecycle(Lifecycle(target, TargetLifecycleState.Running));
                publisher.PublishOcrResult(OcrResult(epoch, target));
                publisher.PublishBudget(new RuntimeBudgetSnapshot(
                    RuntimeProtocol.CurrentVersion, epoch,
                    [new RuntimeBudgetPool("engine.committed.bytes", 100, 10, 0)]));
                publisher.PublishDiagnostic(new EngineDiagnostic(
                    "probe.diagnostic", "probe.diagnostic", RuntimeDiagnosticSeverity.Warning,
                    DateTimeOffset.UtcNow));
            },
        };
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);
        EngineOcrResultEvent? ocr = null;
        EngineBudgetEvent? budget = null;
        EngineDiagnostic? diagnostic = null;
        EngineTargetSnapshotEvent? targetEvent = null;
        service.OcrResultReceived += (_, e) => ocr = e;
        service.BudgetUpdated += (_, e) => budget = e;
        service.DiagnosticRaised += (_, e) => diagnostic = e;
        service.TargetsChanged += (_, e) => targetEvent = e;

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(ocr);
        Assert.NotNull(budget);
        Assert.Equal("probe.diagnostic", diagnostic!.ErrorCode);
        Assert.Equal(TargetLifecycleState.Running, targetEvent!.Lifecycle.Target.State);
        Assert.Equal(target, Assert.Single(service.TargetSnapshots).TargetInstanceId);
    }

    [Fact]
    public async Task ClosedTargetLifecycleRemovesTheSnapshot()
    {
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        var harness = new Harness
        {
            RuntimeEpoch = epoch,
            OnBackendCreated = (publisher, _) =>
            {
                publisher.PublishTargetLifecycle(Lifecycle(target, TargetLifecycleState.Running));
                publisher.PublishTargetLifecycle(Lifecycle(target, TargetLifecycleState.Closed));
            },
        };
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(service.TargetSnapshots);
    }

    [Fact]
    public async Task UnexpectedBackendFaultSurfacesRestartAndRecoversToRunning()
    {
        var harness = new Harness();
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);
        var recorder = new StatusRecorder(service);
        await service.StartAsync(TestContext.Current.CancellationToken);

        harness.Backends[0].Fault(new IOException("engine crashed"));
        await recorder.WaitForAsync(
            statuses => statuses.Count(status => status == EngineRuntimeStatus.Running) == 2,
            TestContext.Current.CancellationToken);

        Assert.Contains(EngineRuntimeStatus.Restarting, recorder.Statuses);
        Assert.Equal(2, harness.Backends.Count);
        Assert.Equal(1, harness.Backends[1].StartCount);
        Assert.True(harness.Backends[0].Disposed);
    }

    [Fact]
    public async Task ExhaustedRestartBudgetTransitionsToFaulted()
    {
        var policy = new RuntimeEngineHostRestartPolicy(
            maxAttempts: 1, window: TimeSpan.FromMinutes(1),
            initialDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero);
        var harness = new Harness();
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, policy);
        var recorder = new StatusRecorder(service);
        await service.StartAsync(TestContext.Current.CancellationToken);

        harness.Backends[0].Fault(new IOException("first crash"));
        await recorder.WaitForAsync(
            statuses => statuses.Count(status => status == EngineRuntimeStatus.Running) == 2,
            TestContext.Current.CancellationToken);
        harness.Backends[1].Fault(new IOException("second crash"));
        await recorder.WaitForAsync(
            statuses => statuses.Contains(EngineRuntimeStatus.Faulted),
            TestContext.Current.CancellationToken);

        Assert.Equal(EngineRuntimeStatus.Faulted, service.Status);
    }

    [Fact]
    public async Task DisposeTearsDownTheBackendAndItsSession()
    {
        var harness = new Harness();
        var ownedResource = new TrackedDisposable();
        var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart,
            lifetimeResource: ownedResource);
        await service.StartAsync(TestContext.Current.CancellationToken);
        ScriptedSession session = harness.Sessions.Single();
        FakeBackend backend = harness.Backends.Single();

        await service.DisposeAsync();

        Assert.True(backend.Disposed);
        Assert.True(session.Disposed);
        Assert.True(ownedResource.Disposed);
    }

    [Fact]
    public async Task StopBeforeStartReportsStopped()
    {
        var harness = new Harness();
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineRuntimeStatus.Stopped, service.Status);
    }

    [Fact]
    public async Task ControlOperationsBeforeRunningThrowNotRunning()
    {
        var harness = new Harness();
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await service.PauseAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal("engine.runtime.notRunning", exception.Message);
    }

    [Fact]
    public async Task ControlOperationsDelegateToTheActiveBackendControl()
    {
        var harness = new Harness();
        await using var service = new EngineRuntimeService(
            Found(), harness.SessionFactory, harness.BackendFactory, FastRestart);
        await service.StartAsync(TestContext.Current.CancellationToken);
        FakeControl control = harness.Controls.Single();

        await service.PauseAllAsync(TestContext.Current.CancellationToken);
        await service.ResumeAllAsync(TestContext.Current.CancellationToken);
        await service.SetOverlayVisibleAsync(false, TestContext.Current.CancellationToken);
        var target = new TargetInstanceId(Guid.NewGuid());
        await service.SetTargetsPausedAsync(
            [target],
            true,
            TestContext.Current.CancellationToken);
        await service.SetTargetsOverlayVisibleAsync(
            [target],
            false,
            TestContext.Current.CancellationToken);
        await service.RequestManualOcrAsync(
            RuntimeManualOcrRequest.Explicit([target]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "pause",
                "resume",
                "overlay:false",
                $"targets.pause:true:{target.Value:D}",
                $"targets.overlay:false:{target.Value:D}",
                $"targets.ocr:{target.Value:D}",
            ],
            control.Calls);
    }

    private static TargetLifecycleEvent Lifecycle(
        TargetInstanceId instance, TargetLifecycleState state) => new(
            new TargetSnapshot(
                instance, new CaptureTargetId(Guid.NewGuid()), state, 1920, 1080, 96),
            1,
            DateTimeOffset.UtcNow,
            null);

    private static OcrResultSnapshot OcrResult(Guid epoch, TargetInstanceId target)
    {
        var source = new SourceGenerationToken(
            epoch, target, CaptureAreaKey.FullTarget, new TextTrackId(Guid.NewGuid()), 1, 1);
        return new OcrResultSnapshot(
            new OcrExecutionToken(source, Guid.NewGuid(), 1, 1),
            [new TextLine("hi", new NormalizedRect(0, 0, 1, 1), 1)],
            "model", "1", true, null);
    }

    private sealed class Harness
    {
        public Guid RuntimeEpoch { get; init; } = Guid.NewGuid();
        public Func<CancellationToken, IRuntimeEngineHostSession>? SessionLaunch { get; init; }
        public Action<IEngineRuntimeEventPublisher, FakeBackend>? OnBackendCreated { get; init; }
        public List<ScriptedSession> Sessions { get; } = [];
        public List<FakeBackend> Backends { get; } = [];
        public List<FakeControl> Controls { get; } = [];

        public ValueTask<IRuntimeEngineHostSession> SessionFactory(CancellationToken cancellationToken)
        {
            if (SessionLaunch is not null)
            {
                return ValueTask.FromResult(SessionLaunch(cancellationToken));
            }
            var session = new ScriptedSession(RuntimeEpoch);
            lock (Sessions) Sessions.Add(session);
            return ValueTask.FromResult<IRuntimeEngineHostSession>(session);
        }

        public ValueTask<EngineRuntimeBackendSession> BackendFactory(
            IRuntimeEngineHostSession session,
            IEngineRuntimeEventPublisher publisher,
            CancellationToken cancellationToken)
        {
            var backend = new FakeBackend(session);
            var control = new FakeControl();
            lock (Backends) Backends.Add(backend);
            lock (Controls) Controls.Add(control);
            OnBackendCreated?.Invoke(publisher, backend);
            return ValueTask.FromResult(new EngineRuntimeBackendSession(backend, control));
        }
    }

    private sealed class FakeBackend(IRuntimeEngineHostSession session) : IRecoverableRuntimeBackend
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task Completion => _completion.Task;

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public void Fault(Exception exception) => _completion.TrySetException(exception);

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            _completion.TrySetResult();
            await session.DisposeAsync();
        }
    }

    private sealed class FakeControl :
        IEngineRuntimeControl,
        ITargetScopedEngineRuntimeControl
    {
        public List<string> Calls { get; } = [];

        public ValueTask PauseAllAsync(CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add("pause");
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAllAsync(CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add("resume");
            return ValueTask.CompletedTask;
        }

        public ValueTask SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add($"overlay:{visible.ToString().ToLowerInvariant()}");
            return ValueTask.CompletedTask;
        }

        public ValueTask RequestManualOcrAsync(CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add("manual");
            return ValueTask.CompletedTask;
        }

        public ValueTask SetTargetsPausedAsync(
            IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
            bool paused,
            CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(
                    $"targets.pause:{paused.ToString().ToLowerInvariant()}:" +
                    string.Join(",", targetInstanceIds.Select(target => target.Value.ToString("D"))));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask SetTargetsOverlayVisibleAsync(
            IReadOnlyCollection<TargetInstanceId> targetInstanceIds,
            bool visible,
            CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(
                    $"targets.overlay:{visible.ToString().ToLowerInvariant()}:" +
                    string.Join(",", targetInstanceIds.Select(target => target.Value.ToString("D"))));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask RequestManualOcrAsync(
            RuntimeManualOcrRequest request,
            CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add("targets.ocr:" + string.Join(
                    ",",
                    request.TargetInstanceIds.Select(target => target.Value.ToString("D"))));
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ScriptedSession(Guid epoch) : IRuntimeEngineHostSession
    {
        private int _disposed;

        public Guid RuntimeEpoch { get; } = epoch;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public async IAsyncEnumerable<RuntimeEngineEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
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

        public ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RuntimeOcrResultAcknowledgement> SubmitOcrResultAsync(
            OcrResultSnapshot result, TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StatusRecorder
    {
        private readonly object _gate = new();
        private readonly List<EngineRuntimeStatusChange> _changes = [];
        private TaskCompletionSource _signal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StatusRecorder(EngineRuntimeService service) =>
            service.StatusChanged += OnStatusChanged;

        public IReadOnlyList<EngineRuntimeStatusChange> Changes
        {
            get { lock (_gate) return _changes.ToArray(); }
        }

        public IReadOnlyList<EngineRuntimeStatus> Statuses
        {
            get { lock (_gate) return _changes.Select(change => change.Status).ToArray(); }
        }

        public async Task WaitForAsync(
            Func<IReadOnlyList<EngineRuntimeStatus>, bool> predicate,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (predicate(_changes.Select(change => change.Status).ToArray()))
                    {
                        return;
                    }
                    signal = _signal.Task;
                }
                await signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        private void OnStatusChanged(object? sender, EngineRuntimeStatusChange change)
        {
            lock (_gate)
            {
                _changes.Add(change);
                TaskCompletionSource previous = _signal;
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                previous.TrySetResult();
            }
        }
    }
}
