using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Scheduling;

namespace InfiniTranseon.Core.Diagnostics;

public sealed class RuntimeStatusReporter
{
    private readonly StatusEventLog _log;
    private readonly TimeProvider _timeProvider;

    public RuntimeStatusReporter(StatusEventLog log, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask ReportTargetLifecycleAsync(
        TargetLifecycleEvent lifecycle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        string errorCode = lifecycle.ErrorCode ??
            $"capture.state.{lifecycle.Target.State}";
        return _log.WriteAsync(new StatusEvent(
            lifecycle.OccurredAtUtc,
            "runtime.capture",
            errorCode,
            "status.runtime.capture.lifecycle",
            lifecycle.ErrorCode is not null || RequiresAttention(lifecycle.Target.State)
                ? StatusEventSeverity.Warning
                : StatusEventSeverity.Information,
            new Dictionary<string, object?>
            {
                ["targetId"] = lifecycle.Target.TargetId.Value,
                ["targetInstanceId"] = lifecycle.Target.TargetInstanceId.Value,
                ["state"] = lifecycle.Target.State,
                ["lifecycleSequence"] = lifecycle.LifecycleSequence,
                ["pixelWidth"] = lifecycle.Target.PixelWidth,
                ["pixelHeight"] = lifecycle.Target.PixelHeight,
                ["dpi"] = lifecycle.Target.Dpi,
                ["nativeErrorCode"] = lifecycle.NativeErrorCode,
            }), cancellationToken);
    }

    public ValueTask ReportPipelineFailureAsync(
        RuntimePipelineFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return _log.WriteAsync(new StatusEvent(
            _timeProvider.GetUtcNow(),
            "runtime.pipeline",
            failure.ErrorCode,
            "status.runtime.pipeline.failure",
            StatusEventSeverity.Error,
            new Dictionary<string, object?>
            {
                ["targetInstanceId"] = failure.TargetInstanceId?.Value,
                ["textTrackId"] = failure.TextTrackId?.Value,
                ["exceptionType"] = new StatusIdentifier(SafeTypeName(failure.Exception)),
            }), cancellationToken);
    }

    public ValueTask ReportDegradationAsync(
        Guid profileId,
        DegradationEvent degradation,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(degradation);
        return _log.WriteAsync(new StatusEvent(
            _timeProvider.GetUtcNow(),
            "runtime.performance",
            degradation.CauseCode,
            "status.runtime.performance.degradation",
            degradation.Kind == DegradationEventKind.Recovered
                ? StatusEventSeverity.Information
                : StatusEventSeverity.Warning,
            new Dictionary<string, object?>
            {
                ["profileId"] = profileId,
                ["kind"] = degradation.Kind,
                ["beforeLevel"] = degradation.BeforeLevel,
                ["afterLevel"] = degradation.AfterLevel,
                ["policyRevision"] = degradation.PolicyRevision,
                ["changedRegionCount"] = degradation.Changes.Count,
            }), cancellationToken);
    }

    private static string SafeTypeName(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string name = exception.GetType().Name;
        return name.Length <= 128 && name.All(char.IsAsciiLetterOrDigit)
            ? name
            : "Exception";
    }

    private static bool RequiresAttention(TargetLifecycleState state) => state is
        TargetLifecycleState.OccludedOrUnsupported or
        TargetLifecycleState.DeviceLost or
        TargetLifecycleState.WaitingForMatch or
        TargetLifecycleState.RunningWithCaptureBorder;
}
