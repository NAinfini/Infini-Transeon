using System.Xml.Linq;

namespace InfiniTranseon.Core.Tests.Architecture;

public sealed class DependencyTests
{
    [Fact]
    public void CoreDependsOnlyOnContracts()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(root, "src", "InfiniTranseon.Core", "InfiniTranseon.Core.csproj");
        XDocument project = XDocument.Load(projectPath);

        string[] references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
            .OfType<string>()
            .ToArray();

        Assert.Equal(["../InfiniTranseon.Contracts/InfiniTranseon.Contracts.csproj"], references);
        Assert.DoesNotContain(
            project.Descendants("PackageReference"),
            element => element.Attribute("Include")?.Value.Contains("WinUI", StringComparison.OrdinalIgnoreCase) is true);
    }

    [Fact]
    public void ProductionProjectsDoNotReferenceSharedTestSupport()
    {
        string root = FindRepositoryRoot();
        string[] productionProjects = Directory.GetFiles(
            Path.Combine(root, "src"),
            "*.csproj",
            SearchOption.AllDirectories);

        foreach (string projectPath in productionProjects)
        {
            string contents = File.ReadAllText(projectPath);
            Assert.DoesNotContain("InfiniTranseon.Testing", contents, StringComparison.Ordinal);
        }
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
