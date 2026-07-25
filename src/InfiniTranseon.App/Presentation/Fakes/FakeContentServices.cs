using InfiniTranseon.App.Controls;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.App.Presentation.Fakes;

// Fakes stand in for backend-integrated services in unit tests and for the seams whose real
// counterparts are owned by another work package. They are seeded with the content that previously
// lived in DesignData/SampleData.cs so the testable presentation graph stays deterministic. The real
// composition does NOT register these; production data comes exclusively from the real services.

public sealed class FakeProfileService : IProfileService
{
    private static readonly Guid EldenRingId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly List<ProfileCard> _profiles =
    [
        new(EldenRingId, "Elden Ring — JP main story", "ELDEN RING™ (window)", "3840×2160 · 150%", "日本語 → 简体中文", 6, 2, "Running", StatusSeverity.Success, "Pause"),
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), "Persona 5 Royal", "P5R.exe (window)", "2560×1440 · 100%", "日本語 → English", 12, 4, "Ready — target matched", StatusSeverity.Info, "Start"),
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), "Steam Deck stream", "Display 2 (full display)", "1920×1080 · 100%", "한국어 → 简体中文", 3, 1, "Target missing", StatusSeverity.Warning, "Locate target"),
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), "Visual novel batch", "NEKOPARA vol.4 (window)", "1920×1080 · 125%", "日本語 → 简体中文", 2, 3, "Provider unhealthy", StatusSeverity.Critical, "Open diagnostics"),
    ];

    public Task<IReadOnlyList<ProfileCard>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileCard>>(_profiles.ToArray());

    public Task<ProfileEditModel?> LoadForEditAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        ProfileCard? card = _profiles.FirstOrDefault(profile => profile.ProfileId == profileId);
        ProfileEditModel? model = card is null
            ? null
            : new ProfileEditModel(card.ProfileId, card.Name, "ja", "zh-Hans", Guid.NewGuid(),
                card.TargetDescription, "Window", card.Resolution, "translation.deepl",
                [new ProfileRegionDraft("Dialogue", RegionPriorityLevel.P0)]);
        return Task.FromResult(model);
    }

    public Task<IReadOnlyList<string>> GetTranslationProviderIdsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            _profiles.Any(profile => profile.ProfileId == profileId)
                ? ["translation.deepl"]
                : []);

    public Task<Guid> SaveAsync(ProfileEditModel profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Guid id = profile.ProfileId == Guid.Empty ? Guid.NewGuid() : profile.ProfileId;
        var card = new ProfileCard(id, profile.Name, profile.TargetName, profile.Resolution,
            $"{profile.SourceLanguage} → {profile.TargetLanguage}", profile.Regions.Count,
            string.IsNullOrWhiteSpace(profile.TranslationProviderId) ? 0 : 1, "Ready", StatusSeverity.Info, "Start");
        _profiles.RemoveAll(existing => existing.ProfileId == id);
        _profiles.Insert(0, card);
        return Task.FromResult(id);
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        _profiles.RemoveAll(profile => profile.ProfileId == profileId);
        return Task.CompletedTask;
    }

    public Task ExportAsync(
        Guid profileId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The design-time profile service does not export files.");

    public Task<Guid> ImportAsync(Stream source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The design-time profile service does not import files.");
}

// Deterministic double of the engine runtime facade. It walks the same status machine the real
// service exposes (Stopped → Locating → Starting → Running → Stopping → Stopped) synchronously.
public sealed class FakeRuntimeControlService : IRuntimeControlService
{
    private static readonly IReadOnlyList<RunningTarget> Seed =
    [
        new("Elden Ring — JP main story", "ELDEN RING™", "Healthy", StatusSeverity.Success, "212 ms", "6 / 6"),
        new("Persona 5 Royal", "P5R", "Degraded — OCR interval raised", StatusSeverity.Warning, "348 ms", "10 / 12"),
    ];

    public EngineRuntimeStatus Status { get; private set; } = EngineRuntimeStatus.Stopped;

    public EngineRuntimeStatusChange? LastChange { get; private set; }

    public bool IsPaused { get; private set; }

    public bool IsOverlayVisible { get; private set; } = true;

    public event EventHandler<EngineRuntimeStatusChange>? StatusChanged;

    public event EventHandler? TargetsChanged;

    public IReadOnlyList<RunningTarget> GetRunningTargets() =>
        Status == EngineRuntimeStatus.Running ? Seed : [];

    public Task StartAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }
        SetStatus(EngineRuntimeStatus.Locating);
        SetStatus(EngineRuntimeStatus.Starting);
        SetStatus(EngineRuntimeStatus.Running);
        TargetsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Status != EngineRuntimeStatus.Stopped)
        {
            SetStatus(EngineRuntimeStatus.Stopping);
            SetStatus(EngineRuntimeStatus.Stopped);
            TargetsChanged?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        IsPaused = paused;
        return Task.CompletedTask;
    }

    public Task SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        IsOverlayVisible = visible;
        return Task.CompletedTask;
    }

    public Task RequestManualOcrAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<ProfileRuntimeApplyResult> ApplyProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            Status == EngineRuntimeStatus.Running
                ? ProfileRuntimeApplyResult.HotApplied
                : ProfileRuntimeApplyResult.SavedOnly);
    }

    public Task<RuntimeThumbnail?> RequestThumbnailAsync(
        Guid targetId,
        int maximumLongEdge,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<RuntimeThumbnail?>(null);

    private void SetStatus(EngineRuntimeStatus status)
    {
        Status = status;
        var change = new EngineRuntimeStatusChange(status, DateTimeOffset.UtcNow);
        LastChange = change;
        StatusChanged?.Invoke(this, change);
    }
}

