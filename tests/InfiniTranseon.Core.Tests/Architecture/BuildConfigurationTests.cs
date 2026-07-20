using System.Text.Json;

namespace InfiniTranseon.Core.Tests.Architecture;

public sealed class BuildConfigurationTests
{
    [Fact]
    public void NativeTestsExposeASeparateAddressSanitizerPreset()
    {
        using JsonDocument presets = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "CMakePresets.json")));
        JsonElement configure = Assert.Single(
            presets.RootElement.GetProperty("configurePresets").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "windows-x64-asan");
        JsonElement variables = configure.GetProperty("cacheVariables");

        Assert.Equal("ON", variables.GetProperty("INFINI_ENABLE_ADDRESS_SANITIZER").GetString());
        Assert.Contains(presets.RootElement.GetProperty("testPresets").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "windows-x64-asan");
    }

    [Fact]
    public void QualityWorkflowGeneratesSbomAndRejectsUnknownLicenses()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "quality.yml"));
        using JsonDocument tools = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, ".config", "dotnet-tools.json")));

        Assert.Contains("dotnet tool run dotnet-CycloneDX", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-sbom-licenses.ps1", workflow, StringComparison.Ordinal);
        Assert.Equal("6.2.0", tools.RootElement.GetProperty("tools")
            .GetProperty("cyclonedx").GetProperty("version").GetString());
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InfiniTranseon.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
