using InfiniTranseon.App.Controls;

namespace InfiniTranseon.App.DesignData;

// Static design-time data used while pages are developed against fakes.
// Replaced by contract-backed services in frontend Task 1; never ships as a runtime data source.

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

public static class SampleData
{
    public static IReadOnlyList<ProfileCard> Profiles { get; } =
    [
        new("Elden Ring — JP main story", "ELDEN RING™ (window)", "3840×2160 · 150%", "日本語 → 简体中文", 6, 2, "Running", StatusSeverity.Success, "Pause"),
        new("Persona 5 Royal", "P5R.exe (window)", "2560×1440 · 100%", "日本語 → English", 12, 4, "Ready — target matched", StatusSeverity.Info, "Start"),
        new("Steam Deck stream", "Display 2 (full display)", "1920×1080 · 100%", "한국어 → 简体中文", 3, 1, "Target missing", StatusSeverity.Warning, "Locate target"),
        new("Visual novel batch", "NEKOPARA vol.4 (window)", "1920×1080 · 125%", "日本語 → 简体中文", 2, 3, "Provider unhealthy", StatusSeverity.Critical, "Open diagnostics"),
    ];

    public static IReadOnlyList<RunningTarget> RunningTargets { get; } =
    [
        new("Elden Ring — JP main story", "ELDEN RING™", "Healthy", StatusSeverity.Success, "212 ms", "6 / 6"),
        new("Persona 5 Royal", "P5R", "Degraded — OCR interval raised", StatusSeverity.Warning, "348 ms", "10 / 12"),
    ];

    public static IReadOnlyList<HistoryEvent> HistoryEvents { get; } =
    [
        new("14:32:07", "力なき者よ、なぜ来た。", "Dialogue box",
        [
            new("Channel 1", "DeepL", "无力之人啊，你为何而来。", "Success", StatusSeverity.Success, "180 ms"),
            new("Channel 2", "GPT-5.4 + refine", "无力者啊，为何来到此地。", "Success", StatusSeverity.Success, "1.2 s"),
        ]),
        new("14:31:52", "遺灰を使いますか？", "Dialogue box",
        [
            new("Channel 1", "DeepL", "要使用骨灰吗？", "Success", StatusSeverity.Success, "165 ms"),
            new("Channel 2", "GPT-5.4 + refine", "是否使用遗灰？", "Fallback — primary timeout", StatusSeverity.Warning, "3.0 s"),
        ]),
        new("14:31:20", "スタミナが不足している", "HUD hint",
        [
            new("Channel 1", "DeepL", "耐力不足", "Success", StatusSeverity.Success, "142 ms"),
            new("Channel 2", "GPT-5.4 + refine", "—", "Failed — rate limited", StatusSeverity.Critical, "—"),
        ]),
    ];

    public static IReadOnlyList<ProviderRow> Providers { get; } =
    [
        new("DeepL", "NMT · cloud", "Connected", StatusSeverity.Success, "API key stored in Windows Credential Manager"),
        new("OpenAI compatible", "LLM · cloud", "Connected", StatusSeverity.Success, "Custom endpoint · streaming"),
        new("Local MADLAD-400 3B", "NMT · local", "Not installed", StatusSeverity.Neutral, "4.1 GB download · Apache-2.0"),
        new("Baidu Translate", "NMT · cloud", "Credential missing", StatusSeverity.Warning, "Add an API key to enable"),
    ];

    public static IReadOnlyList<DiagnosticEvent> DiagnosticEvents { get; } =
    [
        new("14:29:41", "Persona 5 Royal · HUD region", "OCR interval degraded 200 ms → 500 ms",
            "Scanning continues at a reduced rate; overlay stays current.",
            "Recovers automatically when GPU budget frees; lock the region to prevent auto-changes.",
            StatusSeverity.Warning),
        new("14:12:03", "Visual novel batch · Channel 2", "Provider rate limited (HTTP 429)",
            "Channel 2 slot shows failed state; Channel 1 continues.",
            "Retry now, or raise the per-minute budget in channel settings.",
            StatusSeverity.Critical),
        new("13:58:44", "System", "EngineHost reconnected (revision 7)",
            "All targets resumed with the previous capability budget.",
            "No action needed.",
            StatusSeverity.Success),
    ];

    public static IReadOnlyList<GlossaryEntry> GlossaryEntries { get; } =
    [
        new("遺灰", "遗灰", "Profile · ja→zh-Hans", false, true, "Item category, never auto-correct"),
        new("エルデンリング", "艾尔登法环", "Profile · ja→zh-Hans", false, true, "Title"),
        new("スタミナ", "耐力", "Language pair · ja→zh-Hans", false, false, ""),
        new("パリィ", "弹反", "Language pair · ja→zh-Hans", false, false, "Combat term"),
    ];
}
