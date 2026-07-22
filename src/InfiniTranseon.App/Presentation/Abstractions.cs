using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Presentation;

public enum UiThemePreference
{
    System,
    Light,
    Dark,
}

public enum HistoryRetention
{
    Off,
    Days30,
    Days90,
}

public sealed record ApplicationSettings(
    UiThemePreference Theme,
    bool StrictOffline,
    HistoryRetention HistoryRetention,
    string UiLanguage);

// Backing repositories are asynchronous SQLite/credential stores, so every read and mutation is
// async. View models call these from an explicit InitializeAsync/load pattern and surface failures
// as inline errors; no member blocks or swallows exceptions.
public interface IProfileService
{
    Task<IReadOnlyList<ProfileCard>> GetProfilesAsync(CancellationToken cancellationToken = default);

    // Loads an existing profile as an editable draft, or null when the id is unknown.
    Task<ProfileEditModel?> LoadForEditAsync(Guid profileId, CancellationToken cancellationToken = default);

    // Persists a new (ProfileId == Guid.Empty) or existing profile and returns its id.
    Task<Guid> SaveAsync(ProfileEditModel profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}

// Facade over the EngineHost runtime lifecycle for the UI. Status mirrors the Contracts
// EngineRuntimeStatus state machine verbatim (including ExecutableNotFound carrying the searched
// paths in LastChange). Control calls propagate real failures — notably
// EngineRuntimeUnsupportedOperationException for operations protocol v1 does not carry — so view
// models surface them instead of pretending success.
public interface IRuntimeControlService
{
    EngineRuntimeStatus Status { get; }

    // The most recent status transition (error code, searched paths); null before the first start.
    EngineRuntimeStatusChange? LastChange { get; }

    bool IsPaused { get; }

    bool IsOverlayVisible { get; }

    event EventHandler<EngineRuntimeStatusChange>? StatusChanged;

    // Raised whenever the running capture-target set changes.
    event EventHandler? TargetsChanged;

    IReadOnlyList<RunningTarget> GetRunningTargets();

    Task StartAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    Task RequestManualOcrAsync(CancellationToken cancellationToken = default);
}

public interface IHistoryService
{
    Task<IReadOnlyList<HistoryEvent>> GetEventsAsync(CancellationToken cancellationToken = default);
}

public interface IDiagnosticsService
{
    Task<IReadOnlyList<DiagnosticEvent>> GetEventsAsync(CancellationToken cancellationToken = default);
}

// The glossary is stored inside the active profile document; edits persist through the profile
// repository. AddOrUpdate keys on SourceTerm; replacingSourceTerm names the entry being renamed.
public interface IGlossaryService
{
    Task<GlossarySnapshot> GetEntriesAsync(CancellationToken cancellationToken = default);

    Task AddOrUpdateAsync(
        GlossaryEntry entry,
        string? replacingSourceTerm,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string sourceTerm, CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderRow>> GetProvidersAsync(CancellationToken cancellationToken = default);
}

// References and presence only; the secret value never crosses into the presentation layer. Set/Clear
// write straight through to the OS credential store. HasSecret returns presence without exposing value.
public interface ISecretReferenceService
{
    Task<IReadOnlyList<SecretReference>> GetReferencesAsync(CancellationToken cancellationToken = default);

    Task<bool> HasSecretAsync(string providerId, CancellationToken cancellationToken = default);

    Task SetSecretAsync(string providerId, string secret, CancellationToken cancellationToken = default);

    Task ClearSecretAsync(string providerId, CancellationToken cancellationToken = default);
}

// Read-only surface over protocol safety ceilings plus the latest dynamic budget snapshot
// and reconnect revision. View models display localized disabled reasons from these values
// and never derive their own limits.
public interface IRuntimeCapabilitiesService
{
    RuntimeCapabilities Capabilities { get; }

    RuntimeBudgetSnapshot? LatestBudget { get; }

    long ReconnectRevision { get; }

    event EventHandler? Changed;

    void UpdateBudget(RuntimeBudgetSnapshot budget);

    void ApplyReconnect(RuntimeReconnectSnapshot snapshot);
}
