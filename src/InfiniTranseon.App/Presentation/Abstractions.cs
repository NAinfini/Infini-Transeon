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

public interface IProfileService
{
    IReadOnlyList<ProfileCard> GetProfiles();
}

public interface IRuntimeControlService
{
    IReadOnlyList<RunningTarget> GetRunningTargets();

    void PauseAll();

    void ToggleOverlay();

    void RequestManualOcr();
}

public interface IHistoryService
{
    IReadOnlyList<HistoryEvent> GetEvents();
}

public interface IDiagnosticsService
{
    IReadOnlyList<DiagnosticEvent> GetEvents();
}

public interface IGlossaryService
{
    IReadOnlyList<GlossaryEntry> GetEntries();
}

public interface ISettingsService
{
    ApplicationSettings GetSettings();

    void Update(ApplicationSettings settings);

    IReadOnlyList<ProviderRow> GetProviders();
}

// References only; the value never crosses into the presentation layer.
public interface ISecretReferenceService
{
    IReadOnlyList<SecretReference> GetReferences();

    bool HasSecret(string providerId);
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
