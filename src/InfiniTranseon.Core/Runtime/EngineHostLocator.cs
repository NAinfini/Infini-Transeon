namespace InfiniTranseon.Core.Runtime;

/// <summary>Outcome of an <see cref="EngineHostLocator"/> probe.</summary>
public sealed record EngineHostLocateResult
{
    public EngineHostLocateResult(string? executablePath, IReadOnlyList<string> searchedPaths)
    {
        ArgumentNullException.ThrowIfNull(searchedPaths);
        ExecutablePath = executablePath;
        SearchedPaths = Array.AsReadOnly(searchedPaths.ToArray());
    }

    /// <summary>Full path to the located EngineHost executable, or null when none was found.</summary>
    public string? ExecutablePath { get; }

    /// <summary>Every candidate location probed, in order, for diagnostics.</summary>
    public IReadOnlyList<string> SearchedPaths { get; }

    public bool Found => ExecutablePath is not null;
}

/// <summary>
/// Resolves the native EngineHost executable using an explicit, diagnosable probe order:
/// <list type="number">
///   <item>alongside the entry assembly (the deployed layout);</item>
///   <item>the <c>INFINI_ENGINEHOST_PATH</c> environment variable (explicit override);</item>
///   <item>the repository <c>artifacts/cmake/**</c> tree (developer builds).</item>
/// </list>
/// When nothing is found the caller receives every probed path so the failure is explicit
/// rather than a silent fallback.
/// </summary>
public static class EngineHostLocator
{
    public const string ExecutableFileName = "InfiniTranseon.EngineHost.exe";
    public const string EnvironmentVariableName = "INFINI_ENGINEHOST_PATH";

    /// <summary>Probe every known location and report the first hit plus all searched paths.</summary>
    public static EngineHostLocateResult Locate() => Locate(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable(EnvironmentVariableName),
        File.Exists);

    /// <summary>Testable core: the probe order is deterministic given the inputs.</summary>
    public static EngineHostLocateResult Locate(
        string baseDirectory,
        string? environmentOverride,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        var searched = new List<string>();

        // 1. Alongside the entry assembly.
        string alongside = Path.GetFullPath(Path.Combine(baseDirectory, ExecutableFileName));
        searched.Add(alongside);
        if (fileExists(alongside))
        {
            return new EngineHostLocateResult(alongside, searched);
        }

        // 2. Explicit environment override. Accept either a full executable path or a
        // directory that contains it, so operators can point at either.
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            string overrideFull = Path.GetFullPath(environmentOverride);
            string overrideCandidate = HasExecutableExtension(overrideFull)
                ? overrideFull
                : Path.Combine(overrideFull, ExecutableFileName);
            searched.Add(overrideCandidate);
            if (fileExists(overrideCandidate))
            {
                return new EngineHostLocateResult(overrideCandidate, searched);
            }
        }

        // 3. Repository developer build tree: ascend to the repo root (the folder that
        // carries CMakePresets.json) then probe the well-known artifact layouts.
        foreach (string candidate in EnumerateRepositoryCandidates(baseDirectory, fileExists))
        {
            searched.Add(candidate);
            if (fileExists(candidate))
            {
                return new EngineHostLocateResult(candidate, searched);
            }
        }

        return new EngineHostLocateResult(null, searched);
    }

    private static bool HasExecutableExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateRepositoryCandidates(
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        DirectoryInfo? directory = new(baseDirectory);
        while (directory is not null &&
            !fileExists(Path.Combine(directory.FullName, "CMakePresets.json")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
        {
            yield break;
        }

        string cmakeRoot = Path.Combine(directory.FullName, "artifacts", "cmake");
        string engineHostSubPath = Path.Combine(
            "src", "InfiniTranseon.EngineHost", ExecutableFileName);

        // MSBuild-style multi-config layout: artifacts/cmake/<preset>/src/.../<Config>/exe.
        foreach (string preset in new[] { "windows-x64", "windows-x64-asan" })
        {
            foreach (string configuration in new[] { "Debug", "Release", "RelWithDebInfo" })
            {
                yield return Path.Combine(
                    cmakeRoot, preset, "src", "InfiniTranseon.EngineHost",
                    configuration, ExecutableFileName);
            }
        }

        // Ninja single-config layout: artifacts/cmake/ninja-<config>/src/.../exe.
        foreach (string preset in new[] { "ninja-debug", "ninja-release", "ninja-asan" })
        {
            yield return Path.Combine(cmakeRoot, preset, engineHostSubPath);
        }
    }
}

/// <summary>Thrown when a caller starts the runtime but the EngineHost executable is absent.</summary>
public sealed class EngineHostNotFoundException : Exception
{
    public EngineHostNotFoundException(IReadOnlyList<string> searchedPaths)
        : base("The EngineHost executable could not be located in any known location.")
    {
        ArgumentNullException.ThrowIfNull(searchedPaths);
        SearchedPaths = Array.AsReadOnly(searchedPaths.ToArray());
    }

    public IReadOnlyList<string> SearchedPaths { get; }

    public string LocalizationKey => "engine.runtime.executableNotFound";
}