public sealed class FakeWorkbenchService : IWorkbenchService
{
    private readonly WorkbenchProfileDraft _profile = new(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        "Elden Ring — JP main story",
        "ja",
        "zh-Hans",
        "Elden Ring",
        "Open-world action role-playing game",
        6,
        [
            new WorkbenchTargetDraft(
                Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
                "ELDEN RING™",
                "Window",
                true,
                1920,
                true,
                1000,
                [
                    Region(
                        "cccccccc-0000-0000-0000-000000000001",
                        "Dialogue box",
                        RegionPriorityLevel.P0,
                        0.22,
                        0.72,
                        0.56,
                        0.18),
                    Region(
                        "cccccccc-0000-0000-0000-000000000002",
                        "HUD hint",
                        RegionPriorityLevel.P2,
                        0.7,
                        0.08,
                        0.24,
                        0.12),
                ])
        ]);

    public Task<WorkbenchProfileDraft?> LoadAsync(
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<WorkbenchProfileDraft?>(
            profileId == _profile.ProfileId ? _profile : null);

    public Task<ProfileRuntimeApplyResult> SaveAndApplyAsync(
        WorkbenchProfileDraft profile,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ProfileRuntimeApplyResult.HotApplied);

    private static WorkbenchRegionDraft Region(
        string id,
        string name,
        RegionPriorityLevel priority,
        double x,
        double y,
        double width,
        double height) => new(
        Guid.Parse(id),
        name,
        true,
        x,
        y,
        width,
        height,
        priority,
        250,
        "auto",
        "auto",
        1,
        true,
        false,
        0,
        true,
        [
            new WorkbenchChannelDraft(
                Guid.NewGuid(),
                "translation.deepl",
                "DeepL",
                true,
                0),
        ],
        "PreserveLines",
        null,
        64,
        "Auto",
        RegionContextRole.Dialogue,
        "Replace",
        "AutomaticContrastBlur",
        null,
        null,
        0.85,
        12,
        false);
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

    public Task<IReadOnlyList<HistoryEvent>> GetEventsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Seed);

    public void SelectProfile(Guid? profileId)
    {
    }

    public Task SaveCorrectionAsync(
        HistoryEvent historyEvent,
        string correctedText,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
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

    public Task<IReadOnlyList<DiagnosticEvent>> GetEventsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Seed);
}

public sealed class FakeGlossaryService : IGlossaryService
{
    private static readonly Guid ProfileId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly List<GlossaryEntry> _entries =
    [
        new("遺灰", "遗灰", "Profile · ja→zh-Hans", false, true, "Item category, never auto-correct"),
        new("エルデンリング", "艾尔登法环", "Profile · ja→zh-Hans", false, true, "Title"),
        new("スタミナ", "耐力", "Language pair · ja→zh-Hans", false, false, ""),
        new("パリィ", "弹反", "Language pair · ja→zh-Hans", false, false, "Combat term"),
    ];

