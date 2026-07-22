using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Owns the protocol safety ceilings and the latest dynamic budget snapshot. Ceiling values are
/// sourced from the backend-owned <see cref="RuntimeCapabilities"/> contract; view models read
/// them here and never invent their own limits. Reconnect revision increases on every accepted
/// reconnect so the UI can revalidate over-limit selections without discarding user choices.
/// </summary>
public sealed class RuntimeCapabilitiesService : IRuntimeCapabilitiesService
{
    private readonly object _gate = new();
    private RuntimeCapabilities _capabilities;
    private RuntimeBudgetSnapshot? _latestBudget;
    private long _reconnectRevision;

    public RuntimeCapabilitiesService()
        : this(RuntimeCapabilities.VersionOne)
    {
    }

    public RuntimeCapabilitiesService(RuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
    }

    public event EventHandler? Changed;

    public RuntimeCapabilities Capabilities
    {
        get { lock (_gate) return _capabilities; }
    }

    public RuntimeBudgetSnapshot? LatestBudget
    {
        get { lock (_gate) return _latestBudget; }
    }

    public long ReconnectRevision
    {
        get { lock (_gate) return _reconnectRevision; }
    }

    public void UpdateBudget(RuntimeBudgetSnapshot budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        lock (_gate)
        {
            if (_latestBudget is { } current &&
                (budget.RuntimeEpoch != current.RuntimeEpoch ||
                 budget.SnapshotRevision > current.SnapshotRevision))
            {
                _latestBudget = budget;
            }
            else if (_latestBudget is null)
            {
                _latestBudget = budget;
            }
            else
            {
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyReconnect(RuntimeReconnectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _capabilities = snapshot.Capabilities;
            _latestBudget = snapshot.Budget;
            _reconnectRevision++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
