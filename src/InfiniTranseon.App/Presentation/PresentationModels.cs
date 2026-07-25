using InfiniTranseon.App.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniTranseon.Contracts.Runtime;

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
    string PrimaryAction,
    bool IsPinned = false)
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
    public bool IsPinned { get; set; } = IsPinned;
}

public sealed record HomeActivityItem(
    DateTimeOffset OccurredAtUtc,
    string Title,
    string Detail,
    StatusSeverity Severity)
{
    public DateTimeOffset OccurredAtUtc { get; set; } = OccurredAtUtc;
    public string Title { get; set; } = Title;
    public string Detail { get; set; } = Detail;
    public StatusSeverity Severity { get; set; } = Severity;
}

public enum RegionPriorityLevel
{
    P0,
    P1,
    P2,
    P3,
}

public enum RegionContextRole
{
    None,
    Speaker,
    Scene,
    Dialogue,
}

// A single OCR region as entered in the setup wizard or edited in the workbench. Identity and
// normalized bounds travel with the draft so saving metadata never resets a calibrated region.
public sealed record ProfileRegionDraft(
    string Name,
    RegionPriorityLevel Priority,
    Guid RegionId = default,
    double X = 0,
    double Y = 0,
    double Width = 1,
    double Height = 1,
    RegionContextRole ContextRole = RegionContextRole.None);

public sealed record ProfileCaptureTargetDraft(
    Guid TargetId,
    string Name,
    string Kind,
    string Resolution,
    OverlayPixelRect? DesktopRegion = null);

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
    IReadOnlyList<ProfileRegionDraft> Regions,
    OverlayPixelRect? DesktopRegion = null,
    IReadOnlyList<ProfileCaptureTargetDraft>? CaptureTargets = null)
{
    public IReadOnlyList<ProfileCaptureTargetDraft> EffectiveCaptureTargets =>
        CaptureTargets is { Count: > 0 }
            ? CaptureTargets
            :
            [
                new ProfileCaptureTargetDraft(
                    TargetId,
                    TargetName,
                    TargetKind,
                    Resolution,
                    DesktopRegion),
            ];
}

public sealed record WorkbenchChannelDraft(
    Guid ChannelId,
    string ProviderId,
    string Label,
    bool Enabled,
    int DisplayOrder,
    IReadOnlyList<string>? RefinementProviderIds = null)
{
    // XAML 类型信息生成器会为 init-only 属性发出赋值代码(CS8852),故按本文件既有模式
    // (HistoryEvent/ProviderRow)重声明为可写属性。
    public Guid ChannelId { get; set; } = ChannelId;

    public string ProviderId { get; set; } = ProviderId;

    public string Label { get; set; } = Label;

    public bool Enabled { get; set; } = Enabled;

    public int DisplayOrder { get; set; } = DisplayOrder;

    public IReadOnlyList<string>? RefinementProviderIds { get; set; } = RefinementProviderIds;

    public IReadOnlyList<string> EffectiveRefinementProviderIds =>
        RefinementProviderIds ?? [];

    public string RefinementSummary =>
        string.Join(" → ", EffectiveRefinementProviderIds);
}

public sealed record WorkbenchRegionDraft(
    Guid RegionId,
    string Name,
    bool Enabled,
    double X,
    double Y,
    double Width,
    double Height,
    RegionPriorityLevel Priority,
    int RecognitionIntervalMilliseconds,
    string OcrProviderId,
    string RecognitionLanguage,
    double DetectionScale,
    bool DetectOrientation,
    bool UseCloudOcr,
    long CloudConsentPolicyRevision,
    bool TranslationEnabled,
    IReadOnlyList<WorkbenchChannelDraft> Channels,
    string LineBreakMode,
    string? CustomLineSeparator,
    int MaximumLines,
    string LineAlignment,
    RegionContextRole ContextRole,
    string OverlayMode,
    string OverlayBackgroundMode,
    string? BackgroundColor,
    string? TextColor,
    double OverlayOpacity,
    double BlurRadius,
    bool LockDegradation,
    double PreferredFontSize = 24,
    string? OutlineColor = null,
    double OutlineWidth = 1);

public sealed record WorkbenchTargetDraft(
    Guid TargetId,
    string Name,
    string Kind,
    bool Enabled,
    int DetectionLongEdge,
    bool ScanRemainingArea,
    int RemainingAreaIntervalMilliseconds,
    IReadOnlyList<WorkbenchRegionDraft> Regions);