    public void SelectProfile(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }
    }

    private readonly List<StylePromptVersion> _prompts = [];

    public Task<GlossarySnapshot> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new GlossarySnapshot(
            ProfileId,
            "Elden Ring — JP main story",
            _entries.ToArray(),
            _prompts.ToArray(),
            _prompts.LastOrDefault()?.Version ?? 0));

    public Task AddOrUpdateAsync(
        GlossaryEntry entry,
        string? replacingSourceTerm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string removeKey = string.IsNullOrWhiteSpace(replacingSourceTerm) ? entry.SourceTerm : replacingSourceTerm;
        _entries.RemoveAll(existing =>
            existing.SourceTerm == removeKey || existing.SourceTerm == entry.SourceTerm);
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string sourceTerm, CancellationToken cancellationToken = default)
    {
        _entries.RemoveAll(existing => existing.SourceTerm == sourceTerm);
        return Task.CompletedTask;
    }

    public Task ImportAsync(
        IReadOnlyList<GlossaryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        foreach (GlossaryEntry entry in entries)
        {
            int index = _entries.FindIndex(existing =>
                string.Equals(existing.SourceTerm, entry.SourceTerm, StringComparison.Ordinal));
            if (index >= 0) _entries[index] = entry;
            else _entries.Add(entry);
        }
        return Task.CompletedTask;
    }

    public Task SaveStylePromptVersionAsync(
        string name,
        string template,
        CancellationToken cancellationToken = default)
    {
        _prompts.Add(new StylePromptVersion(
            _prompts.Count + 1,
            name,
            template,
            DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task ActivateStylePromptVersionAsync(
        int version,
        CancellationToken cancellationToken = default)
    {
        if (_prompts.All(item => item.Version != version))
        {
            throw new KeyNotFoundException();
        }
        return Task.CompletedTask;
    }
}

public sealed class FakeSettingsService : ISettingsService
{
    private static readonly IReadOnlyList<ProviderRow> Providers =
    [
        new("DeepL", "NMT · cloud", "Connected", StatusSeverity.Success, "API key stored in Windows Credential Manager")
        {
            Id = "translation.deepl",
            Credentials = [new("api-key", "API key", true)],
        },
        new("OpenAI compatible", "LLM · cloud", "Connected", StatusSeverity.Success, "Custom endpoint · streaming")
        {
            Id = "llm.openai",
            Credentials = [new("openai-api-key", "API key", true)],
        },
        new("Local MADLAD-400 3B", "NMT · local", "Not installed", StatusSeverity.Neutral, "4.1 GB download · Apache-2.0")
        {
            IsSelectable = false,
            IsLocalModel = true,
            CanDownloadModel = false,
        },
        new("Baidu Translate", "NMT · cloud", "Credential missing", StatusSeverity.Warning, "Add credentials to enable")
        {
            Id = "translation.baidu",
            Credentials =
            [
                new("translation.baidu.app-id", "APP ID", false),
                new("translation.baidu.secret", "Secret", false),
            ],
        },
    ];

    private ApplicationSettings _settings = new(
        UiThemePreference.System,
        StrictOffline: false,
        HistoryRetention.Days30,
        UiLanguage: "en-US");

    public Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_settings);

    public Task UpdateAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProviderRow>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Providers);

    public Task<ProviderRow> ImportRestAdapterAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ProviderRow>(
            new NotSupportedException("Fake settings does not persist custom REST adapters."));

    public Task RemoveCustomProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new NotSupportedException("Fake settings does not persist custom REST adapters."));
}

public sealed class FakeAppUpdateService : IAppUpdateService
{
    public AppUpdateSnapshot Snapshot { get; private set; } =
        new(AppUpdateStatus.Idle, "0.9.0");

    public event EventHandler? Changed;

    public Task CheckAsync(
        bool explicitUserAction,
        bool mainUiVisible,
        CancellationToken cancellationToken = default)
    {
        Snapshot = new AppUpdateSnapshot(AppUpdateStatus.UpToDate, Snapshot.CurrentVersion);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task DownloadInstallerAsync(
        bool userApproved,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class FakeSecretReferenceService : ISecretReferenceService
{
    private readonly Dictionary<string, bool> _presence = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DeepL"] = true,
        ["OpenAI compatible"] = true,
        ["Baidu Translate"] = false,
    };

    public Task<IReadOnlyList<SecretReference>> GetReferencesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SecretReference> references =
        [
            new("deepl-api-key", "DeepL", "Windows Credential Manager", _presence["DeepL"]),
            new("openai-api-key", "OpenAI compatible", "Windows Credential Manager", _presence["OpenAI compatible"]),
            new("baidu-api-key", "Baidu Translate", "Windows Credential Manager", _presence["Baidu Translate"]),
        ];
        return Task.FromResult(references);
    }

    public Task<bool> HasSecretAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return Task.FromResult(_presence.TryGetValue(providerId, out bool present) && present);
    }

    public Task SetSecretAsync(string providerId, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _presence[providerId] = true;
        return Task.CompletedTask;
    }

    public Task SetSecretAsync(
        string providerId,
        string credentialReference,
        string secret,
        CancellationToken cancellationToken = default) =>
        SetSecretAsync(providerId, secret, cancellationToken);

    public Task ClearSecretAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _presence[providerId] = false;
        return Task.CompletedTask;
    }

    public Task ClearSecretAsync(
        string providerId,
        string credentialReference,
        CancellationToken cancellationToken = default) =>
        ClearSecretAsync(providerId, cancellationToken);
}
