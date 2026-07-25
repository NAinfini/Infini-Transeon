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

    // User-authored declarative REST adapters. Definitions contain templates and credential
    // references only; secret values remain in Windows Credential Manager.
    public string CustomRestAdaptersPath => Path.Combine(RootDirectory, "rest-adapters.json");

    // Where the runtime status reporter writes JSONL status events that the diagnostics page reads.
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    // Metadata-only local crash reports. No dump or upload endpoint is created by default.
    public string CrashReportDirectory => Path.Combine(RootDirectory, "crash-reports");

    // Verified update installers and the monotonic signed release sequence state.
    public string UpdateDownloadDirectory => Path.Combine(RootDirectory, "updates");

    public string UpdateSequencePath => Path.Combine(RootDirectory, "update-sequence.json");

    // Optional local model packages live outside the application install and are populated only
    // after an explicit user-approved download from a separately signed model catalog.
    public string ModelDirectory => Path.Combine(RootDirectory, "models");

    public string ModelCatalogSequencePath => Path.Combine(
        ModelDirectory,
        "catalog-sequence.json");

    public static AppDataOptions Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InfiniTranseon"));

    public void EnsureRootExists() => Directory.CreateDirectory(RootDirectory);
}
