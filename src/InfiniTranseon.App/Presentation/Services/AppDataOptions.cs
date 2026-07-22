namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Filesystem locations for the control app's real data stores, rooted under a single directory
/// (%LOCALAPPDATA%\InfiniTranseon by default). The directory is created explicitly at composition;
/// any failure to create it or open the database propagates so startup opens the recovery window
/// instead of silently falling back to in-memory data.
/// </summary>
public sealed record AppDataOptions(string RootDirectory)
{
    public string DatabasePath => Path.Combine(RootDirectory, "infini-transeon.db");

    // App-only presentation preferences (theme, strict-offline default, history retention) that have
    // no home in the Core ApplicationSettings schema. UiLanguage lives in the Core settings store.
    public string UiPreferencesPath => Path.Combine(RootDirectory, "ui-preferences.json");

    // Where the runtime status reporter writes JSONL status events that the diagnostics page reads.
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static AppDataOptions Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InfiniTranseon"));

    public void EnsureRootExists() => Directory.CreateDirectory(RootDirectory);
}
