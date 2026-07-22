using System.Xml.Linq;

namespace InfiniTranseon.Core.Tests.Packaging;

public sealed class PackageManifestTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap11 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/11";

    [Fact]
    public void ManifestDeclaresBorderlessCaptureExactlyOnce()
    {
        XDocument manifest = LoadManifest();
        XElement root = Assert.IsType<XElement>(manifest.Root);
        string[] ignorableNamespaces = (root.Attribute("IgnorableNamespaces")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        XElement[] declarations = manifest.Descendants(Uap11 + "Capability")
            .Where(element => element.Attribute("Name")?.Value == "graphicsCaptureWithoutBorder")
            .ToArray();

        Assert.Contains("uap11", ignorableNamespaces);
        Assert.Single(declarations);
    }

    [Fact]
    public void ManifestTargetsWindowsElevenX64Baseline()
    {
        XDocument manifest = LoadManifest();
        XElement identity = Assert.Single(manifest.Descendants(Foundation + "Identity"));
        XElement family = Assert.Single(manifest.Descendants(Foundation + "TargetDeviceFamily"));

        Assert.Equal("x64", identity.Attribute("ProcessorArchitecture")?.Value);
        Assert.Equal("Windows.Desktop", family.Attribute("Name")?.Value);
        Assert.Equal("10.0.22621.0", family.Attribute("MinVersion")?.Value);
    }

    [Fact]
    public void ReleaseWorkflowStagesPortableManifestBeforeCreatingTheArchive()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "build-release.yml"));
        int stageManifest = workflow.IndexOf(
            "Copy-Item packaging/portable-manifest.json artifacts/release/publish/",
            StringComparison.Ordinal);
        int createArchive = workflow.IndexOf(
            "Compress-Archive -Path artifacts/release/publish/*",
            StringComparison.Ordinal);

        Assert.True(stageManifest >= 0, "The portable manifest must be staged into the archive root.");
        Assert.True(createArchive > stageManifest, "The portable archive must be created after its manifest is staged.");
    }

    private static XDocument LoadManifest()
    {
        string root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine(root, "packaging", "identity", "Package.appxmanifest"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InfiniTranseon.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Infini-Transeon repository root.");
    }
}
