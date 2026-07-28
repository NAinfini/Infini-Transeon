using System.Diagnostics;
using System.Runtime.InteropServices;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeEngineHostLauncherTests
{
    [Fact]
    public void RuntimeEventOwnsAndClearsSensitivePayload()
    {
        byte[] source = [1, 2, 3, 4];
        var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.TranslationOutput,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddSeconds(1),
            source);
        ReadOnlyMemory<byte> owned = runtimeEvent.Payload;
        source[0] = 99;

        Assert.Equal(1, owned.Span[0]);

        runtimeEvent.Dispose();

        Assert.True(runtimeEvent.IsDisposed);
        Assert.All(owned.ToArray(), value => Assert.Equal(0, value));
        Assert.True(runtimeEvent.Payload.IsEmpty);
    }

    [Fact]
    public async Task LaunchesAuthenticatedEngineHostAndTerminatesItWithTheSession()
    {
        string executablePath = FindEngineHostExecutable();

        int processId;
        await using (RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                executablePath,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken))
        {
            processId = session.ProcessId;
            Assert.True(processId > 0);
            Assert.Equal(processId, session.Connection.AuthenticatedServerProcessId);
            Assert.NotEqual(Guid.Empty, session.RuntimeEpoch);
            Assert.False(session.HasExited);
            Assert.True(session.IsSupervised);
            RuntimeBudgetSnapshot budget = session.AppBudgetSnapshot;
            Assert.Equal(session.RuntimeEpoch, budget.RuntimeEpoch);
            RuntimeBudgetPool ipc = Assert.Single(budget.Pools);
            Assert.Equal("app.ipc.inflight.bytes", ipc.Name);
            Assert.Equal(
                session.Connection.Capabilities.MaxIpcInFlightBytes,
                ipc.Limit);
            await using IAsyncEnumerator<RuntimeEngineEvent> events =
                session.ReadEventsAsync(TestContext.Current.CancellationToken)
                    .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            using RuntimeEngineEvent runtimeEvent = events.Current;
            Assert.Equal(RuntimeMessageKind.RuntimeBudgetSnapshot, runtimeEvent.MessageKind);
            RuntimeBudgetSnapshot engineBudget =
                RuntimeBudgetSnapshotPayloadCodec.Decode(runtimeEvent.Payload.Span);
            Assert.Equal(session.RuntimeEpoch, engineBudget.RuntimeEpoch);
            Assert.Contains(engineBudget.Pools,
                pool => pool.Name == "engine.committed.bytes" &&
                    pool.Unit == RuntimeBudgetUnit.Bytes && pool.Committed > 0);
            Assert.Contains(engineBudget.Pools,
                pool => pool.Name == "engine.targets.slots" &&
                    pool.Unit == RuntimeBudgetUnit.Slots);
        }

        Assert.True(await WaitForExitAsync(processId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExchangesControlHeartbeatWithoutChangingTheRuntimeEpoch()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        Guid epoch = session.RuntimeEpoch;
        await session.PingAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(epoch, session.RuntimeEpoch);
        Assert.False(session.HasExited);
    }

    [Fact]
    public async Task ManualOcrWithoutTargetsReceivesExplicitRejection()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        RuntimeManualOcrAcknowledgement acknowledgement =
            await session.RequestManualOcrAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        Assert.False(acknowledgement.Accepted);
        Assert.Equal(RuntimeManualOcrStatus.NoTargets, acknowledgement.Status);
        Assert.Equal("ocr.manual.noTargets", acknowledgement.ErrorCode);
    }

    [Fact]
    public async Task ManualOcrWithAnExplicitUnknownTargetReceivesNoMatch()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        RuntimeManualOcrAcknowledgement acknowledgement =
            await session.RequestManualOcrAsync(
                RuntimeManualOcrRequest.Explicit([new TargetInstanceId(Guid.NewGuid())]),
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        Assert.False(acknowledgement.Accepted);
        Assert.Equal(RuntimeManualOcrStatus.TargetUnavailable, acknowledgement.Status);
        Assert.Equal("ocr.manual.noMatch", acknowledgement.ErrorCode);
        Assert.Equal(0, acknowledgement.TargetCount);
        Assert.Equal(0, acknowledgement.RegionCount);
    }

    [Fact]
    public async Task CorrelatesConcurrentRequestsThroughOneReceiveLoop()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        Task[] requests = Enumerable.Range(0, 16)
            .Select(_ => session.PingAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken).AsTask())
            .ToArray();

        await Task.WhenAll(requests);
        Assert.Equal(0, session.DroppedEventCount);
        Assert.False(session.HasExited);
    }

    [Fact]
    public async Task ShutdownWaitsForAcknowledgementAndEngineHostExit()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        await session.ShutdownAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(await WaitForExitAsync(session.ProcessId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CaptureTargetCommandReceivesCorrelatedExplicitAcknowledgement()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        var command = new RuntimeCaptureTargetCommand(
            RuntimeCaptureTargetOperation.Upsert,
            1,
            new CaptureTargetId(Guid.NewGuid()),
            new TargetInstanceId(Guid.NewGuid()),
            RuntimeCaptureTargetKind.Window,
            1,
            null);

        RuntimeCaptureTargetAcknowledgement acknowledgement =
            await session.ApplyCaptureTargetAsync(
                command,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        Assert.Equal(command.CommandRevision, acknowledgement.CommandRevision);
        Assert.Equal(command.TargetId, acknowledgement.TargetId);
        Assert.Equal(command.TargetInstanceId, acknowledgement.TargetInstanceId);
        Assert.False(acknowledgement.Accepted);
        Assert.Equal(TargetLifecycleState.Closed, acknowledgement.State);
        Assert.Equal("capture.targetUnavailable", acknowledgement.ErrorCode);

        await session.PingAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OverlayAndPolicyCommandsReceiveAppliedOrExplicitRejectedAcknowledgements()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        TargetInstanceId missingTarget = new(Guid.NewGuid());

        RuntimeOverlayAcknowledgement overlay = await session.ApplyOverlayAsync(
            new OverlayDesiredState(session.RuntimeEpoch, missingTarget, 1, []),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.False(overlay.Accepted);
        Assert.Equal(RuntimeOverlayApplyStatus.InvalidState, overlay.Status);
        Assert.Equal("overlay.targetUnavailable", overlay.ErrorCode);

        RegionId region = new(Guid.NewGuid());
        Guid profileId = Guid.NewGuid();
        PolicyAcknowledgement unavailable = await session.ApplyPolicyAsync(
            new PolicyRevision(1, profileId, 1, new Dictionary<RegionId, string>
            {
                [region] = "3:degraded",
            }),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(unavailable.Accepted);
        Assert.Equal("runtime.policy.profileUnavailable", unavailable.RejectionCode);

        PolicyAcknowledgement secondUnavailable = await session.ApplyPolicyAsync(
            new PolicyRevision(1, profileId, 1, new Dictionary<RegionId, string>
            {
                [region] = "3:configured",
            }),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(secondUnavailable.Accepted);
        Assert.Equal("runtime.policy.profileUnavailable", secondUnavailable.RejectionCode);

        await session.PingAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OcrResultReceivesIdentityBoundExplicitRejection()
    {
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        var target = new TargetInstanceId(Guid.NewGuid());
        var region = new RegionId(Guid.NewGuid());
        var token = new OcrExecutionToken(
            new SourceGenerationToken(
                session.RuntimeEpoch,
                target,
                CaptureAreaKey.UserRegion(region),
                new TextTrackId(Guid.NewGuid()),
                1,
                1),
            Guid.NewGuid(),
            1,
            1);

        RuntimeOcrResultAcknowledgement acknowledgement =
            await session.SubmitOcrResultAsync(
                new OcrResultSnapshot(token, [], "test-model", "1", true, null),
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

        Assert.False(acknowledgement.Accepted);
        Assert.Equal(RuntimeOcrResultApplyStatus.TargetMissing, acknowledgement.Status);
        Assert.Equal("ocr.result.targetMissing", acknowledgement.ErrorCode);
    }

    [Fact]
    public async Task CapturesPrimaryMonitorAndRemovesTheRuntimeSubscription()
    {
        nint monitor = MonitorFromPoint(default, 1U);
        Assert.NotEqual(nint.Zero, monitor);
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        var targetId = new CaptureTargetId(Guid.NewGuid());
        var instanceId = new TargetInstanceId(Guid.NewGuid());

        RuntimeCaptureTargetAcknowledgement added = await session.ApplyCaptureTargetAsync(
            new RuntimeCaptureTargetCommand(
                RuntimeCaptureTargetOperation.Upsert,
                1,
                targetId,
                instanceId,
                RuntimeCaptureTargetKind.Monitor,
                unchecked((ulong)monitor.ToInt64()),
                null),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        if (!added.Accepted && added.ErrorCode == "capture.serviceUnavailable")
            Assert.Skip(
                $"Windows Graphics Capture service is unavailable; HRESULT=0x{added.NativeErrorCode.GetValueOrDefault():X8}.");
        Assert.True(added.Accepted,
            $"{added.ErrorCode}; HRESULT=0x{added.NativeErrorCode.GetValueOrDefault():X8}");
        Assert.Contains(added.State, new[]
        {
            TargetLifecycleState.Running,
            TargetLifecycleState.RunningWithCaptureBorder,
        });

        RuntimeCaptureTargetAcknowledgement removed = await session.ApplyCaptureTargetAsync(
            new RuntimeCaptureTargetCommand(
                RuntimeCaptureTargetOperation.Remove,
                2,
                targetId,
                instanceId,
                RuntimeCaptureTargetKind.Monitor,
                0,
                null),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(removed.Accepted, removed.ErrorCode);
        Assert.Equal(TargetLifecycleState.Closed, removed.State);
    }

    [Fact]
    public async Task MonitorAdmissionPublishesMeasuredDxgiGpuBudget()
    {
        nint monitor = MonitorFromPoint(default, 1U);
        if (monitor == nint.Zero) Assert.Skip("No monitor is available for DXGI budget measurement.");
        await using RuntimeEngineHostSession session =
            await RuntimeEngineHostLauncher.LaunchAsync(
                FindEngineHostExecutable(),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<RuntimeEngineEvent> events =
            session.ReadEventsAsync(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        RuntimeBudgetSnapshot initial = await ReadBudgetAsync(
            events, TestContext.Current.CancellationToken);
        var targetId = new CaptureTargetId(Guid.NewGuid());
        var instanceId = new TargetInstanceId(Guid.NewGuid());

        RuntimeCaptureTargetAcknowledgement admission = await session.ApplyCaptureTargetAsync(
            new RuntimeCaptureTargetCommand(
                RuntimeCaptureTargetOperation.Upsert,
                1,
                targetId,
                instanceId,
                RuntimeCaptureTargetKind.Monitor,
                unchecked((ulong)monitor.ToInt64()),
                null),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        RuntimeBudgetSnapshot measured = await ReadBudgetAsync(
            events, TestContext.Current.CancellationToken);

        Assert.True(measured.SnapshotRevision > initial.SnapshotRevision);
        RuntimeBudgetPool gpu = Assert.Single(measured.Pools,
            pool => pool.Name.StartsWith("engine.gpu.", StringComparison.Ordinal) &&
                pool.Name.EndsWith(".local.bytes", StringComparison.Ordinal));
        Assert.Equal(RuntimeBudgetUnit.Bytes, gpu.Unit);
        Assert.True(gpu.Limit > 0);
        Assert.InRange(gpu.Committed, 0, gpu.Limit);
        if (admission.Accepted)
        {
            RuntimeCaptureTargetAcknowledgement removed = await session.ApplyCaptureTargetAsync(
                new RuntimeCaptureTargetCommand(
                    RuntimeCaptureTargetOperation.Remove,
                    2,
                    targetId,
                    instanceId,
                    RuntimeCaptureTargetKind.Monitor,
                    0,
                    null),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.True(removed.Accepted, removed.ErrorCode);
        }
    }

    [Fact]
    public async Task UnsupportedPostHandshakeMessageTerminatesWithProtocolFailure()
    {
        RuntimeEngineHostSession session = await RuntimeEngineHostLauncher.LaunchAsync(
            FindEngineHostExecutable(),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        using Process process = Process.GetProcessById(session.ProcessId);
        using var unsupported = new RuntimeFrame(
            new RuntimeEnvelopeHeader(
                RuntimeProtocol.CurrentVersion,
                RuntimeMessageKind.TargetSnapshot,
                Guid.NewGuid(),
                session.RuntimeEpoch,
                0,
                DateTimeOffset.UtcNow.AddSeconds(2)),
            []);

        await RuntimeFrameCodec.WriteAsync(
            session.Connection.Stream,
            unsupported,
            TestContext.Current.CancellationToken);

        Assert.True(process.WaitForExit(5_000));
        Assert.Equal(68, session.ExitCode);
        await session.DisposeAsync();
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
                "artifacts", "cmake", "windows-x64",
                "src", "InfiniTranseon.EngineHost", configuration,
                "InfiniTranseon.EngineHost.exe"),
            Path.Combine(
                directory.FullName,
                "artifacts", "cmake", $"ninja-{configuration.ToLowerInvariant()}",
                "src", "InfiniTranseon.EngineHost", "InfiniTranseon.EngineHost.exe"),
        ];
        string? path = candidates.FirstOrDefault(File.Exists);
        Assert.True(path is not null,
            $"Build EngineHost before this test: {string.Join(" or ", candidates)}");
        return path!;
    }

    private static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using System.Diagnostics.Process process =
                    System.Diagnostics.Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<RuntimeBudgetSnapshot> ReadBudgetAsync(
        IAsyncEnumerator<RuntimeEngineEvent> events,
        CancellationToken cancellationToken)
    {
        while (await events.MoveNextAsync().AsTask().WaitAsync(
                   TimeSpan.FromSeconds(5), cancellationToken))
        {
            using RuntimeEngineEvent runtimeEvent = events.Current;
            if (runtimeEvent.MessageKind == RuntimeMessageKind.RuntimeBudgetSnapshot)
                return RuntimeBudgetSnapshotPayloadCodec.Decode(runtimeEvent.Payload.Span);
        }
        throw new EndOfStreamException("EngineHost ended before publishing a budget snapshot.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);
}
