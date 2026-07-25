using System.Xml.Linq;
using System.Text.Json;

namespace InfiniTranseon.Core.Tests.Packaging;

public sealed class PackageManifestTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Uap11 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/11";
    private static readonly XNamespace Rescap =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

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

        Assert.Equal("neutral", identity.Attribute("ProcessorArchitecture")?.Value);
        Assert.Equal("Windows.Desktop", family.Attribute("Name")?.Value);
        Assert.Equal("10.0.22621.0", family.Attribute("MinVersion")?.Value);
    }

    [Fact]
    public void IdentityTemplateUsesAValidCertificateSubjectPublisher()
    {
        XDocument manifest = LoadManifest();
        XElement identity = Assert.Single(manifest.Descendants(Foundation + "Identity"));
        string publisher = identity.Attribute("Publisher")?.Value ?? string.Empty;

        Assert.Equal("CN=InfiniTranseon", publisher);
    }

    [Fact]
    public void ManifestIsAnExternalLocationIdentityPackageForAWin32Application()
    {
        XDocument manifest = LoadManifest();
        XElement properties = Assert.Single(manifest.Descendants(Foundation + "Properties"));
        XElement allowExternal = Assert.Single(properties.Elements(Uap10 + "AllowExternalContent"));
        XElement application = Assert.Single(manifest.Descendants(Foundation + "Application"));
        XElement visualElements = Assert.Single(application.Elements(Uap + "VisualElements"));
        string[] restrictedCapabilities = manifest.Descendants(Rescap + "Capability")
            .Select(element => element.Attribute("Name")?.Value ?? string.Empty)
            .ToArray();

        Assert.Equal("true", allowExternal.Value);
        Assert.Equal("win32App", application.Attribute(Uap10 + "RuntimeBehavior")?.Value);
        Assert.Equal("mediumIL", application.Attribute(Uap10 + "TrustLevel")?.Value);
        Assert.Equal("none", visualElements.Attribute("AppListEntry")?.Value);
        Assert.Contains("runFullTrust", restrictedCapabilities);
        Assert.Contains("unvirtualizedResources", restrictedCapabilities);
    }

    [Fact]
    public void ManifestRegistersTheDeepLinkProtocolExactlyOnce()
    {
        XDocument manifest = LoadManifest();
        XElement application = Assert.Single(manifest.Descendants(Foundation + "Application"));
        XElement extensions = Assert.Single(application.Elements(Foundation + "Extensions"));
        XElement extension = Assert.Single(
            extensions.Elements(Uap + "Extension"),
            element => element.Attribute("Category")?.Value == "windows.protocol");
        XElement protocol = Assert.Single(extension.Elements(Uap + "Protocol"));

        Assert.Equal("infinitranseon", protocol.Attribute("Name")?.Value);
    }

    [Fact]
    public void UnsignedExecutableManifestDoesNotClaimPackageIdentity()
    {
        string root = FindRepositoryRoot();
        XDocument executableManifest = XDocument.Load(
            Path.Combine(root, "src", "InfiniTranseon.App", "app.manifest"));

        Assert.DoesNotContain(
            executableManifest.Descendants(),
            element => element.Name.LocalName == "msix");
    }

    [Fact]
    public void UnpackagedAppGeneratesAndPublishesItsResourceIndex()
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(
            root,
            "src",
            "InfiniTranseon.App",
            "InfiniTranseon.App.csproj"));
        XElement generate = Assert.Single(
            project.Descendants("Target"),
            target => target.Attribute("Name")?.Value == "GenerateUnpackagedPri");
        XElement publish = Assert.Single(
            project.Descendants("Target"),
            target => target.Attribute("Name")?.Value == "PublishUnpackagedPri");
        XElement command = Assert.Single(generate.Elements("Exec"));
        XElement copy = Assert.Single(publish.Elements("Copy"));

        string commandText = command.Attribute("Command")?.Value ?? string.Empty;
        Assert.Contains("$(MakePriExeFullPath)", commandText);
        Assert.Contains("$(TargetDir)resources.pri", commandText);
        Assert.Equal(
            "$(PublishDir)resources.pri",
            copy.Attribute("DestinationFiles")?.Value);
    }

    [Fact]
    public void IdentityManifestAssetsExistAndAreStagedByTheReleaseScript()
    {
        string root = FindRepositoryRoot();
        XDocument manifest = LoadManifest();
        string[] assetPaths =
        [
            Assert.Single(manifest.Descendants(Foundation + "Logo")).Value,
            Assert.Single(manifest.Descendants(Uap + "VisualElements"))
                .Attribute("Square150x150Logo")!.Value,
            Assert.Single(manifest.Descendants(Uap + "VisualElements"))
                .Attribute("Square44x44Logo")!.Value,
        ];
        foreach (string relativePath in assetPaths)
        {
            Assert.True(
                File.Exists(Path.Combine(
                    root,
                    "packaging",
                    "identity",
                    relativePath.Replace('\\', Path.DirectorySeparatorChar))),
                $"Identity asset '{relativePath}' must exist.");
        }

        string script = File.ReadAllText(
            Path.Combine(root, "scripts", "prepare-release-identity.ps1"));
        Assert.Contains("Copy-Item -LiteralPath $source", script, StringComparison.Ordinal);
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
        Assert.DoesNotContain("InfiniTranseon.Identity.msix", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("prepare-release-identity.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "verify-portable-layout.ps1 -PublishDirectory artifacts/release/publish",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-ChildItem -LiteralPath artifacts/release/publish -Recurse -Filter *.pdb",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("Unsigned Windows build", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTHENTICODE_CERTIFICATE", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-release-binaries.ps1", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsignedPortableManifestDoesNotRequireAnUnregistrableIdentityPackage()
    {
        string root = FindRepositoryRoot();
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "packaging", "portable-manifest.json")));
        string[] required = manifest.RootElement.GetProperty("requiredFiles")
            .EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .ToArray();

        Assert.DoesNotContain("InfiniTranseon.Identity.msix", required);
        Assert.Contains("resources.pri", required);
        Assert.Contains("InfiniTranseon.ModelRuntime.Native.dll", required);
        Assert.Contains("model-catalog.json", required);
        Assert.Contains("sbom.json", required);
        Assert.Contains("THIRD-PARTY-NOTICES.json", required);
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
