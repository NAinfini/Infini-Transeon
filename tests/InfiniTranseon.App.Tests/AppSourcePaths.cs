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
}
