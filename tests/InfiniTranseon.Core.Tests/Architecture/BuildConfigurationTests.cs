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
        string releaseWorkflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "build-release.yml"));
        using JsonDocument tools = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, ".config", "dotnet-tools.json")));

        Assert.Contains("dotnet tool run dotnet-CycloneDX", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet tool run wix build", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("dotnet tool run wix msi validate", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet tool install --global", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("verify-sbom-licenses.ps1", workflow, StringComparison.Ordinal);
        Assert.Equal("6.2.0", tools.RootElement.GetProperty("tools")
            .GetProperty("cyclonedx").GetProperty("version").GetString());
        Assert.Equal("5.0.2", tools.RootElement.GetProperty("tools")
            .GetProperty("wix").GetProperty("version").GetString());
        string msiVerifier = File.ReadAllText(
            Path.Combine(root, "scripts", "verify-msi-layout.ps1"));
        Assert.Contains("dotnet tool run wix msi decompile", msiVerifier, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseLicenseGatePinsTheReviewedOnnxRuntimeLicense()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "verify-sbom-licenses.ps1"));

        Assert.Contains(
            "'Microsoft.ML.OnnxRuntime@1.27.1' = 'MIT'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Microsoft.ML.OnnxRuntime.Managed@1.27.1' = 'MIT'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Versions are pinned so every upgrade forces a fresh review",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseSigningScriptsCanRunOutsideGitHubActions()
    {
        string root = FindRepositoryRoot();
        foreach (string fileName in new[]
                 {
                     "build-signed-model-catalog.ps1",
                     "build-signed-release.ps1",
                     "verify-release-signing-key.ps1",
                 })
        {
            string script = File.ReadAllText(Path.Combine(root, "scripts", fileName));
            Assert.Contains("[IO.Path]::GetTempPath()", script, StringComparison.Ordinal);
            Assert.Contains("$env:RUNNER_TEMP", script, StringComparison.Ordinal);
        }

        string githubSetup = File.ReadAllText(
            Path.Combine(root, "scripts", "configure-github-release.ps1"));
        Assert.Contains("[switch] $ValidateOnly", githubSetup, StringComparison.Ordinal);
        Assert.Contains("[switch] $UploadSecrets", githubSetup, StringComparison.Ordinal);
        Assert.Contains("GitHubSecretChanged = $false", githubSetup, StringComparison.Ordinal);
        Assert.Contains(
            "[InfiniTranseon.ReleaseCredentialReader]::Read",
            githubSetup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationNeverProvisionsOcrModelsWithoutAnExplicitUserAction()
    {
        string root = FindRepositoryRoot();
        string application = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InfiniTranseon.App",
            "App.xaml.cs"));
        string composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InfiniTranseon.App",
            "Composition",
            "PresentationComposition.cs"));

        Assert.DoesNotContain("MaintainOcrModelsAfterLaunchAsync", application, StringComparison.Ordinal);
        Assert.DoesNotContain("OcrModelProvisioningService", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionEditorUsesThemeAwareBrushesForHighContrast()
    {
        string root = FindRepositoryRoot();
        string canvas = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InfiniTranseon.App",
            "Controls",
            "RegionCanvas.xaml.cs"));
        string tokens = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InfiniTranseon.App",
            "Theme",
            "DesignTokens.xaml"));

        Assert.DoesNotContain("ColorHelper.FromArgb", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("Colors.White", canvas, StringComparison.Ordinal);
        Assert.Contains("RegionCanvasSelectedBorderBrush", canvas, StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"RegionCanvasSelectedBorderBrush\"",
            tokens,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"HighContrast\"",
            tokens,
            StringComparison.Ordinal);
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
