using System;
using System.IO;

namespace InfiniTranseon.Bench.Tests;

/// <summary>
/// Unique scratch directory for a single test. Deletion is best-effort so a locked
/// leftover PNG never masks the assertion the test actually cares about; it mirrors
/// the generator's own <c>TryDeleteDirectory</c> cleanup contract.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BenchTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only; a leftover temp file must not fail the run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
