using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Profiles;

public enum CaptureTargetKind
{
    Window,
    Display,
    DesktopFixedRegion,
}

public enum RegionPriority
{
    P0,
    P1,
    P2,
    P3,
}

public enum LineBreakMode
{
    PreserveLines,
    JoinParagraph,
    KeyValueRows,
    CustomSeparator,
    PerBox,
}

public enum LineAlignment
{
    Auto,
    Left,
    Center,
    Right,
}

public enum ProfileRegionContextRole
{
    None,
    Speaker,
    Scene,
    Dialogue,
}

public enum OverlayMode
{
    Replace,
    TranslucentOrBlurred,
    Offset,
    FloatingPanel,
    NoCover,
}

public enum OverlayBackgroundMode
{
    AutomaticContrastBlur,
    Solid,
    Transparent,
    Translucent,
    CachedCleanBackground,
}

public enum ProviderNetworkPolicy
{
    OnlineOnly,
    OfflineOnly,
    Either,
}

public sealed record TargetMachineBinding(int ProcessId, string ExecutablePath, string WindowTitle);

public sealed record ProfileContextSettings
{
    public string GameName { get; init; } = string.Empty;
    public string GameDescription { get; init; } = string.Empty;
    public int RecentLineCount { get; init; } = 6;
}

public sealed record ProfileStylePromptVersion
{
    public int Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Template { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ProfileStylePromptSettings
{
    public int ActiveVersion { get; init; }
    public List<ProfileStylePromptVersion> Versions { get; init; } = [];

    public ProfileStylePromptVersion? Active =>
        Versions.FirstOrDefault(version => version.Version == ActiveVersion);
}

public sealed record ProfileLayoutVariant
{
    public Guid VariantId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public double MinimumAspectRatio { get; init; } = 0.1;
    public double MaximumAspectRatio { get; init; } = 10;
    public int? MinimumWidthPixels { get; init; }
    public int? MaximumWidthPixels { get; init; }
    public double BoundsScale { get; init; } = 1;
    public Dictionary<Guid, NormalizedRect> RegionBounds { get; init; } = [];
}

public sealed record ProfileOcrSettings
{
    public string ProviderId { get; init; } = "auto";
    public string RecognitionLanguage { get; init; } = "auto";
    public double DetectionScale { get; init; } = 1;
    public bool DetectOrientation { get; init; } = true;
    public bool UseCloudOcr { get; init; }
    public long CloudConsentPolicyRevision { get; init; }
    public List<string> PreprocessingSteps { get; init; } = [];
}

public sealed record ProfileOverlaySettings
{
    public OverlayMode Mode { get; init; } = OverlayMode.Replace;
    public OverlayBackgroundMode BackgroundMode { get; init; } = OverlayBackgroundMode.AutomaticContrastBlur;
    public string? BackgroundColor { get; init; }
    public string? TextColor { get; init; }
    public string? OutlineColor { get; init; }
    public double Opacity { get; init; } = 0.85;
    public double BlurRadius { get; init; } = 12;
    public double OutlineWidth { get; init; } = 1;
    public double PreferredFontSize { get; init; } = 24;
    public TimeSpan MinimumDwell { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan CrossfadeDuration { get; init; } = TimeSpan.FromMilliseconds(120);
    public NormalizedRect? OffsetDestination { get; init; }
}

public sealed record ProfileHistorySettings
{
    public bool Enabled { get; init; }
    public int MaxAgeDays { get; init; } = 30;
    public long MaxBytes { get; init; } = 524_288_000;
}

public sealed record ProfileHotkey
{
    public Guid HotkeyId { get; init; } = Guid.NewGuid();
    public string Action { get; init; } = string.Empty;
    public string Gesture { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}

public sealed record ProfileRefinementStep
{
    public Guid StageId { get; init; } = Guid.NewGuid();
    public string ProviderId { get; init; } = string.Empty;
    public string PromptTemplateId { get; init; } = string.Empty;
}

public sealed record ProfileTranslationChannel
{
    public Guid ChannelId { get; init; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string? DisabledReasonCode { get; set; }
    public string InitialProviderId { get; init; } = string.Empty;
    public ProviderNetworkPolicy NetworkPolicy { get; init; } = ProviderNetworkPolicy.Either;
    public List<string> FallbackProviderIds { get; init; } = [];
    public List<ProfileRefinementStep> RefinementSteps { get; init; } = [];
    public int RetryCount { get; init; }
    public int DisplayOrder { get; init; }
    public string DisplayLabel { get; init; } = string.Empty;
    public bool IncludeGameContext { get; init; } = true;
    public bool IncludeRecentContext { get; init; } = true;
    public bool MemoryCacheEnabled { get; init; } = true;
    public bool PersistentCacheEnabled { get; init; }
    public decimal? MaxEstimatedCostPerRequest { get; init; }

    public static ProfileTranslationChannel Create(string providerId) => new()
    {
        InitialProviderId = providerId,
        DisplayLabel = providerId,
    };
}

public sealed record ProfileRegion
{
    public Guid RegionId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? DisabledReasonCode { get; set; }
    public NormalizedRect Bounds { get; init; } = new(0, 0, 1, 1);
    public CaptureAreaKind AreaMode { get; init; } = CaptureAreaKind.UserRegion;
    public RegionPriority Priority { get; init; }
    public TimeSpan RecognitionInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public ProfileOcrSettings Ocr { get; init; } = new();
    public bool TranslationEnabled { get; init; } = true;
    public List<ProfileTranslationChannel> TranslationChannels { get; init; } = [];
    public LineBreakMode LineBreakMode { get; init; } = LineBreakMode.PreserveLines;
    public string? CustomLineSeparator { get; init; }
    public int MaximumLines { get; init; } = 64;
    public LineAlignment LineAlignment { get; init; } = LineAlignment.Auto;
    public ProfileRegionContextRole ContextRole { get; init; }
    public ProfileOverlaySettings Overlay { get; init; } = new();
    public bool LockDegradation { get; init; }

    public static ProfileRegion Create(string name, NormalizedRect bounds) => new()
    {
        Name = name,
        Bounds = bounds,
    };
}

public sealed record ProfileTarget
{
    public Guid TargetId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public CaptureTargetKind Kind { get; init; }
    public bool Enabled { get; set; } = true;
    public string? DisabledReasonCode { get; set; }
    public TargetMachineBinding? MachineBinding { get; init; }
    public OverlayPixelRect? DesktopRegion { get; init; }
    public List<ProfileLayoutVariant> LayoutVariants { get; init; } = [];
    public int DetectionLongEdge { get; init; } = 1920;
    public List<ProfileRegion> Regions { get; init; } = [];
    public bool ScanRemainingArea { get; init; }
    public TimeSpan RemainingAreaInterval { get; init; } = TimeSpan.FromSeconds(1);
    public ProfileRegion? RemainingAreaRegion { get; init; }

    public static ProfileTarget Create(string name, CaptureTargetKind kind) => new()
    {
        Name = name,
        Kind = kind,
    };
}

public sealed record ProfileDocument
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; init; } = CurrentVersion;
    public Guid ProfileId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public bool StrictOffline { get; init; }
    public ProfileContextSettings Context { get; init; } = new();
    public ProfileStylePromptSettings StylePrompt { get; init; } = new();
    public List<ProfileTarget> Targets { get; init; } = [];
    public List<ProfileHotkey> Hotkeys { get; init; } = [];
    public ProfileHistorySettings History { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = new(StringComparer.Ordinal);

    public static ProfileDocument Create(string name, string sourceLanguage, string targetLanguage) => new()
    {
        Name = name,
        SourceLanguage = sourceLanguage,
        TargetLanguage = targetLanguage,
    };
}

public static class ProfileJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static ProfileDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<ProfileDocument>(json, Options) ??
            throw new JsonException("Profile JSON did not contain a document.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 64,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
