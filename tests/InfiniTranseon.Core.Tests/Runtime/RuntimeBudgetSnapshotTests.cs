using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeBudgetSnapshotTests
{
    [Fact]
    public void AvailableIsDerivedFromLimitCommittedAndReserved()
    {
        var pool = new RuntimeBudgetPool("ipc-bytes", 100, 40, 10);

        Assert.Equal(50, pool.Available);
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
}
