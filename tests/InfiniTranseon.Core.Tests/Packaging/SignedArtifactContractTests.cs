using InfiniTranseon.Contracts.Security;

namespace InfiniTranseon.Core.Tests.Packaging;

public sealed class SignedArtifactContractTests
{
    [Fact]
    public void TrustRootSupportsCurrentAndNextEd25519KeysOnly()
    {
        var root = new Ed25519TrustRootSet(
            new Ed25519PublicKey("release-2026-a", Enumerable.Repeat((byte)1, 32).ToArray()),
            new Ed25519PublicKey("release-2026-b", Enumerable.Repeat((byte)2, 32).ToArray()),
            []);

        Assert.Equal(2, root.ActiveKeys.Count);
        Assert.Throws<ArgumentException>(() => new Ed25519PublicKey("short", new byte[31]));
        Assert.Throws<ArgumentException>(() => new Ed25519TrustRootSet(
            root.Current,
            root.Next,
            [root.Current.KeyId]));
    }

    [Fact]
    public void SignedSequenceStateRejectsRollbackButAllowsIdempotentRead()
    {
        var state = new SignedSequenceState();

        Assert.True(state.TryAccept(10));
        Assert.True(state.TryAccept(10));
        Assert.False(state.TryAccept(9));
        Assert.True(state.TryAccept(11));
        Assert.Equal(11, state.HighestAccepted);
    }
}
