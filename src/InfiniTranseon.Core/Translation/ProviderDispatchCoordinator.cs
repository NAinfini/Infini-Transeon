using System.Collections.Concurrent;
using System.Diagnostics;
using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation;

public sealed record ProviderDispatchLimits(
    int GlobalConcurrency = 8,
    int PerProfileConcurrency = 4,
    int PerProviderConcurrency = 2,
    int RequestsPerMinute = 120);

public sealed class ProviderDispatchRejectedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class ProviderDispatchCoordinator : IDisposable
{
    private sealed class BudgetState(decimal limit)
    {
        public decimal Limit { get; } = limit;
        public decimal Reserved { get; set; }
        public decimal Spent { get; set; }
    }

    private sealed class TokenBucket(int capacity)
    {
        private readonly object _gate = new();
        private readonly int _capacity = capacity;
        private double _tokens = capacity;
        private long _lastTimestamp = Stopwatch.GetTimestamp();

        public TimeSpan TakeOrDelay()
        {
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedMinutes = (double)(now - _lastTimestamp) / Stopwatch.Frequency / 60d;
                _tokens = Math.Min(_capacity, _tokens + elapsedMinutes * _capacity);
                _lastTimestamp = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return TimeSpan.Zero;
                }
                return TimeSpan.FromMinutes((1 - _tokens) / _capacity);
            }
        }
    }

    private readonly ProviderDispatchLimits _limits;
    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _profiles = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly object _budgetGate = new();
    private readonly Dictionary<(Guid Profile, string Provider, string Unit, string Currency), BudgetState> _budgets = [];
    private int _disposed;

    public ProviderDispatchCoordinator(ProviderDispatchLimits? limits = null)
    {
        _limits = limits ?? new ProviderDispatchLimits();
        if (_limits.GlobalConcurrency < 1 || _limits.PerProfileConcurrency < 1 ||
            _limits.PerProviderConcurrency < 1 || _limits.RequestsPerMinute < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _global = new SemaphoreSlim(_limits.GlobalConcurrency, _limits.GlobalConcurrency);
    }

    public void ConfigureBudget(
        Guid profileId,
        string providerId,
        string billingUnit,
        string currency,
        decimal limit)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(billingUnit);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (providerId.Length > 128 || billingUnit.Length > 64 || currency.Length > 16)
            throw new ArgumentOutOfRangeException(nameof(providerId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_budgetGate)
            _budgets[(profileId, providerId, billingUnit, currency)] = new BudgetState(limit);
    }

    public async ValueTask<ProviderDispatchLease> AcquireAsync(
        Guid profileId,
        string providerId,
        ProviderCostReservation cost,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (providerId.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(providerId));
        ArgumentNullException.ThrowIfNull(cost);
        if (string.IsNullOrWhiteSpace(cost.BillingUnit) || cost.BillingUnit.Length > 64 ||
            cost.MaximumUnits < 0 || cost.MaximumCost < 0 ||
            cost.MaximumCost.HasValue != (cost.Currency is not null) ||
            cost.Currency is { } currency &&
                (string.IsNullOrWhiteSpace(currency) || currency.Length > 16))
            throw new ArgumentOutOfRangeException(nameof(cost));
        SemaphoreSlim profile = _profiles.GetOrAdd(profileId, _ =>
            new SemaphoreSlim(_limits.PerProfileConcurrency, _limits.PerProfileConcurrency));
        SemaphoreSlim provider = _providers.GetOrAdd(providerId, _ =>
            new SemaphoreSlim(_limits.PerProviderConcurrency, _limits.PerProviderConcurrency));
        TokenBucket bucket = _buckets.GetOrAdd(providerId, _ => new TokenBucket(_limits.RequestsPerMinute));
        TimeSpan delay = bucket.TakeOrDelay();
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        decimal reserved = Reserve(profileId, providerId, cost);
        bool globalHeld = false;
        bool profileHeld = false;
        try
        {
            await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
            globalHeld = true;
            await profile.WaitAsync(cancellationToken).ConfigureAwait(false);
            profileHeld = true;
            await provider.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProviderDispatchLease(
                this, profileId, providerId, cost, reserved, profile, provider);
        }
        catch
        {
            if (profileHeld) profile.Release();
            if (globalHeld) _global.Release();
            ReleaseReservation(profileId, providerId, cost, reserved, spent: null);
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private decimal Reserve(Guid profileId, string providerId, ProviderCostReservation cost)
    {
        if (cost.MaximumCost is not decimal maximum || cost.Currency is null) return 0;
        lock (_budgetGate)
        {
            if (!_budgets.TryGetValue((profileId, providerId, cost.BillingUnit, cost.Currency), out BudgetState? budget))
                return maximum;
            if (budget.Spent + budget.Reserved + maximum > budget.Limit)
                throw new ProviderDispatchRejectedException("provider.costBudget", "Provider cost budget is exhausted.");
            budget.Reserved += maximum;
            return maximum;
        }
    }

    private void ReleaseReservation(
        Guid profileId,
        string providerId,
        ProviderCostReservation cost,
        decimal reserved,
        decimal? spent)
    {
        if (reserved == 0 || cost.Currency is null) return;
        lock (_budgetGate)
        {
            if (!_budgets.TryGetValue((profileId, providerId, cost.BillingUnit, cost.Currency), out BudgetState? budget)) return;
            budget.Reserved -= reserved;
            if (spent is decimal actual) budget.Spent += Math.Min(actual, reserved);
        }
    }

    public sealed class ProviderDispatchLease : IAsyncDisposable
    {
        private readonly ProviderDispatchCoordinator _owner;
        private readonly Guid _profileId;
        private readonly string _providerId;
        private readonly ProviderCostReservation _cost;
        private readonly decimal _reserved;
        private readonly SemaphoreSlim _profile;
        private readonly SemaphoreSlim _provider;
        private decimal? _spent;
        private int _disposed;

        internal ProviderDispatchLease(
            ProviderDispatchCoordinator owner,
            Guid profileId,
            string providerId,
            ProviderCostReservation cost,
            decimal reserved,
            SemaphoreSlim profile,
            SemaphoreSlim provider)
        {
            _owner = owner;
            _profileId = profileId;
            _providerId = providerId;
            _cost = cost;
            _reserved = reserved;
            _profile = profile;
            _provider = provider;
        }

        public bool EstimateOnly => _cost.MaximumCost is null || _cost.Currency is null;

        public void Settle(decimal actualCost)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(actualCost);
            if (_reserved > 0 && actualCost > _reserved)
                throw new InvalidDataException("Provider actual cost exceeded its reservation.");
            _spent = actualCost;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            _owner.ReleaseReservation(_profileId, _providerId, _cost, _reserved, _spent);
            _provider.Release();
            _profile.Release();
            _owner._global.Release();
            return ValueTask.CompletedTask;
        }
    }
}
