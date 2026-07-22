using System.Diagnostics;
using System.Runtime.CompilerServices;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeEngineHostSupervisorTests
{
    [Fact]
    public void RestartBudgetIsFiniteWithinItsWindow()
    {
        var budget = new RuntimeRestartBudget(new RuntimeEngineHostRestartPolicy(
            maxAttempts: 2,
            window: TimeSpan.FromSeconds(10),
            initialDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromSeconds(1)));
        DateTimeOffset start = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        Assert.True(budget.TryReserve(start, out TimeSpan firstDelay));
        Assert.Equal(TimeSpan.FromMilliseconds(100), firstDelay);
        Assert.True(budget.TryReserve(start.AddSeconds(1), out TimeSpan secondDelay));
        Assert.Equal(TimeSpan.FromMilliseconds(200), secondDelay);
        Assert.False(budget.TryReserve(start.AddSeconds(2), out _));

        Assert.True(budget.TryReserve(start.AddSeconds(12), out TimeSpan resetDelay));
        Assert.Equal(TimeSpan.FromMilliseconds(100), resetDelay);
    }

    [Fact]
    public void ZeroDelayRemainsZeroForEveryConfiguredAttempt()
    {
        var budget = new RuntimeRestartBudget(new RuntimeEngineHostRestartPolicy(
            maxAttempts: 1_100,
            window: TimeSpan.FromHours(1),
            initialDelay: TimeSpan.Zero,
            maxDelay: TimeSpan.Zero));
        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        for (int attempt = 0; attempt < 1_100; ++attempt)
        {
            Assert.True(budget.TryReserve(now, out TimeSpan delay));
            Assert.Equal(TimeSpan.Zero, delay);
        }
    }

    [Fact]
    public async Task RestartsCrashedEngineHostWithANewProcessAndEpochThenStopsAtLimit()
    {
        await using var supervisor = new RuntimeEngineHostSupervisor(
            FindEngineHostExecutable(),
            TimeSpan.FromSeconds(10),
            new RuntimeEngineHostRestartPolicy(
                maxAttempts: 1,
                window: TimeSpan.FromMinutes(1),
                initialDelay: TimeSpan.Zero,
                maxDelay: TimeSpan.Zero));
        RuntimeEngineHostSession first = await supervisor.StartAsync(
            TestContext.Current.CancellationToken);
        int firstProcessId = first.ProcessId;
        Guid firstEpoch = first.RuntimeEpoch;

        KillAndWait(firstProcessId);
        RuntimeEngineHostSession second = await supervisor.RestartAfterUnexpectedExitAsync(
            TestContext.Current.CancellationToken);

        Assert.NotEqual(firstProcessId, second.ProcessId);
        Assert.NotEqual(firstEpoch, second.RuntimeEpoch);
        KillAndWait(second.ProcessId);
        await Assert.ThrowsAsync<RuntimeRestartLimitExceededException>(async () =>
            await supervisor.RestartAfterUnexpectedExitAsync(
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestartsAfterBackendAlreadyDisposedTheOwnedSession()
    {
        await using var supervisor = new RuntimeEngineHostSupervisor(
            FindEngineHostExecutable(),
            TimeSpan.FromSeconds(10),
            new RuntimeEngineHostRestartPolicy(
                maxAttempts: 1,
                window: TimeSpan.FromMinutes(1),
                initialDelay: TimeSpan.Zero,
                maxDelay: TimeSpan.Zero));
        RuntimeEngineHostSession first = await supervisor.StartAsync(
            TestContext.Current.CancellationToken);
        Guid firstEpoch = first.RuntimeEpoch;

        await first.DisposeAsync();
        RuntimeEngineHostSession second = await supervisor.RestartAfterRuntimeFailureAsync(
            TestContext.Current.CancellationToken);

        Assert.NotEqual(firstEpoch, second.RuntimeEpoch);
        Assert.False(second.HasExited);
    }

    [Fact]
    public async Task RecoveryCoordinatorReportsFailureAndRebuildsBackendForNewEpoch()
    {
        var firstSession = new FakeSession();
        var secondSession = new FakeSession();
        var firstRun = new FakeBackendRun(
            Task.FromException(new InvalidOperationException("pipe failed")));
        var secondCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = new FakeBackendRun(secondCompletion.Task);
        int restarts = 0;
        var failures = new List<string>();
        var runs = new Queue<FakeBackendRun>([firstRun, secondRun]);
        await using var recovery = new RuntimeBackendRecoveryCoordinator(
            _ => ValueTask.FromResult<IRuntimeEngineHostSession>(firstSession),
            _ =>
            {
                restarts++;
                return ValueTask.FromResult<IRuntimeEngineHostSession>(secondSession);
            },
            _ => runs.Dequeue(),
            (failure, _) =>
            {
                failures.Add(failure.Message);
                return ValueTask.CompletedTask;
            });

        recovery.Start();
        await secondRun.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, restarts);
        Assert.Equal(["pipe failed"], failures);
        Assert.True(firstRun.Disposed);
        Assert.True(firstSession.Disposed);
        Assert.NotEqual(firstSession.RuntimeEpoch, secondSession.RuntimeEpoch);
    }

    [Fact]
    public async Task RecoveryCoordinatorReportsInitialLaunchFailureBeforeStopping()
    {
        var failures = new List<string>();
        var recovery = new RuntimeBackendRecoveryCoordinator(
            _ => ValueTask.FromException<IRuntimeEngineHostSession>(
                new InvalidOperationException("launch failed")),
            _ => throw new InvalidOperationException("restart must not run"),
            _ => throw new InvalidOperationException("backend must not be created"),
            (failure, _) =>
            {
                failures.Add(failure.Message);
                return ValueTask.CompletedTask;
            });

        try
        {
            recovery.Start();
            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => recovery.Completion);

            Assert.Equal("launch failed", failure.Message);
            Assert.Equal(["launch failed"], failures);
        }
        finally
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => recovery.DisposeAsync().AsTask());
        }
    }

    private static void KillAndWait(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(5_000));
    }

    private static string FindEngineHostExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string[] candidates =
        [
            Path.Combine(
                directory.FullName,
                "artifacts", "cmake", $"ninja-{configuration.ToLowerInvariant()}",
                "src", "InfiniTranseon.EngineHost", "InfiniTranseon.EngineHost.exe"),
            Path.Combine(
                directory.FullName,
                "artifacts", "cmake", "windows-x64",
                "src", "InfiniTranseon.EngineHost", configuration,
                "InfiniTranseon.EngineHost.exe"),
        ];
        string? path = candidates.FirstOrDefault(File.Exists);
        Assert.True(path is not null,
            $"Build EngineHost before this test: {string.Join(" or ", candidates)}");
        return path!;
    }

    private sealed class FakeBackendRun(Task completion) : IRecoverableRuntimeBackend
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; } = completion;
        public bool Disposed { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession : IRuntimeEngineHostSession
    {
        public Guid RuntimeEpoch { get; } = Guid.NewGuid();
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<RuntimeEngineEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
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
        public ValueTask<RuntimeOcrResultAcknowledgement> SubmitOcrResultAsync(
            OcrResultSnapshot result, TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
