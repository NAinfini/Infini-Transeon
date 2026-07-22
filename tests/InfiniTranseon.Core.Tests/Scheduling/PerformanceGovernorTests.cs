using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Scheduling;

namespace InfiniTranseon.Core.Tests.Scheduling;

public sealed class PerformanceGovernorTests
{
    [Fact]
    public void SustainedOverloadDegradesOnlyUnlockedRegionsAndRecoversInReverseOrder()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var governor = new PerformanceGovernor(
            PerformancePreset.Balanced,
            new PerformanceGovernorOptions(2, 2, TimeSpan.Zero));
        RegionPerformancePolicy[] regions =
        [
            new(Guid.NewGuid(), 0, true, TimeSpan.FromMilliseconds(100)),
            new(Guid.NewGuid(), 3, false, TimeSpan.FromSeconds(1)),
        ];

        Assert.Null(governor.Observe(Overloaded(), regions, now));
        DegradationEvent started = Assert.IsType<DegradationEvent>(
            governor.Observe(Overloaded(), regions, now.AddSeconds(1)));

        Assert.Equal(DegradationEventKind.Started, started.Kind);
        Assert.DoesNotContain(started.Changes, item => item.RegionId == regions[0].RegionId);
        Assert.Contains(started.Changes, item => item.RegionId == regions[1].RegionId &&
            item.Action == DegradationAction.LengthenLowPriorityInterval);

