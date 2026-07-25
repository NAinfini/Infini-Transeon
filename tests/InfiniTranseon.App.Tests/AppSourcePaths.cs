namespace InfiniTranseon.App.Tests;

// Resolves paths into the App project from the test output directory by walking up to the repo
// layout, so tests read the single source of truth (the App's own resources and shell XAML)
// regardless of the bin/obj folder depth.
internal static class AppSourcePaths
{
    private static readonly Lazy<string> ProjectDirectory = new(() =>
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "InfiniTranseon.App");
            if (Directory.Exists(Path.Combine(candidate, "Resources")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate src/InfiniTranseon.App from '{AppContext.BaseDirectory}'.");
    });

    public static string ResourcesFile(string culture) =>
        Path.Combine(ProjectDirectory.Value, "Resources", culture, "Resources.resw");

    public static string AppShellXaml =>
        Path.Combine(ProjectDirectory.Value, "Shell", "AppShell.xaml");

    public static string DesignTokensXaml =>
        Path.Combine(ProjectDirectory.Value, "Theme", "DesignTokens.xaml");

    public static string ControlStylesXaml =>
        Path.Combine(ProjectDirectory.Value, "Theme", "ControlStyles.xaml");

    public static string PageShellXaml =>
        Path.Combine(ProjectDirectory.Value, "Controls", "PageShell.xaml");

    public static string AppShellCode =>
        Path.Combine(ProjectDirectory.Value, "Shell", "AppShell.xaml.cs");

    /// <summary>Every C# file in the App project, excluding generated output.</summary>
    public static IReadOnlyList<string> AllCSharpFiles() =>
        Directory.GetFiles(ProjectDirectory.Value, "*.cs", SearchOption.AllDirectories)
            .Where(IsAuthoredSource)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every XAML file shipped by the App project, for repository-wide XAML invariants.</summary>
    public static IReadOnlyList<string> AllXamlFiles() =>
        Directory.GetFiles(ProjectDirectory.Value, "*.xaml", SearchOption.AllDirectories)
            .Where(IsAuthoredSource)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool IsAuthoredSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