public sealed record WorkbenchProfileDraft(
    Guid ProfileId,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string GameName,
    string GameDescription,
    int RecentLineCount,
    IReadOnlyList<WorkbenchTargetDraft> Targets);

// Glossary snapshot for the active profile. Carries the entries plus enough context for the page to
// render an intentional empty state when no profile exists yet.
public sealed record GlossarySnapshot(
    Guid ActiveProfileId,
    string ActiveProfileName,
    IReadOnlyList<GlossaryEntry> Entries,
    IReadOnlyList<StylePromptVersion> StylePromptVersions,
    int ActiveStylePromptVersion)
{
    public bool HasActiveProfile => ActiveProfileId != Guid.Empty;

    public static GlossarySnapshot Empty { get; } =
        new(Guid.Empty, string.Empty, [], [], 0);
}

public sealed record StylePromptVersion(
    int Version,
    string Name,
    string Template,
    DateTimeOffset CreatedAt)
{
    public string DisplayName => $"v{Version} — {Name}";
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
    IReadOnlyList<ChannelResult> Channels,
    Guid ProfileId = default,
    Guid SourceEventId = default,
    string ProfileName = "")
{
    public string Timestamp { get; set; } = Timestamp;
    public string SourceText { get; set; } = SourceText;
    public string Region { get; set; } = Region;
    public IReadOnlyList<ChannelResult> Channels { get; set; } = Channels;
    public Guid ProfileId { get; set; } = ProfileId;
    public Guid SourceEventId { get; set; } = SourceEventId;
    public string ProfileName { get; set; } = ProfileName;
    public DateTimeOffset CapturedAtUtc { get; set; }
}

// Pure classification for the history date-grouping headers (spec 5.8: Today / Yesterday / specific
// dates). Kept as a plain enum plus the group's own DateOnly so label text — which is localized and
// therefore belongs to the page layer, not this WinUI-free model — can be resolved from either without
// re-deriving "today" a second time.
public enum HistoryDateGroupKind
{
    Today,
    Yesterday,
    Earlier,
}

public sealed record HistoryDateGroup(
    HistoryDateGroupKind Kind,
    DateOnly Date,
    IReadOnlyList<HistoryEvent> Items);

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
    public string Id { get; set; } = Name;
    public bool IsSelectable { get; set; } = true;
    public bool IsTranslationProvider { get; set; } = true;
    public bool IsCustom { get; set; }
    public bool IsLocalModel { get; set; }
    public bool CanDownloadModel { get; set; }
    public bool CanRemoveModel { get; set; }
    public string? ModelId { get; set; }
    public string? ModelVersion { get; set; }
    public string? ModelLicense { get; set; }
    public string? ModelRuntime { get; set; }
    public string? ModelPackageDirectory { get; set; }
    public string? ModelDownloadSize { get; set; }
    public bool RequiresEndpoint { get; set; }
    public string? Endpoint { get; set; }
    public string? EndpointPlaceholder { get; set; }
    public IReadOnlyList<ProviderCredentialField> Credentials { get; set; } = [];
    public bool CanConfigure => Credentials.Count > 0;
    public bool IsReady =>
        IsSelectable &&
        (!RequiresEndpoint || !string.IsNullOrWhiteSpace(Endpoint)) &&
        (Credentials.Count == 0 || Credentials.All(credential => credential.IsPresent));
}

public sealed record ProviderCredentialField(
    string ReferenceId,
    string DisplayName,
    bool IsPresent)
{
    public string ReferenceId { get; set; } = ReferenceId;
    public string DisplayName { get; set; } = DisplayName;
    public bool IsPresent { get; set; } = IsPresent;
}

public sealed partial class HotkeyEditorRow : ObservableObject
{
    public HotkeyEditorRow(AppHotkeyBinding binding)
    {
        Action = binding.Action;
        Scope = binding.Scope;
        Gesture = binding.Gesture;
        Enabled = binding.Enabled;
    }

    public AppHotkeyAction Action { get; }

    public AppHotkeyScope Scope { get; }

    [ObservableProperty]
    public partial string ActionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScopeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Gesture { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public AppHotkeyBinding ToBinding() => new(Action, Gesture, Enabled, Scope);
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

public enum AppUpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    DisabledByOfflineMode,
    Failed,
}

public sealed record AppUpdateSnapshot(
    AppUpdateStatus Status,
    string CurrentVersion,
    string? AvailableVersion = null,
    string? InstallerPath = null,
    string? ErrorCode = null,
    bool InstallerIsAuthenticodeSigned = false);