        Assert.Null(governor.Observe(Healthy(), regions, now.AddSeconds(2)));
        DegradationEvent recovered = Assert.IsType<DegradationEvent>(
            governor.Observe(Healthy(), regions, now.AddSeconds(3)));
        Assert.Equal(DegradationEventKind.Recovered, recovered.Kind);
        Assert.Equal(0, recovered.AfterLevel);
    }

    [Fact]
    public void HardCapacityWithEveryRegionLockedPausesExplicitlyInsteadOfChangingPolicy()
    {
        var governor = new PerformanceGovernor(
            PerformancePreset.Performance,
            new PerformanceGovernorOptions(1, 2, TimeSpan.Zero));
        RegionPerformancePolicy[] locked =
        [
            new(Guid.NewGuid(), 0, true, TimeSpan.FromMilliseconds(16)),
            new(Guid.NewGuid(), 1, true, TimeSpan.FromMilliseconds(33)),
        ];

        DegradationEvent result = Assert.IsType<DegradationEvent>(governor.Observe(
            new PerformanceSnapshot(99, 3L * 1024 * 1024 * 1024, null, 100, 4000, 0, true),
            locked,
            DateTimeOffset.UtcNow));

        Assert.Equal(DegradationEventKind.PausedCapacity, result.Kind);
        Assert.Empty(result.Changes);
        Assert.Equal("performance.lockedCapacity", result.CauseCode);
    }

    [Fact]
    public void MissingGpuMetricDoesNotPretendToBeZeroOrTriggerGpuAction()
    {
        var governor = new PerformanceGovernor(
            PerformancePreset.Eco,
            new PerformanceGovernorOptions(1, 2, TimeSpan.Zero));
        RegionPerformancePolicy[] regions =
        [
            new(Guid.NewGuid(), 2, false, TimeSpan.FromSeconds(1)),
        ];

        DegradationEvent? result = governor.Observe(
            new PerformanceSnapshot(5, 100_000_000, null, 0, 10, 30, false),
            regions,
            DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void MissingQueueAndOcrMetricsDoNotTriggerTheirThresholds()
    {
        var governor = new PerformanceGovernor(
            PerformancePreset.Eco,
            new PerformanceGovernorOptions(1, 2, TimeSpan.Zero));

        DegradationEvent? result = governor.Observe(
            new PerformanceSnapshot(
                5, 100_000_000, null, long.MaxValue, double.MaxValue, 0, false,
                QueueMetricAvailable: false,
                OcrMetricAvailable: false,
                CaptureMetricAvailable: false),
            [new RegionPerformancePolicy(Guid.NewGuid(), 2, false, TimeSpan.FromSeconds(1))],
            DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessSourceMarksMetricsItCannotObserveAsUnavailable()
    {
        using var source = new ProcessPerformanceSnapshotSource(
            [Environment.ProcessId],
            RuntimeCapabilities.VersionOne.MaxEngineCommittedBytes);

        PerformanceSnapshot snapshot = await source.SampleAsync(
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.False(snapshot.QueueMetricAvailable);
        Assert.False(snapshot.OcrMetricAvailable);
        Assert.False(snapshot.CaptureMetricAvailable);
        Assert.Null(snapshot.GpuFrameTimeMilliseconds);
    }

    [Fact]
    public async Task PolicyRevisionIsSentAndReconnectReturnsTheLastCompleteSnapshot()
    {
        PolicyRevision? sent = null;
        var governor = new PerformanceGovernor(
            PerformancePreset.Eco,
            new PerformanceGovernorOptions(1, 2, TimeSpan.Zero));
        var coordinator = new PerformancePolicyCoordinator(
            governor,
            (revision, _) => { sent = revision; return ValueTask.CompletedTask; });
        RegionPerformancePolicy[] regions =
        [
            new(Guid.NewGuid(), 3, false, TimeSpan.FromSeconds(1)),
        ];
        Guid profileId = Guid.NewGuid();

        DegradationEvent? change = await coordinator.ObserveAndSendAsync(
            Overloaded(), regions, profileId, 7, DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.NotNull(change);
        PolicyRevision actual = Assert.IsType<PolicyRevision>(sent);
        Assert.Equal(change.PolicyRevision, actual.Revision);
        Assert.Equal(actual, coordinator.CreateReconnectSnapshot());
        coordinator.Acknowledge(new PolicyAcknowledgement(actual.Revision, false, "runtime.policy.capacity"));
    }

    [Fact]
    public async Task PolicySnapshotsRemainCumulativeAndRecoverInReverseOrder()
    {
        var sent = new List<PolicyRevision>();
        var governor = new PerformanceGovernor(
            PerformancePreset.Eco,
            new PerformanceGovernorOptions(1, 1, TimeSpan.Zero));
        var coordinator = new PerformancePolicyCoordinator(
            governor,
            (revision, _) =>
            {
                sent.Add(revision);
                return ValueTask.CompletedTask;
            });
        Guid regionId = Guid.NewGuid();
        RegionPerformancePolicy[] regions =
        [
            new(regionId, 3, false, TimeSpan.FromSeconds(1)),
        ];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid profileId = Guid.NewGuid();

        await coordinator.ObserveAndSendAsync(Overloaded(), regions, profileId, 1, now,
            TestContext.Current.CancellationToken);
        await coordinator.ObserveAndSendAsync(Overloaded(), regions, profileId, 1, now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        await coordinator.ObserveAndSendAsync(Healthy(), regions, profileId, 1, now.AddSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal("1:degraded", sent[0].RegionPolicies[new RegionId(regionId)]);
        Assert.Equal("1:degraded;2:degraded", sent[1].RegionPolicies[new RegionId(regionId)]);
        Assert.Equal("1:degraded", sent[2].RegionPolicies[new RegionId(regionId)]);
    }

    [Fact]
    public async Task HardCapacityPublishesExplicitPauseForLockedRegions()
    {
        PolicyRevision? sent = null;
        var coordinator = new PerformancePolicyCoordinator(
            new PerformanceGovernor(
                PerformancePreset.Eco,
                new PerformanceGovernorOptions(1, 1, TimeSpan.Zero)),
            (revision, _) =>
            {
                sent = revision;
                return ValueTask.CompletedTask;
            });
        Guid regionId = Guid.NewGuid();
        RegionPerformancePolicy[] regions =
        [
            new(regionId, 0, true, TimeSpan.FromMilliseconds(100)),
        ];

        await coordinator.ObserveAndSendAsync(
            new PerformanceSnapshot(100, long.MaxValue, null, 0, 0, 0, true),
            regions,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.Equal("0:paused", sent!.RegionPolicies[new RegionId(regionId)]);
    }

    [Fact]
    public async Task RefinementPauseStaysLocalAndDoesNotSendUnsupportedNativeAction()
    {
        var sent = new List<PolicyRevision>();
        var coordinator = new PerformancePolicyCoordinator(
            new PerformanceGovernor(
                PerformancePreset.Eco,
                new PerformanceGovernorOptions(1, 1, TimeSpan.Zero)),
            (revision, _) =>
            {
                sent.Add(revision);
                return ValueTask.CompletedTask;
            });
        Guid regionId = Guid.NewGuid();
        RegionPerformancePolicy[] regions =
        [
            new(regionId, 3, false, TimeSpan.FromSeconds(1),
                RemainingArea: true, OptionalRefinementEnabled: true),
        ];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid profileId = Guid.NewGuid();
        for (int index = 0; index < 5; index++)
        {
            await coordinator.ObserveAndSendAsync(
                Overloaded(), regions, profileId, 1, now.AddSeconds(index),
                TestContext.Current.CancellationToken);
        }

        Assert.True(coordinator.ShouldPauseOptionalRefinement(regionId));
        Assert.DoesNotContain("5:", sent[^1].RegionPolicies[new RegionId(regionId)]);
    }

    [Fact]
    public async Task RuntimeControllerSamplesAppliesAcknowledgedPolicyAndReportsDegradation()
    {
        Guid profileId = Guid.NewGuid();
        Guid regionId = Guid.NewGuid();
        PolicyRevision? sent = null;
        DegradationEvent? reported = null;
        var source = new StubPerformanceSource(Overloaded());
        var controller = new RuntimePerformanceController(
            source,
            new PerformanceGovernor(
                PerformancePreset.Eco,
                new PerformanceGovernorOptions(1, 1, TimeSpan.Zero)),
            [new RegionPerformancePolicy(regionId, 3, false, TimeSpan.FromSeconds(1))],
            profileId,
            profileRevision: 4,
            (revision, _) =>
            {
                sent = revision;
                return ValueTask.FromResult(new PolicyAcknowledgement(revision.Revision, true, null));
            },
            (change, _) =>
            {
                reported = change;
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        DegradationEvent? change = await controller.ObserveOnceAsync(
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.NotNull(change);
        Assert.Equal(change, reported);
        Assert.Equal("1:degraded", sent!.RegionPolicies[new RegionId(regionId)]);
        await controller.DisposeAsync();
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task RuntimeControllerSurfacesNativePolicyRejection()
    {
        Guid regionId = Guid.NewGuid();
        var controller = new RuntimePerformanceController(
            new StubPerformanceSource(Overloaded()),
            new PerformanceGovernor(
                PerformancePreset.Eco,
                new PerformanceGovernorOptions(1, 1, TimeSpan.Zero)),
            [new RegionPerformancePolicy(regionId, 3, false, TimeSpan.FromSeconds(1))],
            Guid.NewGuid(),
            1,
            (revision, _) => ValueTask.FromResult(new PolicyAcknowledgement(
                revision.Revision, false, "runtime.policy.capacity")),
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1));

        RuntimePerformancePolicyException exception = await Assert.ThrowsAsync<RuntimePerformancePolicyException>(
            () => controller.ObserveOnceAsync(
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("runtime.policy.capacity", exception.ErrorCode);
        await controller.DisposeAsync();
    }

    private static PerformanceSnapshot Overloaded() =>
        new(95, 700_000_000, 20, 30, 900, 60, false);

    private static PerformanceSnapshot Healthy() =>
        new(5, 150_000_000, 1, 0, 30, 30, false);

    private sealed class StubPerformanceSource(PerformanceSnapshot snapshot)
        : IPerformanceSnapshotSource, IDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask<PerformanceSnapshot> SampleAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);
        public void Dispose() => Disposed = true;
    }
}
