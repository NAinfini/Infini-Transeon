namespace InfiniTranseon.Contracts.Runtime;

public sealed record RuntimeBudgetPool
{
    public RuntimeBudgetPool(string name, long limit, long committed, long reserved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (limit < 0 || committed < 0 || reserved < 0 || committed > limit - reserved)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Budget values must be non-negative and fit within the limit.");
        }

        Name = name;
        Limit = limit;
        Committed = committed;
        Reserved = reserved;
    }

    public string Name { get; }

    public long Limit { get; }

    public long Committed { get; }

    public long Reserved { get; }

    public long Available => Limit - Committed - Reserved;
}

public sealed record RuntimeBudgetSnapshot
{
    public RuntimeBudgetSnapshot(int protocolVersion, Guid runtimeEpoch, IEnumerable<RuntimeBudgetPool> pools)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(protocolVersion, 1);
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        }
        ArgumentNullException.ThrowIfNull(pools);

        RuntimeBudgetPool[] ownedPools = pools.ToArray();
        if (ownedPools.Select(pool => pool.Name).Distinct(StringComparer.Ordinal).Count() != ownedPools.Length)
        {
            throw new ArgumentException("Budget pool names must be unique.", nameof(pools));
        }

        ProtocolVersion = protocolVersion;
        RuntimeEpoch = runtimeEpoch;
        Pools = Array.AsReadOnly(ownedPools);
    }

    public int ProtocolVersion { get; }

    public Guid RuntimeEpoch { get; }

    public IReadOnlyList<RuntimeBudgetPool> Pools { get; }
}
