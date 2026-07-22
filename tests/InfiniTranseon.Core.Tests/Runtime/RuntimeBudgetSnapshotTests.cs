using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeBudgetSnapshotTests
{
    [Fact]
    public void EmptyRuntimeEpochIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new RuntimeBudgetSnapshot(1, Guid.Empty, []));
    }

    [Fact]
    public void AvailableIsDerivedFromLimitCommittedAndReserved()
    {
        var pool = new RuntimeBudgetPool(
            "ipc-bytes", 100, 40, 10, RuntimeBudgetUnit.Bytes);

        Assert.Equal(50, pool.Available);
        Assert.Equal(RuntimeBudgetUnit.Bytes, pool.Unit);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 0, -1)]
    [InlineData(10, 8, 3)]
    public void InvalidPoolAccountingIsRejected(long limit, long committed, long reserved)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RuntimeBudgetPool("invalid", limit, committed, reserved));
    }

    [Fact]
    public void SnapshotRejectsDuplicatePoolNames()
    {
        RuntimeBudgetPool[] pools =
        [
            new("ocr-slots", 4, 1, 0),
            new("ocr-slots", 4, 0, 1),
        ];

        Assert.Throws<ArgumentException>(() => new RuntimeBudgetSnapshot(1, Guid.NewGuid(), pools));
    }

    [Fact]
    public void SnapshotOwnsAnImmutableCopyOfPools()
    {
        RuntimeBudgetPool[] pools = [new("ocr-slots", 4, 1, 0)];
        var snapshot = new RuntimeBudgetSnapshot(1, Guid.NewGuid(), pools);

        pools[0] = new RuntimeBudgetPool("changed", 1, 0, 0);

        Assert.Equal("ocr-slots", snapshot.Pools[0].Name);
    }

    [Fact]
    public void LedgerReservesCommitsAndReleasesWithoutExceedingItsPool()
    {
        Guid epoch = Guid.NewGuid();
        var ledger = new RuntimeBudgetLedger(
            epoch,
            [new RuntimeBudgetPoolDefinition("gpu.adapter.1.bytes", 100)]);

        Assert.True(ledger.TryReserve(
            "gpu.adapter.1.bytes", 60, out RuntimeBudgetReservation? current,
            out RuntimeBudgetAdmissionFailure? failure));
        Assert.Null(failure);
        Assert.Equal((0, 60, 40), Accounting(ledger.Snapshot()));

        current!.Commit();
        Assert.Equal((60, 0, 40), Accounting(ledger.Snapshot()));

        Assert.True(ledger.TryReserve(
            "gpu.adapter.1.bytes", 40, out RuntimeBudgetReservation? replacement,
            out failure));
        Assert.False(ledger.TryReserve(
            "gpu.adapter.1.bytes", 1, out _, out failure));
        Assert.Equal("runtime.budget.capacity", failure!.ErrorCode);
        Assert.Equal(0, failure.Available);
        Assert.Equal((60, 40, 0), Accounting(ledger.Snapshot()));

        replacement!.Dispose();
        current.Dispose();
        Assert.Equal((0, 0, 100), Accounting(ledger.Snapshot()));

        static (long Committed, long Reserved, long Available) Accounting(
            RuntimeBudgetSnapshot snapshot)
        {
            RuntimeBudgetPool pool = Assert.Single(snapshot.Pools);
            return (pool.Committed, pool.Reserved, pool.Available);
        }
    }

    [Fact]
    public void LedgerReportsUnknownPoolsAndRejectsDoubleCommit()
    {
        var ledger = new RuntimeBudgetLedger(
            Guid.NewGuid(),
            [new RuntimeBudgetPoolDefinition("ocr.sessions", 2)]);

        Assert.False(ledger.TryReserve(
            "missing", 1, out _, out RuntimeBudgetAdmissionFailure? failure));
        Assert.Equal("runtime.budget.poolUnknown", failure!.ErrorCode);
        Assert.True(ledger.TryReserve(
            "ocr.sessions", 1, out RuntimeBudgetReservation? reservation, out _));
        reservation!.Commit();

        Assert.Throws<InvalidOperationException>(reservation.Commit);
        reservation.Dispose();
    }

    [Fact]
    public void LedgerAdvancesSnapshotRevisionForEveryAccountingTransition()
    {
        var ledger = new RuntimeBudgetLedger(
            Guid.NewGuid(),
            [new RuntimeBudgetPoolDefinition("ocr.sessions", 2)]);

        Assert.Equal(1, ledger.Snapshot().SnapshotRevision);
        Assert.True(ledger.TryReserve(
            "ocr.sessions", 1, out RuntimeBudgetReservation? reservation, out _));
        Assert.Equal(2, ledger.Snapshot().SnapshotRevision);
        reservation!.Commit();
        Assert.Equal(3, ledger.Snapshot().SnapshotRevision);
        reservation.Dispose();
        Assert.Equal(4, ledger.Snapshot().SnapshotRevision);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, ledger.Snapshot().CapturedAtUtc);
    }

    [Fact]
    public void BudgetPayloadRoundTripsRevisionTimestampUnitsAndAccounting()
    {
        Guid epoch = Guid.NewGuid();
        var capturedAt = new DateTimeOffset(2026, 7, 21, 12, 30, 0, TimeSpan.Zero);
        var snapshot = new RuntimeBudgetSnapshot(
            RuntimeProtocol.CurrentVersion,
            epoch,
            snapshotRevision: 9,
            capturedAt,
            [
                new RuntimeBudgetPool(
                    "engine.committed.bytes", 1000, 400, 100,
                    RuntimeBudgetUnit.Bytes),
                new RuntimeBudgetPool(
                    "engine.targets.slots", 8, 2, 1,
                    RuntimeBudgetUnit.Slots),
            ]);

        byte[] payload = RuntimeBudgetSnapshotPayloadCodec.Encode(snapshot);
        RuntimeBudgetSnapshot decoded = RuntimeBudgetSnapshotPayloadCodec.Decode(payload);

        Assert.Equal(snapshot.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(snapshot.RuntimeEpoch, decoded.RuntimeEpoch);
        Assert.Equal(snapshot.Pools, decoded.Pools);
        Assert.Equal(9, decoded.SnapshotRevision);
        Assert.Equal(capturedAt, decoded.CapturedAtUtc);
        Assert.Equal(RuntimeBudgetUnit.Slots, decoded.Pools[1].Unit);
    }
}
