using System.Globalization;
using InfiniTranseon.Core.Scheduling;

namespace InfiniTranseon.Core.Settings;

public enum FormattingRegionMode
{
    System,
    Explicit,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum HistoryRetentionPolicy
{
    Off,
    Days30,
    Days90,
}

/// <summary>Which engine reads text off the screen.</summary>
public enum OcrBackendPreference
{
    /// <summary>
    /// Windows when it holds a recognizer for the source language, the downloaded local models
    /// otherwise. This is the default because the Windows recognizer costs nothing to install and is
    /// faster, while the local models cover the languages Windows cannot read on this machine.
    /// </summary>
    Automatic,

    /// <summary>Always Windows, and fail plainly when it has no recognizer for the language.</summary>
    Windows,

    /// <summary>Always the downloaded PP-OCR models, even where Windows would also work.</summary>
    Local,
}

public sealed record HotkeySetting(
    string Action,
    string Gesture,
    bool Enabled,
    string Scope,
    IReadOnlyList<HotkeyTargetReference>? SpecificTargets = null);

public sealed record HotkeyTargetReference(Guid ProfileId, Guid ProfileTargetId);

public sealed record PerformanceRuntimeSettings
{
    public PerformancePreset Preset { get; init; } = PerformancePreset.Balanced;
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int OverloadSamples { get; init; } = 3;
    public int RecoverySamples { get; init; } = 5;
    public TimeSpan MinimumDwell { get; init; } = TimeSpan.FromSeconds(5);
    public PerformanceThresholds? CustomThresholds { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Preset) ||
            SampleInterval < TimeSpan.FromMilliseconds(100) ||
            SampleInterval > TimeSpan.FromMinutes(1) ||
            OverloadSamples is < 1 or > 60 || RecoverySamples is < 1 or > 60 ||
            MinimumDwell < TimeSpan.Zero || MinimumDwell > TimeSpan.FromMinutes(10) ||
            (Preset == PerformancePreset.Custom) != (CustomThresholds is not null))
            throw new InvalidDataException("Performance runtime settings are invalid.");
        if (CustomThresholds is not null &&
            (!double.IsFinite(CustomThresholds.MaximumCpuPercent) ||
             CustomThresholds.MaximumCpuPercent is <= 0 or > 100 ||
             CustomThresholds.MaximumWorkingSetBytes <= 0 ||
             !double.IsFinite(CustomThresholds.MaximumGpuFrameTimeMilliseconds) ||
             CustomThresholds.MaximumGpuFrameTimeMilliseconds <= 0 ||
             CustomThresholds.MaximumQueueReplacementsPerMinute <= 0 ||
             !double.IsFinite(CustomThresholds.MaximumOcrP95Milliseconds) ||
             CustomThresholds.MaximumOcrP95Milliseconds <= 0))
            throw new InvalidDataException("Custom performance thresholds are invalid.");
    }
}

public sealed record ApplicationSettings
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; init; } = CurrentVersion;
    public string UiLanguage { get; init; } = "en-US";
    public FormattingRegionMode FormattingRegionMode { get; init; } = FormattingRegionMode.System;
    public string? FormattingRegion { get; init; }
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool StrictOffline { get; init; }
    public OcrBackendPreference OcrBackend { get; init; } = OcrBackendPreference.Automatic;
    public HistoryRetentionPolicy HistoryRetention { get; init; } = HistoryRetentionPolicy.Days30;
    public IReadOnlyList<HotkeySetting>? Hotkeys { get; init; }
    public IReadOnlyDictionary<string, string> ProviderEndpoints { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public bool ReducedMotion { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool CloseToTrayConfirmed { get; init; }
    public IReadOnlyList<Guid> PinnedProfileIds { get; init; } = [];
    public PerformanceRuntimeSettings Performance { get; init; } = new();

    public void Validate()
    {
        if (SchemaVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Application settings schema version {SchemaVersion} is not supported.");
        }
        if (string.IsNullOrWhiteSpace(UiLanguage))
        {
            throw new InvalidDataException("Application UI language is required.");
        }
        if (FormattingRegionMode == FormattingRegionMode.Explicit &&
            string.IsNullOrWhiteSpace(FormattingRegion))
        {
            throw new InvalidDataException("An explicit formatting region is required.");
        }
        if (!Enum.IsDefined(Theme) || !Enum.IsDefined(HistoryRetention) || !Enum.IsDefined(OcrBackend))
        {
            throw new InvalidDataException("Application presentation settings are invalid.");
        }
        if (Hotkeys is { Count: > 32 })
        {
            throw new InvalidDataException("Too many global hotkeys are configured.");
        }
        foreach (HotkeySetting hotkey in Hotkeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(hotkey.Action) || hotkey.Action.Length > 64 ||
                string.IsNullOrWhiteSpace(hotkey.Gesture) || hotkey.Gesture.Length > 128 ||
                string.IsNullOrWhiteSpace(hotkey.Scope) || hotkey.Scope.Length > 64)
            {
                throw new InvalidDataException("A global hotkey setting is invalid.");
            }
            IReadOnlyList<HotkeyTargetReference>? targets = hotkey.SpecificTargets;
            if (targets is { Count: > 128 } || targets?.Any(target =>
                    target.ProfileId == Guid.Empty || target.ProfileTargetId == Guid.Empty) == true ||
                targets?.Distinct().Count() != targets?.Count)
            {
                throw new InvalidDataException("Specific global-hotkey targets are invalid.");
            }
            if (string.Equals(hotkey.Scope, "SpecificTargetGroup", StringComparison.OrdinalIgnoreCase) &&
                targets is { Count: 0 })
            {
                // A missing collection is a legacy document and remains visible for repair; all new
                // writes carry an explicit collection and must not save an empty specific scope.
                throw new InvalidDataException("A specific global hotkey requires at least one target.");
            }
            if (string.Equals(hotkey.Action, "EmergencyStop", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(hotkey.Scope, "AllRunningTargets", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Emergency stop must apply to all running targets.");
            }
        }
        ArgumentNullException.ThrowIfNull(ProviderEndpoints);
        ArgumentNullException.ThrowIfNull(PinnedProfileIds);
        if (PinnedProfileIds.Count > 256 ||
            PinnedProfileIds.Any(profileId => profileId == Guid.Empty) ||
            PinnedProfileIds.Distinct().Count() != PinnedProfileIds.Count)
        {
            throw new InvalidDataException("Pinned profile IDs are invalid.");
        }
        if (ProviderEndpoints.Count > 32)
        {
            throw new InvalidDataException("Too many provider endpoints are configured.");
        }
        foreach ((string providerId, string endpointText) in ProviderEndpoints)
        {
            if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128 ||
                !Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(endpoint.UserInfo) ||
                endpoint.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
            {
                throw new InvalidDataException(
                    $"Provider endpoint '{providerId}' must be an HTTPS origin.");
            }
        }
        ArgumentNullException.ThrowIfNull(Performance);
        Performance.Validate();
        try
        {
            _ = CultureInfo.GetCultureInfo(UiLanguage);
            if (FormattingRegionMode == FormattingRegionMode.Explicit)
                _ = CultureInfo.GetCultureInfo(FormattingRegion!);
        }
        catch (CultureNotFoundException error)
        {
            throw new InvalidDataException("Application language or formatting region is invalid.", error);
        }
    }
}
