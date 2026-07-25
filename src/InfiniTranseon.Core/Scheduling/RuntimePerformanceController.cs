using InfiniTranseon.Contracts.Runtime;
using System.Runtime.ExceptionServices;

namespace InfiniTranseon.Core.Scheduling;

public interface IPerformanceSnapshotSource
{
    ValueTask<PerformanceSnapshot> SampleAsync(CancellationToken cancellationToken);
}

public interface IRuntimePerformanceController : IAsyncDisposable
{
    Task Completion { get; }
    void Start();
}

public sealed class RuntimePerformancePolicyException : Exception
{
    public RuntimePerformancePolicyException(string errorCode) : base(errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128 ||
            errorCode.Any(character => character is not (>= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("Performance policy error code is invalid.", nameof(errorCode));
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class RuntimePerformanceController : IRuntimePerformanceController
{
    private readonly IPerformanceSnapshotSource _source;
    private readonly IReadOnlyList<RegionPerformancePolicy> _regions;
    private readonly Guid _profileId;
    private readonly long _profileRevision;
    private readonly Func<DegradationEvent, CancellationToken, ValueTask> _report;
    private readonly TimeSpan _sampleInterval;
    private readonly TimeProvider _timeProvider;
    private readonly PerformancePolicyCoordinator _coordinator;
    private readonly SemaphoreSlim _observeGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _started;
    private int _disposed;

    public RuntimePerformanceController(
        IPerformanceSnapshotSource source,
        PerformanceGovernor governor,
        IEnumerable<RegionPerformancePolicy> regions,
        Guid profileId,
        long profileRevision,
        Func<PolicyRevision, CancellationToken, ValueTask<PolicyAcknowledgement>> applyPolicy,
        Func<DegradationEvent, CancellationToken, ValueTask> report,
        TimeSpan sampleInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(applyPolicy);
        ArgumentNullException.ThrowIfNull(report);
        if (profileId == Guid.Empty) throw new ArgumentException("Profile identity cannot be empty.", nameof(profileId));
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        if (sampleInterval < TimeSpan.FromMilliseconds(100) ||
            sampleInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        RegionPerformancePolicy[] ownedRegions = regions.ToArray();
        if (ownedRegions.Length == 0 ||
            ownedRegions.Any(region => region.RegionId == Guid.Empty) ||
            ownedRegions.Select(region => region.RegionId).Distinct().Count() != ownedRegions.Length)
            throw new ArgumentException("Performance regions must have unique non-empty identities.", nameof(regions));

        _source = source;
        _regions = Array.AsReadOnly(ownedRegions);
        _profileId = profileId;
        _profileRevision = profileRevision;
        _report = report;
        _sampleInterval = sampleInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        PerformancePolicyCoordinator? coordinator = null;
        coordinator = new PerformancePolicyCoordinator(
            governor,
            async (revision, cancellationToken) =>
            {
                PolicyAcknowledgement acknowledgement = await applyPolicy(
                    revision, cancellationToken).ConfigureAwait(false);
                if (acknowledgement.Revision != revision.Revision)
                    throw new RuntimeContractException(RuntimeContractError.PolicyAcknowledgementOutOfOrder);
                coordinator!.Acknowledge(acknowledgement);
                if (!acknowledgement.Accepted)
                    throw new RuntimePerformancePolicyException(
                        acknowledgement.RejectionCode ?? "runtime.policy.rejected");
            });
        _coordinator = coordinator;
    }

    public Task Completion => _loop ?? Task.CompletedTask;

    public ITranslationDegradationPolicy DegradationPolicy => _coordinator;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Runtime performance controller has already started.");
        _loop = RunAsync();
    }

    public async ValueTask<DegradationEvent?> ObserveOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await ObserveCoreAsync(now, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DegradationEvent?> ObserveCoreAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (now.Offset != TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        await _observeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PerformanceSnapshot snapshot = await _source.SampleAsync(cancellationToken)
                .ConfigureAwait(false);
            DegradationEvent? change = await _coordinator.ObserveAndSendAsync(
                snapshot,
                _regions,
                _profileId,
                _profileRevision,
                now,
                cancellationToken).ConfigureAwait(false);
            if (change is not null)
                await _report(change, cancellationToken).ConfigureAwait(false);
            return change;
        }
        finally
        {
            _observeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? failure = null;
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) { failure = exception; }
        }
        try
        {
            await _observeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _observeGate.Release();
            if (_source is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (_source is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        finally
        {
            _observeGate.Dispose();
            _stop.Dispose();
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            _stop.Token.ThrowIfCancellationRequested();
            await ObserveCoreAsync(_timeProvider.GetUtcNow(), _stop.Token).ConfigureAwait(false);
            await Task.Delay(_sampleInterval, _timeProvider, _stop.Token).ConfigureAwait(false);
        }
    }
}
