using InfiniTranseon.App.Controls;

namespace InfiniTranseon.App.Presentation;

// Presentation contracts for the control UI. They carry IDs, metadata, and display
// strings only — never full capture frames or secret values (see frontend plan Task 1).

public sealed record ProfileCard(
    string Name,
    string TargetDescription,
    string Resolution,
    string Languages,
    int RegionCount,
    int ChannelCount,
    string MatchStateText,
    StatusSeverity MatchSeverity,
    string PrimaryAction);

public sealed record RunningTarget(
    string ProfileName,
    string WindowTitle,
    string HealthText,
    StatusSeverity HealthSeverity,
    string LatencyP95,
    string ActiveRegions);

public sealed record HistoryEvent(
    string Timestamp,
    string SourceText,
    string Region,
    IReadOnlyList<ChannelResult> Channels);

public sealed record ChannelResult(
    string ChannelLabel,
    string Provider,
    string Text,
    string StateText,
    StatusSeverity StateSeverity,
    string Latency);

public sealed record ProviderRow(
    string Name,
    string Kind,
    string StateText,
    StatusSeverity StateSeverity,
    string Detail);

public sealed record DiagnosticEvent(
    string Timestamp,
    string Scope,
    string Title,
    string CurrentBehavior,
    string RecoveryAction,
    StatusSeverity Severity);

public sealed record GlossaryEntry(
    string SourceTerm,
    string TargetTerm,
    string Scope,
    bool CaseSensitive,
    bool Protected,
    string Notes);

// Handle to a secret held by the OS credential store. The UI never receives the value.
public sealed record SecretReference(
    string ReferenceId,
    string ProviderId,
    string StorageLocation,
    bool IsPresent);
