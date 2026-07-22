using InfiniTranseon.App.Controls;

namespace InfiniTranseon.App.Presentation.Fakes;

// Fakes stand in for backend-integrated services until the corresponding backend gates land.
// They are seeded with the content that previously lived in DesignData/SampleData.cs so the
// visible UI stays identical.

public sealed class FakeProfileService : IProfileService
{
    private static readonly IReadOnlyList<ProfileCard> Seed =
    [
        new("Elden Ring — JP main story", "ELDEN RING™ (window)", "3840×2160 · 150%", "日本語 → 简体中文", 6, 2, "Running", StatusSeverity.Success, "Pause"),
        new("Persona 5 Royal", "P5R.exe (window)", "2560×1440 · 100%", "日本語 → English", 12, 4, "Ready — target matched", StatusSeverity.Info, "Start"),
        new("Steam Deck stream", "Display 2 (full display)", "1920×1080 · 100%", "한국어 → 简体中文", 3, 1, "Target missing", StatusSeverity.Warning, "Locate target"),
        new("Visual novel batch", "NEKOPARA vol.4 (window)", "1920×1080 · 125%", "日本語 → 简体中文", 2, 3, "Provider unhealthy", StatusSeverity.Critical, "Open diagnostics"),
    ];

    public IReadOnlyList<ProfileCard> GetProfiles() => Seed;
}

public sealed class FakeRuntimeControlService : IRuntimeControlService
{
    private static readonly IReadOnlyList<RunningTarget> Seed =
    [
        new("Elden Ring — JP main story", "ELDEN RING™", "Healthy", StatusSeverity.Success, "212 ms", "6 / 6"),
        new("Persona 5 Royal", "P5R", "Degraded — OCR interval raised", StatusSeverity.Warning, "348 ms", "10 / 12"),
    ];

    public IReadOnlyList<RunningTarget> GetRunningTargets() => Seed;

    // Runtime intents are wired to EngineHost at the backend integration gate; the fake is a
    // deterministic no-op double so the UI stays interactive without a live engine.
    public void PauseAll()
    {
    }

    public void ToggleOverlay()
    {
    }

    public void RequestManualOcr()
    {
    }
}

public sealed class FakeHistoryService : IHistoryService
{
    private static readonly IReadOnlyList<HistoryEvent> Seed =
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

    public IReadOnlyList<HistoryEvent> GetEvents() => Seed;
}

public sealed class FakeDiagnosticsService : IDiagnosticsService
{
    private static readonly IReadOnlyList<DiagnosticEvent> Seed =
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

    public IReadOnlyList<DiagnosticEvent> GetEvents() => Seed;
}

public sealed class FakeGlossaryService : IGlossaryService
{
    private static readonly IReadOnlyList<GlossaryEntry> Seed =
    [
        new("遺灰", "遗灰", "Profile · ja→zh-Hans", false, true, "Item category, never auto-correct"),
        new("エルデンリング", "艾尔登法环", "Profile · ja→zh-Hans", false, true, "Title"),
        new("スタミナ", "耐力", "Language pair · ja→zh-Hans", false, false, ""),
        new("パリィ", "弹反", "Language pair · ja→zh-Hans", false, false, "Combat term"),
    ];

    public IReadOnlyList<GlossaryEntry> GetEntries() => Seed;
}

public sealed class FakeSettingsService : ISettingsService
{
    private static readonly IReadOnlyList<ProviderRow> Seed =
    [
        new("DeepL", "NMT · cloud", "Connected", StatusSeverity.Success, "API key stored in Windows Credential Manager"),
        new("OpenAI compatible", "LLM · cloud", "Connected", StatusSeverity.Success, "Custom endpoint · streaming"),
        new("Local MADLAD-400 3B", "NMT · local", "Not installed", StatusSeverity.Neutral, "4.1 GB download · Apache-2.0"),
        new("Baidu Translate", "NMT · cloud", "Credential missing", StatusSeverity.Warning, "Add an API key to enable"),
    ];

    private ApplicationSettings _settings = new(
        UiThemePreference.System,
        StrictOffline: false,
        HistoryRetention.Days30,
        UiLanguage: "en-US");

    public ApplicationSettings GetSettings() => _settings;

    public void Update(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public IReadOnlyList<ProviderRow> GetProviders() => Seed;
}

public sealed class FakeSecretReferenceService : ISecretReferenceService
{
    private static readonly IReadOnlyList<SecretReference> Seed =
    [
        new("deepl-api-key", "DeepL", "Windows Credential Manager", IsPresent: true),
        new("openai-api-key", "OpenAI compatible", "Windows Credential Manager", IsPresent: true),
        new("baidu-api-key", "Baidu Translate", "Windows Credential Manager", IsPresent: false),
    ];

    public IReadOnlyList<SecretReference> GetReferences() => Seed;

    public bool HasSecret(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return Seed.Any(reference =>
            reference.IsPresent &&
            string.Equals(reference.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    }
}
