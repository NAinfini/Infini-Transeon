namespace InfiniTranseon.Core.Scheduling;

public enum PerformancePreset
{
    Eco,
    Balanced,
    Performance,
    Custom,
}

public enum DegradationAction
{
    LengthenLowPriorityInterval = 1,
    ReduceUnknownAreaCadence = 2,
    PauseRemainingAreaScan = 3,
    UseConfiguredSmallerOcrModel = 4,
    PauseOptionalRefinement = 5,
}

public enum DegradationEventKind
{
    Started,
    Changed,
    Recovered,
    PausedCapacity,
}

public sealed record RegionPerformancePolicy(
    Guid RegionId,
    int Priority,
    bool Locked,
    TimeSpan RecognitionInterval,
    bool RemainingArea = false,
    bool SmallerOcrModelConfigured = false,
    bool OptionalRefinementEnabled = true);

public sealed record RegionPolicyChange(
    Guid RegionId,
    DegradationAction Action,
    string BeforeValue,
    string AfterValue,
    string Impact);

public sealed record DegradationEvent(
    DegradationEventKind Kind,
    string CauseCode,
    int BeforeLevel,
    int AfterLevel,
    long PolicyRevision,
    IReadOnlyList<RegionPolicyChange> Changes,
    string RecoveryCondition);

public sealed record PerformanceSnapshot(
    double ProcessCpuPercent,
    long WorkingSetBytes,
    double? GpuFrameTimeMilliseconds,
    long QueueReplacementsPerMinute,
    double OcrP95Milliseconds,
    double CaptureFrameArrivalRate,
    bool HardCapacityExceeded,
    bool QueueMetricAvailable = true,
    bool OcrMetricAvailable = true,
    bool CaptureMetricAvailable = true);

public sealed record PerformanceThresholds(
    double MaximumCpuPercent,
    long MaximumWorkingSetBytes,
    double MaximumGpuFrameTimeMilliseconds,
    long MaximumQueueReplacementsPerMinute,
    double MaximumOcrP95Milliseconds)
{
    public static PerformanceThresholds ForPreset(PerformancePreset preset) => preset switch
    {
        PerformancePreset.Eco => new(20, 400L * 1024 * 1024, 12, 100, 500),
        PerformancePreset.Balanced => new(35, 600L * 1024 * 1024, 16, 250, 750),
        PerformancePreset.Performance => new(60, 900L * 1024 * 1024, 24, 500, 1000),
        _ => new(35, 600L * 1024 * 1024, 16, 250, 750),
    };
}
