using System.Diagnostics;

namespace InfiniTranseon.Core.Scheduling;

public sealed record PerformanceSnapshotSupplement(
    double? GpuFrameTimeMilliseconds,
    long QueueReplacementsPerMinute,
    double OcrP95Milliseconds,
    double CaptureFrameArrivalRate,
    bool HardCapacityExceeded);

public sealed class ProcessPerformanceSnapshotSource : IPerformanceSnapshotSource, IDisposable
{
    private readonly Process[] _processes;
    private readonly long _hardCommittedByteLimit;
    private readonly Func<CancellationToken, ValueTask<PerformanceSnapshotSupplement?>>? _supplement;
    private TimeSpan _lastProcessorTime;
    private long _lastTimestamp;
    private int _disposed;

    public ProcessPerformanceSnapshotSource(
        IEnumerable<int> processIds,
        long hardCommittedByteLimit,
        Func<CancellationToken, ValueTask<PerformanceSnapshotSupplement?>>? supplement = null)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(hardCommittedByteLimit, 1);
        int[] ids = processIds.Distinct().ToArray();
        if (ids.Length == 0 || ids.Any(id => id <= 0))
            throw new ArgumentException("Performance process IDs must be positive and non-empty.", nameof(processIds));
        try { _processes = ids.Select(Process.GetProcessById).ToArray(); }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("performance.processUnavailable", exception);
        }
        _hardCommittedByteLimit = hardCommittedByteLimit;
        _supplement = supplement;
    }

    public async ValueTask<PerformanceSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        long timestamp = Stopwatch.GetTimestamp();
        TimeSpan processorTime = TimeSpan.Zero;
        long workingSet = 0;
        try
        {
            foreach (Process process in _processes)
            {
                process.Refresh();
                if (process.HasExited) throw new InvalidOperationException("performance.processExited");
                processorTime += process.TotalProcessorTime;
                workingSet = checked(workingSet + process.WorkingSet64);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("performance.processUnavailable", exception);
        }

        double cpu = 0;
        if (_lastTimestamp != 0 && timestamp > _lastTimestamp)
        {
            double elapsedSeconds = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds;
            double processorSeconds = (processorTime - _lastProcessorTime).TotalSeconds;
            cpu = Math.Clamp(
                processorSeconds / elapsedSeconds / Environment.ProcessorCount * 100,
                0,
                100);
        }
        _lastTimestamp = timestamp;
        _lastProcessorTime = processorTime;

        PerformanceSnapshotSupplement? supplement = _supplement is null
            ? null
            : await _supplement(cancellationToken).ConfigureAwait(false);
        return new PerformanceSnapshot(
            cpu,
            workingSet,
            supplement?.GpuFrameTimeMilliseconds,
            supplement?.QueueReplacementsPerMinute ?? 0,
            supplement?.OcrP95Milliseconds ?? 0,
            supplement?.CaptureFrameArrivalRate ?? 0,
            workingSet > _hardCommittedByteLimit || supplement?.HardCapacityExceeded == true,
            QueueMetricAvailable: supplement is not null,
            OcrMetricAvailable: supplement is not null,
            CaptureMetricAvailable: supplement is not null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (Process process in _processes) process.Dispose();
    }
}
