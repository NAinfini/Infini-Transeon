using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class EngineHostLocatorTests
{
    private const string Base = @"C:\app";

    [Fact]
    public void PrefersExecutableAlongsideEntryAssembly()
    {
        string alongside = Path.Combine(Base, EngineHostLocator.ExecutableFileName);
        EngineHostLocateResult result = EngineHostLocator.Locate(
            Base, environmentOverride: @"C:\override", fileExists: path =>
                string.Equals(path, alongside, StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Found);
        Assert.Equal(alongside, result.ExecutablePath);
        Assert.Equal(alongside, Assert.Single(result.SearchedPaths));
    }

    [Fact]
    public void FallsBackToEnvironmentOverrideDirectory()
    {
        string overrideDirectory = @"C:\custom\engine";
        string overrideExecutable = Path.Combine(
            overrideDirectory, EngineHostLocator.ExecutableFileName);
        EngineHostLocateResult result = EngineHostLocator.Locate(
            Base, overrideDirectory, path =>
                string.Equals(path, overrideExecutable, StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Found);
        Assert.Equal(overrideExecutable, result.ExecutablePath);
    }

    [Fact]
    public void AcceptsEnvironmentOverridePointingAtExecutableFile()
    {
        string overrideExecutable = @"C:\custom\host.exe";
        EngineHostLocateResult result = EngineHostLocator.Locate(
            Base, overrideExecutable, path =>
                string.Equals(path, overrideExecutable, StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Found);
        Assert.Equal(overrideExecutable, result.ExecutablePath);
    }

    [Fact]
    public void ProbesRepositoryArtifactTreeForDeveloperBuilds()
    {
        string presets = Path.Combine(Base, "CMakePresets.json");
        string ninjaDebug = Path.Combine(
            Base, "artifacts", "cmake", "ninja-debug",
            "src", "InfiniTranseon.EngineHost", EngineHostLocator.ExecutableFileName);
        EngineHostLocateResult result = EngineHostLocator.Locate(
            Base, environmentOverride: null, fileExists: path =>
                string.Equals(path, presets, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, ninjaDebug, StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Found);
        Assert.Equal(ninjaDebug, result.ExecutablePath);
    }

    [Fact]
    public void ReportsEverySearchedPathWhenNothingIsFound()
    {
        EngineHostLocateResult result = EngineHostLocator.Locate(
            Base, @"C:\override", _ => false);

        Assert.False(result.Found);
        Assert.Null(result.ExecutablePath);
        Assert.Contains(
            Path.Combine(Base, EngineHostLocator.ExecutableFileName),
            result.SearchedPaths);
        Assert.Contains(
            Path.Combine(@"C:\override", EngineHostLocator.ExecutableFileName),
            result.SearchedPaths);
    }

    [Fact]
    public void NotFoundExceptionCarriesSearchedPaths()
    {
        string[] searched = [@"C:\a", @"C:\b"];
        var exception = new EngineHostNotFoundException(searched);

        Assert.Equal(searched, exception.SearchedPaths);
        Assert.Equal("engine.runtime.executableNotFound", exception.LocalizationKey);
    }
}
