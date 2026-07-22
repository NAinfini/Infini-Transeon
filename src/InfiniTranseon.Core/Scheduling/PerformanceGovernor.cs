namespace InfiniTranseon.Core.Scheduling;

public sealed record PerformanceGovernorOptions(
    int OverloadSamples = 3,
    int RecoverySamples = 5,
    TimeSpan? MinimumDwell = null,
    PerformanceThresholds? CustomThresholds = null);

public sealed class PerformanceGovernor
{
    private readonly PerformanceThresholds _thresholds;
    private readonly PerformanceGovernorOptions _options;
    private int _level;
    private int _overloadSamples;
    private int _recoverySamples;
    private long _policyRevision;
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    private bool _capacityPaused;

    public PerformanceGovernor(PerformancePreset preset, PerformanceGovernorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.OverloadSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RecoverySamples, 1);
        if (preset == PerformancePreset.Custom && options.CustomThresholds is null)
            throw new ArgumentException("Custom performance mode requires explicit thresholds.", nameof(options));
        _thresholds = options.CustomThresholds ?? PerformanceThresholds.ForPreset(preset);
        _options = options;
    }

    public int CurrentLevel => _level;

    public DegradationEvent? Observe(
        PerformanceSnapshot snapshot,
        IReadOnlyList<RegionPerformancePolicy> regions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Any(item => item.RegionId == Guid.Empty || item.Priority is < 0 or > 3))
            throw new ArgumentException("Region performance policies are invalid.", nameof(regions));

        bool overloaded = IsOverloaded(snapshot);
        _overloadSamples = overloaded ? _overloadSamples + 1 : 0;
        _recoverySamples = overloaded ? 0 : _recoverySamples + 1;
        TimeSpan dwell = _options.MinimumDwell ?? TimeSpan.FromSeconds(10);
        if (now - _lastChange < dwell) return null;

        RegionPerformancePolicy[] unlocked = regions.Where(item => !item.Locked).ToArray();
        if (snapshot.HardCapacityExceeded)
        {
            if (_capacityPaused) return null;
            _capacityPaused = true;
            _lastChange = now;
            _policyRevision++;
            return new DegradationEvent(
                DegradationEventKind.PausedCapacity,
                "performance.lockedCapacity",
                _level,
                _level,
                _policyRevision,
                [],
                unlocked.Length == 0
                    ? "Unlock a region or restore capacity before resuming."
                    : "Old work is discarded; processing resumes only after capacity is restored.");
        }
        if (_capacityPaused)
        {
            _capacityPaused = false;
            _lastChange = now;
            _policyRevision++;
            return new DegradationEvent(
                DegradationEventKind.Recovered,
                "performance.capacityRecovered",
                _level,
                _level,
                _policyRevision,
                [],
                "Capacity has been restored; fresh-generation work may resume.");
        }

        if (_overloadSamples >= _options.OverloadSamples && _level < 5)
        {
            int before = _level;
            _level++;
            _overloadSamples = 0;
            _lastChange = now;
            _policyRevision++;
            IReadOnlyList<RegionPolicyChange> changes = BuildChanges(unlocked, _level, applying: true);
            return new DegradationEvent(
                before == 0 ? DegradationEventKind.Started : DegradationEventKind.Changed,
                Cause(snapshot),
                before,
                _level,
                _policyRevision,
                changes,
                "Metrics must remain below 75% of thresholds for the configured recovery sample count.");
        }

        if (_recoverySamples >= _options.RecoverySamples && _level > 0 && IsRecovered(snapshot))
        {
            int before = _level;
            IReadOnlyList<RegionPolicyChange> changes = BuildChanges(unlocked, _level, applying: false);
            _level--;
            _recoverySamples = 0;
            _lastChange = now;
            _policyRevision++;
            return new DegradationEvent(
                _level == 0 ? DegradationEventKind.Recovered : DegradationEventKind.Changed,
                "performance.recovered",
                before,
                _level,
                _policyRevision,
                changes,
                _level == 0 ? "Baseline policy restored." : "Continue sustained recovery.");
        }
        return null;
    }

    private bool IsOverloaded(PerformanceSnapshot value) =>
        value.HardCapacityExceeded ||
        value.ProcessCpuPercent > _thresholds.MaximumCpuPercent ||
        value.WorkingSetBytes > _thresholds.MaximumWorkingSetBytes ||
        value.GpuFrameTimeMilliseconds is double gpu && gpu > _thresholds.MaximumGpuFrameTimeMilliseconds ||
        (value.QueueMetricAvailable &&
            value.QueueReplacementsPerMinute > _thresholds.MaximumQueueReplacementsPerMinute) ||
        (value.OcrMetricAvailable &&
            value.OcrP95Milliseconds > _thresholds.MaximumOcrP95Milliseconds);

    private bool IsRecovered(PerformanceSnapshot value) =>
        !value.HardCapacityExceeded &&
        value.ProcessCpuPercent < _thresholds.MaximumCpuPercent * 0.75 &&
        value.WorkingSetBytes < _thresholds.MaximumWorkingSetBytes * 0.75 &&
        (value.GpuFrameTimeMilliseconds is null ||
         value.GpuFrameTimeMilliseconds < _thresholds.MaximumGpuFrameTimeMilliseconds * 0.75) &&
        (!value.QueueMetricAvailable ||
         value.QueueReplacementsPerMinute < _thresholds.MaximumQueueReplacementsPerMinute * 0.75) &&
        (!value.OcrMetricAvailable ||
         value.OcrP95Milliseconds < _thresholds.MaximumOcrP95Milliseconds * 0.75);

    private string Cause(PerformanceSnapshot value)
    {
        if (value.HardCapacityExceeded) return "performance.hardCapacity";
        if (value.QueueMetricAvailable &&
            value.QueueReplacementsPerMinute > _thresholds.MaximumQueueReplacementsPerMinute)
            return "performance.queuePressure";
        if (value.OcrMetricAvailable &&
            value.OcrP95Milliseconds > _thresholds.MaximumOcrP95Milliseconds)
            return "performance.ocrLatency";
        return "performance.resourcePressure";
    }

    private static IReadOnlyList<RegionPolicyChange> BuildChanges(
        IReadOnlyList<RegionPerformancePolicy> regions,
        int level,
        bool applying)
    {
        DegradationAction action = (DegradationAction)level;
        IEnumerable<RegionPerformancePolicy> eligible = action switch
        {
            DegradationAction.LengthenLowPriorityInterval => regions.Where(item => item.Priority >= 2),
            DegradationAction.PauseRemainingAreaScan => regions.Where(item => item.RemainingArea),
            DegradationAction.UseConfiguredSmallerOcrModel => regions.Where(item => item.SmallerOcrModelConfigured),
            DegradationAction.PauseOptionalRefinement => regions.Where(item => item.OptionalRefinementEnabled),
            _ => regions,
        };
        return eligible.Select(region => new RegionPolicyChange(
            region.RegionId,
            action,
            applying ? "configured" : "degraded",
            applying ? "degraded" : "configured",
            action switch
            {
                DegradationAction.LengthenLowPriorityInterval => "Lower-priority OCR updates less often.",
                DegradationAction.ReduceUnknownAreaCadence => "Automatic unknown-area detection updates less often.",
                DegradationAction.PauseRemainingAreaScan => "Optional remaining-area scan is paused.",
                DegradationAction.UseConfiguredSmallerOcrModel => "Configured smaller OCR model is used.",
                _ => "Optional LLM refinement is paused.",
            })).ToArray();
    }
}
