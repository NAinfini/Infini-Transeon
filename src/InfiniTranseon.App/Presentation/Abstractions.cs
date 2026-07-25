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

public enum AppPerformancePreset
{
    Eco,
    Balanced,
    Performance,
}

public sealed record ApplicationSettings(
    UiThemePreference Theme,
    bool StrictOffline,
    HistoryRetention HistoryRetention,
    string UiLanguage,
    IReadOnlyList<AppHotkeyBinding>? Hotkeys = null,
    AppPerformancePreset PerformancePreset = AppPerformancePreset.Balanced,
    bool ReducedMotion = false,
    IReadOnlyDictionary<string, string>? ProviderEndpoints = null,
    bool CloseToTray = true,
    bool CloseToTrayConfirmed = false,
    IReadOnlyList<Guid>? PinnedProfileIds = null)
{
    public IReadOnlyList<AppHotkeyBinding> EffectiveHotkeys =>
        Hotkeys ?? HotkeyDefaults.Create();

    public IReadOnlyDictionary<string, string> EffectiveProviderEndpoints =>
        ProviderEndpoints ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<Guid> EffectivePinnedProfileIds => PinnedProfileIds ?? [];
}

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

    Task ExportAsync(
        Guid profileId,
        Stream destination,
        CancellationToken cancellationToken = default);

    Task<Guid> ImportAsync(Stream source, CancellationToken cancellationToken = default);
}

public interface IWorkbenchService
{
    Task<WorkbenchProfileDraft?> LoadAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    Task<ProfileRuntimeApplyResult> SaveAndApplyAsync(
        WorkbenchProfileDraft profile,
        CancellationToken cancellationToken = default);
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

    // Raised whenever the running capture-target set or its user-visible control state changes.
    event EventHandler? TargetsChanged;

    IReadOnlyList<RunningTarget> GetRunningTargets();

    Task StartAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    Task RequestManualOcrAsync(CancellationToken cancellationToken = default);

    Task<ProfileRuntimeApplyResult> ApplyProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ProfileRuntimeApplyResult.SavedOnly);

    Task<RuntimeThumbnail?> RequestThumbnailAsync(
        Guid targetId,
        int maximumLongEdge,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<RuntimeThumbnail?>(null);
}

public enum ProfileRuntimeApplyResult
{
    SavedOnly,
    HotApplied,
    Restarted,
}

public interface IHistoryService
{
    void SelectProfile(Guid? profileId);

    Task<IReadOnlyList<HistoryEvent>> GetEventsAsync(CancellationToken cancellationToken = default);

    Task SaveCorrectionAsync(
        HistoryEvent historyEvent,
        string correctedText,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticsService
{
    Task<IReadOnlyList<DiagnosticEvent>> GetEventsAsync(CancellationToken cancellationToken = default);
}

// The glossary is stored inside the active profile document; edits persist through the profile
// repository. AddOrUpdate keys on SourceTerm; replacingSourceTerm names the entry being renamed.
public interface IGlossaryService
{
    void SelectProfile(Guid profileId);

    Task<GlossarySnapshot> GetEntriesAsync(CancellationToken cancellationToken = default);

    Task AddOrUpdateAsync(
        GlossaryEntry entry,
        string? replacingSourceTerm,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string sourceTerm, CancellationToken cancellationToken = default);

    Task ImportAsync(
        IReadOnlyList<GlossaryEntry> entries,
        CancellationToken cancellationToken = default);

    Task SaveStylePromptVersionAsync(
        string name,
        string template,
        CancellationToken cancellationToken = default);

    Task ActivateStylePromptVersionAsync(
        int version,
        CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderRow>> GetProvidersAsync(CancellationToken cancellationToken = default);

    Task<ProviderRow> ImportRestAdapterAsync(
        Stream source,
        CancellationToken cancellationToken = default);

    Task RemoveCustomProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default);
}

// Update metadata and progress only. Release manifests and downloaded binaries are verified by the
// Core update service before this presentation seam reports an installer as ready.
public interface IAppUpdateService
{
    AppUpdateSnapshot Snapshot { get; }

    event EventHandler? Changed;

    Task CheckAsync(
        bool explicitUserAction,
        bool mainUiVisible,
        CancellationToken cancellationToken = default);

    Task DownloadInstallerAsync(
        bool userApproved,
        CancellationToken cancellationToken = default);
}

// References and presence only; the secret value never crosses into the presentation layer. Set/Clear
// write straight through to the OS credential store. HasSecret returns presence without exposing value.
public interface ISecretReferenceService
{
    Task<IReadOnlyList<SecretReference>> GetReferencesAsync(CancellationToken cancellationToken = default);

    Task<bool> HasSecretAsync(string providerId, CancellationToken cancellationToken = default);

    Task SetSecretAsync(string providerId, string secret, CancellationToken cancellationToken = default);

    Task SetSecretAsync(
        string providerId,
        string credentialReference,
        string secret,
        CancellationToken cancellationToken = default);

    Task ClearSecretAsync(string providerId, CancellationToken cancellationToken = default);

    Task ClearSecretAsync(
        string providerId,
        string credentialReference,
        CancellationToken cancellationToken = default);
}
