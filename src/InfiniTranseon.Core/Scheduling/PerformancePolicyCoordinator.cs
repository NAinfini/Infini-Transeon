using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Scheduling;

public interface ITranslationDegradationPolicy
{
    bool ShouldPauseOptionalRefinement(Guid regionId);
}

public sealed class PerformancePolicyCoordinator : ITranslationDegradationPolicy
{
    private readonly object _gate = new();
    private readonly PerformanceGovernor _governor;
    private readonly Func<PolicyRevision, CancellationToken, ValueTask> _send;
    private readonly RuntimePolicyAcknowledgementGate _acknowledgements = new();
    private readonly Dictionary<Guid, SortedDictionary<int, string>> _active = [];
    private PolicyRevision? _latest;
    private Guid? _profileId;

    public PerformancePolicyCoordinator(
        PerformanceGovernor governor,
        Func<PolicyRevision, CancellationToken, ValueTask> send)
    {
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(send);
        _governor = governor;
        _send = send;
    }

    public async ValueTask<DegradationEvent?> ObserveAndSendAsync(
        PerformanceSnapshot snapshot,
        IReadOnlyList<RegionPerformancePolicy> regions,
        Guid profileId,
        long profileRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile identity cannot be empty.", nameof(profileId));
        DegradationEvent? change;
        PolicyRevision revision;
        lock (_gate)
        {
            if (_profileId is Guid existingProfile && existingProfile != profileId)
                throw new InvalidOperationException(
                    "A performance policy coordinator is bound to one profile.");
            _profileId ??= profileId;
            change = _governor.Observe(snapshot, regions, now);
            if (change is null) return null;
            Apply(change, regions);
            var policies = _active
                .Where(item => item.Value.Count > 0)
                .ToDictionary(
                    item => new RegionId(item.Key),
                    item => string.Join(';', item.Value
                        .Where(action => action.Key !=
                            (int)DegradationAction.PauseOptionalRefinement)
                        .Select(action =>
                        $"{action.Key}:{action.Value}")));
            policies = policies
                .Where(item => item.Value.Length > 0)
                .ToDictionary(item => item.Key, item => item.Value);
            revision = new PolicyRevision(
                change.PolicyRevision, profileId, profileRevision, policies);
            Volatile.Write(ref _latest, revision);
        }
        _acknowledgements.RecordSent(revision.Revision);
        await _send(revision, cancellationToken).ConfigureAwait(false);
        return change;
    }

    public void Acknowledge(PolicyAcknowledgement acknowledgement) =>
        _acknowledgements.Accept(acknowledgement);

    public PolicyRevision? CreateReconnectSnapshot() => Volatile.Read(ref _latest);

    public bool ShouldPauseOptionalRefinement(Guid regionId)
    {
        if (regionId == Guid.Empty)
            throw new ArgumentException("Region identity cannot be empty.", nameof(regionId));
        lock (_gate)
        {
            return _active.TryGetValue(regionId, out SortedDictionary<int, string>? actions) &&
                actions.TryGetValue(
                    (int)DegradationAction.PauseOptionalRefinement, out string? state) &&
                state == "degraded";
        }
    }

    private void Apply(
        DegradationEvent change,
        IReadOnlyList<RegionPerformancePolicy> regions)
    {
        if (change.Kind == DegradationEventKind.PausedCapacity)
        {
            foreach (RegionPerformancePolicy region in regions)
                Actions(region.RegionId)[0] = "paused";
        }
        else if (change.CauseCode == "performance.capacityRecovered")
        {
            foreach (SortedDictionary<int, string> actions in _active.Values)
                actions.Remove(0);
        }

        foreach (RegionPolicyChange regionChange in change.Changes)
        {
            SortedDictionary<int, string> actions = Actions(regionChange.RegionId);
            int action = (int)regionChange.Action;
            if (regionChange.AfterValue == "configured") actions.Remove(action);
            else actions[action] = regionChange.AfterValue;
        }
    }

    private SortedDictionary<int, string> Actions(Guid regionId)
    {
        if (!_active.TryGetValue(regionId, out SortedDictionary<int, string>? actions))
        {
            actions = [];
            _active.Add(regionId, actions);
        }
        return actions;
    }
}
