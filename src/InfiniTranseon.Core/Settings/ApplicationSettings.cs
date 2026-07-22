using System.Globalization;
using InfiniTranseon.Core.Scheduling;

namespace InfiniTranseon.Core.Settings;

public enum FormattingRegionMode
{
    System,
    Explicit,
}

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
    public bool ReducedMotion { get; init; }
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

public static class ApplicationDataPaths
{
    public static string ProfileDatabase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Infini-Transeon",
        "profiles.db");
}
