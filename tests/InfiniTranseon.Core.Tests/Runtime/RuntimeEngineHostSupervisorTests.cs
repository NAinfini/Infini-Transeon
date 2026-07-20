using System.Diagnostics;
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
}
