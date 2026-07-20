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
