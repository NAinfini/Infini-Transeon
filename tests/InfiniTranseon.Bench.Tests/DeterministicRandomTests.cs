using System.Linq;
using GameOcrBench;

namespace InfiniTranseon.Bench.Tests;

public sealed class DeterministicRandomTests
{
    [Fact]
    public void SameSeedProducesAnIdenticalSequence()
    {
        var first = new DeterministicRandom(0xDEADBEEFUL);
        var second = new DeterministicRandom(0xDEADBEEFUL);

        ulong[] a = Enumerable.Range(0, 256).Select(_ => first.NextUInt64()).ToArray();
        ulong[] b = Enumerable.Range(0, 256).Select(_ => second.NextUInt64()).ToArray();

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeedsDivergeImmediately()
    {
        var first = new DeterministicRandom(1UL);
        var second = new DeterministicRandom(2UL);

        Assert.NotEqual(first.NextUInt64(), second.NextUInt64());
    }

    [Fact]
    public void SplitMix64MatchesTheReferenceStreamForSeedZero()
    {
        // Golden values pin the exact SplitMix64 constants so an accidental algorithm
        // change (which would silently break cross-host reproducibility) is caught.
        var rng = new DeterministicRandom(0UL);

        Assert.Equal(0xE220A8397B1DCDAFUL, rng.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, rng.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, rng.NextUInt64());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(1000)]
    public void NextIntStaysWithinTheRequestedHalfOpenRange(int maxExclusive)
    {
        var rng = new DeterministicRandom(0x1234_5678_9ABC_DEF0UL);

        for (int i = 0; i < 10_000; i++)
        {
            int value = rng.NextInt(maxExclusive);
            Assert.InRange(value, 0, maxExclusive - 1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NextIntRejectsNonPositiveBounds(int maxExclusive)
    {
        var rng = new DeterministicRandom(7UL);

        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(maxExclusive));
    }
}
