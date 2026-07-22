using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public sealed record RuntimeBudgetPoolDefinition
{
    public RuntimeBudgetPoolDefinition(string name, long limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Name = name;
        Limit = limit;
    }

    public string Name { get; }
    public long Limit { get; }
}

public sealed record RuntimeBudgetAdmissionFailure(
    string ErrorCode,
    string PoolName,
    long Requested,
    long Available);

public sealed class RuntimeBudgetLedger
{
    private sealed class Pool(long limit)
    {
        public long Limit { get; } = limit;
        public long Committed { get; set; }
        public long Reserved { get; set; }
    }

    private readonly object _gate = new();
    private readonly Guid _runtimeEpoch;
    private readonly Dictionary<string, Pool> _pools;
    private readonly TimeProvider _timeProvider;
    private long _revision = 1;
    private DateTimeOffset _capturedAtUtc;

    public RuntimeBudgetLedger(
        Guid runtimeEpoch,
        IEnumerable<RuntimeBudgetPoolDefinition> pools,
        TimeProvider? timeProvider = null)
    {
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        ArgumentNullException.ThrowIfNull(pools);
        RuntimeBudgetPoolDefinition[] definitions = pools.ToArray();
        if (definitions.Length == 0 ||
            definitions.Select(pool => pool.Name)
                .Distinct(StringComparer.Ordinal).Count() != definitions.Length)
            throw new ArgumentException(
                "Budget pools must be non-empty and uniquely named.", nameof(pools));
        _runtimeEpoch = runtimeEpoch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capturedAtUtc = _timeProvider.GetUtcNow();
        _pools = definitions.ToDictionary(
            definition => definition.Name,
            definition => new Pool(definition.Limit),
            StringComparer.Ordinal);
    }

    public bool TryReserve(
        string poolName,
        long amount,
        out RuntimeBudgetReservation? reservation,
        out RuntimeBudgetAdmissionFailure? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolName);
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        lock (_gate)
        {
            if (!_pools.TryGetValue(poolName, out Pool? pool))
            {
                reservation = null;
                failure = new RuntimeBudgetAdmissionFailure(
                    "runtime.budget.poolUnknown", poolName, amount, 0);
                return false;
            }
            long available = pool.Limit - pool.Committed - pool.Reserved;
            if (amount > available)
            {
                reservation = null;
                failure = new RuntimeBudgetAdmissionFailure(
                    "runtime.budget.capacity", poolName, amount, available);
                return false;
            }
            pool.Reserved += amount;
            Changed();
            reservation = new RuntimeBudgetReservation(this, poolName, amount);
            failure = null;
            return true;
        }
    }

    public RuntimeBudgetSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RuntimeBudgetSnapshot(
                RuntimeProtocol.CurrentVersion,
                _runtimeEpoch,
                _revision,
                _capturedAtUtc,
                _pools.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new RuntimeBudgetPool(
                        item.Key,
                        item.Value.Limit,
                        item.Value.Committed,
                        item.Value.Reserved)));
        }
    }

    internal void Commit(string poolName, long amount)
    {
        lock (_gate)
        {
            Pool pool = _pools[poolName];
            if (amount > pool.Reserved)
                throw new InvalidOperationException("runtime.budget.reservationInvalid");
            pool.Reserved -= amount;
            pool.Committed += amount;
            Changed();
        }
    }

    internal void Release(string poolName, long amount, bool committed)
    {
        lock (_gate)
        {
            Pool pool = _pools[poolName];
            if (committed)
            {
                if (amount > pool.Committed)
                    throw new InvalidOperationException("runtime.budget.commitmentInvalid");
                pool.Committed -= amount;
            }
            else
            {
                if (amount > pool.Reserved)
                    throw new InvalidOperationException("runtime.budget.reservationInvalid");
                pool.Reserved -= amount;
            }
            Changed();
        }
    }

    private void Changed()
    {
        _revision = checked(_revision + 1);
        _capturedAtUtc = _timeProvider.GetUtcNow();
    }
}

public sealed class RuntimeBudgetReservation : IDisposable
{
    private readonly RuntimeBudgetLedger _owner;
    private readonly string _poolName;
    private readonly long _amount;
    private int _state;

    internal RuntimeBudgetReservation(
        RuntimeBudgetLedger owner,
        string poolName,
        long amount)
    {
        _owner = owner;
        _poolName = poolName;
        _amount = amount;
    }

    public void Commit()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("runtime.budget.reservationNotPending");
        _owner.Commit(_poolName, _amount);
    }

    public void Dispose()
    {
        int previous = Interlocked.Exchange(ref _state, 2);
        if (previous == 2) return;
        _owner.Release(_poolName, _amount, committed: previous == 1);
    }
}
