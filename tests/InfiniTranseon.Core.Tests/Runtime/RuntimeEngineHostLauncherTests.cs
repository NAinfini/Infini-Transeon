using System.Diagnostics;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeEngineHostLauncherTests
{
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
        string path = Path.Combine(
            directory.FullName,
            "artifacts",
            "cmake",
            "windows-x64",
            "src",
            "InfiniTranseon.EngineHost",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "InfiniTranseon.EngineHost.exe");
        Assert.True(File.Exists(path), $"Build EngineHost before this test: {path}");
        return path;
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
}
