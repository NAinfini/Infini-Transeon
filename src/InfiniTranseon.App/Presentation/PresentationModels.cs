using InfiniTranseon.App.Controls;

namespace InfiniTranseon.App.Presentation;

// Presentation contracts for the control UI. They carry IDs, metadata, and display
// strings only — never full capture frames or secret values (see frontend plan Task 1).

// Properties are declared settable in the body (rather than left as positional init-only) because the
// WinUI x:Bind XAML compiler emits get+set accessors in XamlTypeInfo for every bound member of an
// x:DataType; init-only setters fail CS8852 there. Positional construction, `with`, and record value
// equality are all preserved by initializing each settable property from its positional parameter.
public sealed record ProfileCard(
    Guid ProfileId,
    string Name,
    string TargetDescription,
    string Resolution,
    string Languages,
    int RegionCount,
    int ChannelCount,
    string MatchStateText,
    StatusSeverity MatchSeverity,
    string PrimaryAction)
{
    public Guid ProfileId { get; set; } = ProfileId;
    public string Name { get; set; } = Name;
    public string TargetDescription { get; set; } = TargetDescription;
    public string Resolution { get; set; } = Resolution;
    public string Languages { get; set; } = Languages;
    public int RegionCount { get; set; } = RegionCount;
    public int ChannelCount { get; set; } = ChannelCount;
    public string MatchStateText { get; set; } = MatchStateText;
    public StatusSeverity MatchSeverity { get; set; } = MatchSeverity;
    public string PrimaryAction { get; set; } = PrimaryAction;
}

public enum RegionPriorityLevel
{
    P0,
    P1,
    P2,
    P3,
}

// A single OCR region as entered in the setup wizard (name + priority). Bounds default to the whole
// target and are refined later in the workbench; the wizard only needs identity and priority.
public sealed record ProfileRegionDraft(string Name, RegionPriorityLevel Priority);

// Presentation-level profile draft. The wizard builds this from purely presentation/contract types;
// the real profile service maps it to a Core ProfileDocument so no view model touches Core types.
public sealed record ProfileEditModel(
    Guid ProfileId,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    Guid TargetId,
    string TargetName,
    string TargetKind,
    string Resolution,
    string TranslationProviderId,
    IReadOnlyList<ProfileRegionDraft> Regions);

// Glossary snapshot for the active profile. Carries the entries plus enough context for the page to
// render an intentional empty state when no profile exists yet.
public sealed record GlossarySnapshot(
    Guid ActiveProfileId,
    string ActiveProfileName,
    IReadOnlyList<GlossaryEntry> Entries)
{
    public bool HasActiveProfile => ActiveProfileId != Guid.Empty;

    public static GlossarySnapshot Empty { get; } = new(Guid.Empty, string.Empty, []);
}

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
    IReadOnlyList<ChannelResult> Channels)
{
    public string Timestamp { get; set; } = Timestamp;
    public string SourceText { get; set; } = SourceText;
    public string Region { get; set; } = Region;
    public IReadOnlyList<ChannelResult> Channels { get; set; } = Channels;
}

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
    string Detail)
{
    public string Name { get; set; } = Name;
    public string Kind { get; set; } = Kind;
    public string StateText { get; set; } = StateText;
    public StatusSeverity StateSeverity { get; set; } = StateSeverity;
    public string Detail { get; set; } = Detail;
}

public sealed record DiagnosticEvent(
    string Timestamp,
    string Scope,
    string Title,
    string CurrentBehavior,
    string RecoveryAction,
    StatusSeverity Severity)
{
    public string Timestamp { get; set; } = Timestamp;
    public string Scope { get; set; } = Scope;
    public string Title { get; set; } = Title;
    public string CurrentBehavior { get; set; } = CurrentBehavior;
    public string RecoveryAction { get; set; } = RecoveryAction;
    public StatusSeverity Severity { get; set; } = Severity;
}

public sealed record GlossaryEntry(
    string SourceTerm,
    string TargetTerm,
    string Scope,
    bool CaseSensitive,
    bool Protected,
    string Notes)
{
    public string SourceTerm { get; set; } = SourceTerm;
    public string TargetTerm { get; set; } = TargetTerm;
    public string Scope { get; set; } = Scope;
    public bool CaseSensitive { get; set; } = CaseSensitive;
    public bool Protected { get; set; } = Protected;
    public string Notes { get; set; } = Notes;
}

// Handle to a secret held by the OS credential store. The UI never receives the value.
public sealed record SecretReference(
    string ReferenceId,
    string ProviderId,
    string StorageLocation,
    bool IsPresent);
