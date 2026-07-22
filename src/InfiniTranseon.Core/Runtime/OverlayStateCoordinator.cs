using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public sealed class OverlayStateCoordinator
{
    private sealed class SlotState
    {
        public required TranslationChannelId ChannelId { get; init; }
        public required Guid SlotId { get; init; }
        public required int Order { get; init; }
        public required string Label { get; init; }
        public Guid? ChannelRunId { get; set; }
        public int StageIndex { get; set; }
        public int Attempt { get; set; }
        public long StreamSequence { get; set; }
        public OverlaySlotState State { get; set; } = OverlaySlotState.Waiting;
        public string Text { get; set; } = string.Empty;
        public DateTimeOffset? LastAppliedAtUtc { get; set; }
    }

    private sealed class RegionState
    {
        public required Guid RegionId { get; init; }
        public required SourceGenerationToken Source { get; init; }
        public required OverlayPixelRect Bounds { get; init; }
        public required OverlayRegionStyleSnapshot Style { get; init; }
        public required Dictionary<TranslationChannelId, SlotState> Slots { get; init; }
    }

    private readonly object _gate = new();
    private readonly Guid _runtimeEpoch;
    private readonly TargetInstanceId _target;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, RegionState> _regions = [];
    private readonly Dictionary<SourceGenerationToken, Guid> _sourceRegions = [];
    private long _revision;
    private long _lastAcknowledgedRevision;

    public OverlayStateCoordinator(
        Guid runtimeEpoch,
        TargetInstanceId target,
        TimeProvider? timeProvider = null)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        ArgumentNullException.ThrowIfNull(target);
        if (target.Value == Guid.Empty) throw new ArgumentException("Target identity cannot be empty.", nameof(target));
        _runtimeEpoch = runtimeEpoch;
        _target = target;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public long LastAcknowledgedRevision
    {
        get { lock (_gate) return _lastAcknowledgedRevision; }
    }

    public OverlayDesiredState BeginRegion(
        Guid regionId,
        SourceGenerationToken source,
        OverlayPixelRect bounds,
        OverlayRegionStyleSnapshot style,
        IReadOnlyList<TranslationChannelDefinition> channels)
    {
        if (regionId == Guid.Empty) throw new ArgumentException("Region identity cannot be empty.", nameof(regionId));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(channels);
        if (source.RuntimeEpoch != _runtimeEpoch || source.TargetInstanceId != _target)
            throw new ArgumentException("Source generation belongs to a different runtime target.", nameof(source));
        if (source.Area.Kind == CaptureAreaKind.UserRegion && source.Area.UserRegionId?.Value != regionId)
            throw new ArgumentException("Source generation belongs to a different profile region.", nameof(source));
        if (channels.Count is < 1 or > 4 ||
            channels.Select(item => item.Id).Distinct().Count() != channels.Count ||
            channels.Select(item => item.DisplaySlot.SlotId).Distinct().Count() != channels.Count ||
            channels.Select(item => item.DisplaySlot.Order).Distinct().Count() != channels.Count)
            throw new ArgumentException("Overlay channels must have unique channel, slot and order identities.", nameof(channels));

        var slots = channels.ToDictionary(
            item => item.Id,
            item => new SlotState
            {
                ChannelId = item.Id,
                SlotId = item.DisplaySlot.SlotId,
                Order = item.DisplaySlot.Order,
                Label = item.DisplaySlot.Label,
            });
        lock (_gate)
        {
            if (_regions.Remove(regionId, out RegionState? previous))
                _sourceRegions.Remove(previous.Source);
            var region = new RegionState
            {
                RegionId = regionId,
                Source = source,
                Bounds = bounds,
                Style = style,
                Slots = slots,
            };
            _regions.Add(regionId, region);
            _sourceRegions.Add(source, regionId);
            AdvanceRevision();
            return SnapshotUnsafe();
        }
    }

    public bool RemoveRegion(Guid regionId, out OverlayDesiredState? state)
    {
        lock (_gate)
        {
            if (!_regions.Remove(regionId, out RegionState? removed))
            {
                state = null;
                return false;
            }
            _sourceRegions.Remove(removed.Source);
            AdvanceRevision();
            state = SnapshotUnsafe();
            return true;
        }
    }

    public bool RemoveAreaRegionsExcept(
        CaptureAreaKey area,
        IReadOnlySet<Guid> retainedRegionIds,
        out IReadOnlyList<Guid> removedRegionIds,
        out OverlayDesiredState? state)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(retainedRegionIds);
        if (retainedRegionIds.Any(id => id == Guid.Empty))
            throw new ArgumentException(
                "Retained overlay region identities cannot be empty.",
                nameof(retainedRegionIds));
        lock (_gate)
        {
            Guid[] removed = _regions.Values
                .Where(region => region.Source.Area == area &&
                    !retainedRegionIds.Contains(region.RegionId))
                .Select(region => region.RegionId)
                .ToArray();
            if (removed.Length == 0)
            {
                removedRegionIds = Array.Empty<Guid>();
                state = null;
                return false;
            }
            foreach (Guid regionId in removed)
            {
                RegionState region = _regions[regionId];
                _regions.Remove(regionId);
                _sourceRegions.Remove(region.Source);
            }
            AdvanceRevision();
            removedRegionIds = Array.AsReadOnly(removed);
            state = SnapshotUnsafe();
            return true;
        }
    }

    public bool TryApply(TranslationOutput output, out OverlayDesiredState? state)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            SourceGenerationToken source = output.ExecutionToken.Channel.Source;
            if (!_sourceRegions.TryGetValue(source, out Guid regionId) ||
                !_regions.TryGetValue(regionId, out RegionState? region) ||
                region.Source != source ||
                !region.Slots.TryGetValue(output.ChannelId, out SlotState? slot) ||
                slot.SlotId != output.ImmutableSlotId ||
                output.ExecutionToken.Channel.ImmutableSlotId != slot.SlotId ||
                output.ExecutionToken.Channel.ChannelId != slot.ChannelId)
            {
                state = null;
                return false;
            }

            Guid channelRunId = output.ExecutionToken.Channel.ChannelRunId;
            if (slot.ChannelRunId is Guid currentRun && currentRun != channelRunId)
            {
                state = null;
                return false;
            }
            if (slot.ChannelRunId is null) slot.ChannelRunId = channelRunId;
            int ordering = Compare(output, slot);
            if (ordering <= 0)
            {
                state = null;
                return false;
            }
            slot.StageIndex = output.StageIndex;
            slot.Attempt = output.Attempt;
            slot.StreamSequence = output.ExecutionToken.StreamSequence;
            if (!string.IsNullOrEmpty(output.Text) || output.TerminalErrorCode is null)
                slot.Text = output.Text;
            slot.State = ResolveState(output);
            slot.LastAppliedAtUtc = _timeProvider.GetUtcNow();
            AdvanceRevision();
            state = SnapshotUnsafe();
            return true;
        }
    }

    public TimeSpan GetRefinementDelay(TranslationOutput output, TimeSpan minimumDwell)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (minimumDwell < TimeSpan.Zero || minimumDwell > TimeSpan.FromSeconds(3))
            throw new ArgumentOutOfRangeException(nameof(minimumDwell));
        if (minimumDwell == TimeSpan.Zero || output.Stage != TranslationStage.Refinement)
            return TimeSpan.Zero;
        lock (_gate)
        {
            SourceGenerationToken source = output.ExecutionToken.Channel.Source;
            if (!_sourceRegions.TryGetValue(source, out Guid regionId) ||
                !_regions.TryGetValue(regionId, out RegionState? region) ||
                region.Source != source ||
                !region.Slots.TryGetValue(output.ChannelId, out SlotState? slot) ||
                slot.SlotId != output.ImmutableSlotId ||
                output.ExecutionToken.Channel.ImmutableSlotId != slot.SlotId ||
                output.ExecutionToken.Channel.ChannelId != slot.ChannelId ||
                slot.ChannelRunId != output.ExecutionToken.Channel.ChannelRunId ||
                output.StageIndex <= slot.StageIndex ||
                slot.LastAppliedAtUtc is not DateTimeOffset presentedAt)
                return TimeSpan.Zero;
            TimeSpan elapsed = _timeProvider.GetUtcNow() - presentedAt;
            return elapsed >= minimumDwell ? TimeSpan.Zero : minimumDwell - elapsed;
        }
    }

    public bool TryAcknowledge(RuntimeOverlayAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        lock (_gate)
        {
            if (acknowledgement.TargetInstanceId != _target ||
                acknowledgement.OverlayRevision <= _lastAcknowledgedRevision ||
                acknowledgement.OverlayRevision > _revision)
                return false;
            _lastAcknowledgedRevision = acknowledgement.OverlayRevision;
            return true;
        }
    }

    public OverlayDesiredState Snapshot()
    {
        lock (_gate) return SnapshotUnsafe();
    }

    private void AdvanceRevision() => _revision = checked(_revision + 1);

    private OverlayDesiredState SnapshotUnsafe() => new(
        _runtimeEpoch,
        _target,
        _revision,
        _regions.Values
            .OrderBy(item => item.RegionId)
            .Select(region => new OverlayRegionSnapshot(
                region.RegionId,
                region.Bounds,
                region.Style,
                region.Slots.Values
                    .OrderBy(slot => slot.Order)
                    .Select(slot => new OverlaySlotSnapshot(
                        slot.SlotId, slot.Order, slot.State, slot.Text, slot.Label)
                    {
                        StageIndex = slot.StageIndex,
                    })
                    .ToArray()))
            .ToArray());

    private static int Compare(TranslationOutput output, SlotState slot)
    {
        int value = output.StageIndex.CompareTo(slot.StageIndex);
        if (value != 0) return value;
        value = output.Attempt.CompareTo(slot.Attempt);
        if (value != 0) return value;
        return output.ExecutionToken.StreamSequence.CompareTo(slot.StreamSequence);
    }

    private static OverlaySlotState ResolveState(TranslationOutput output)
    {
        if (output.TerminalErrorCode is not null)
        {
            if (output.SupersededReason == "cancelled") return OverlaySlotState.Cancelled;
            if (output.TerminalErrorCode.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return OverlaySlotState.Timeout;
            return OverlaySlotState.Failure;
        }
        if (!output.StreamCompleted) return OverlaySlotState.Streaming;
        return output.Stage == TranslationStage.Fallback || output.FallbackFromProviderId is not null
            ? OverlaySlotState.Fallback
            : OverlaySlotState.Success;
    }
}
