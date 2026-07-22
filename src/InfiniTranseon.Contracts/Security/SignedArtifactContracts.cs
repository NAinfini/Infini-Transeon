namespace InfiniTranseon.Contracts.Security;

public sealed record Ed25519PublicKey
{
    private readonly byte[] _keyBytes;

    public Ed25519PublicKey(string keyId, ReadOnlySpan<byte> keyBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyBytes.Length != 32)
        {
            throw new ArgumentException("Ed25519 public keys must contain exactly 32 bytes.", nameof(keyBytes));
        }
        KeyId = keyId;
        _keyBytes = keyBytes.ToArray();
    }

    public string KeyId { get; }
    public ReadOnlyMemory<byte> KeyBytes => _keyBytes;
}

public sealed record Ed25519TrustRootSet
{
    public Ed25519TrustRootSet(
        Ed25519PublicKey current,
        Ed25519PublicKey? next,
        IEnumerable<string> revokedKeyIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(revokedKeyIds);
        string[] revoked = revokedKeyIds.ToArray();
        if (revoked.Any(string.IsNullOrWhiteSpace) ||
            revoked.Distinct(StringComparer.Ordinal).Count() != revoked.Length ||
            next is not null && next.KeyId == current.KeyId ||
            revoked.Contains(current.KeyId, StringComparer.Ordinal) ||
            next is not null && revoked.Contains(next.KeyId, StringComparer.Ordinal))
        {
            throw new ArgumentException("Trust-root key identities must be unique and active keys cannot be revoked.");
        }

        Current = current;
        Next = next;
        Ed25519PublicKey[] activeKeys = next is null ? [current] : [current, next];
        ActiveKeys = Array.AsReadOnly(activeKeys);
        RevokedKeyIds = Array.AsReadOnly(revoked);
    }

    public Ed25519PublicKey Current { get; }
    public Ed25519PublicKey? Next { get; }
    public IReadOnlyList<Ed25519PublicKey> ActiveKeys { get; }
    public IReadOnlyList<string> RevokedKeyIds { get; }
}

public interface ISignedSequenceState
{
    long HighestAccepted { get; }
    bool TryAccept(long sequence);
}

public sealed class SignedSequenceState : ISignedSequenceState
{
    private long _highestAccepted;

    public long HighestAccepted => Interlocked.Read(ref _highestAccepted);

    public bool TryAccept(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        while (true)
        {
            long current = Interlocked.Read(ref _highestAccepted);
            if (sequence < current) return false;
            if (sequence == current) return true;
            if (Interlocked.CompareExchange(ref _highestAccepted, sequence, current) == current) return true;
        }
    }
}
