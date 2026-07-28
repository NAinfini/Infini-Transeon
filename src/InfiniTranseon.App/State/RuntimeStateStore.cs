using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.State;

public enum RuntimeStateAdmission
{
    Accepted,
    RejectedStaleSourceGeneration,
    RejectedStaleChannel,
    RejectedStaleStage,
}

/// <summary>
/// Single runtime state store for the control UI. It enforces the
/// SourceGenerationToken → ChannelExecutionToken → StageExecutionToken hierarchy defined by the
/// runtime architecture spec: once a newer source generation appears, older OCR/translation/stream
/// updates are rejected; after a translator-group switch (a new channel run) or a superseded stage,
/// late results from the old run/stage are rejected. Kept free of WinUI types so it is unit-testable.
/// </summary>
public sealed class RuntimeStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<TrackKey, TrackState> _tracks = [];
    private Guid _currentEpoch;

    public Guid CurrentEpoch
    {
        get { lock (_gate) return _currentEpoch; }
    }

    public int ActiveTrackCount
    {
        get { lock (_gate) return _tracks.Count; }
    }

    /// <summary>
    /// Adopts a new runtime epoch (EngineHost restart / reconnect). All prior track state belongs to
    /// the old runtime and is dropped so late old-epoch events are rejected as stale.
    /// </summary>
    public void AdvanceEpoch(Guid runtimeEpoch)
    {
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        }

        lock (_gate)
        {
            if (_currentEpoch == runtimeEpoch)
            {
                return;
            }

            _currentEpoch = runtimeEpoch;
            _tracks.Clear();
        }
    }

    /// <summary>
    /// Admits a source generation event. Accepted only when it belongs to the current epoch and is
    /// strictly newer than the track's current generation; accepting it makes it current and clears
    /// any channel/stage state carried by the superseded generation.
    /// </summary>
    public RuntimeStateAdmission AdmitSourceGeneration(SourceGenerationToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            if (_currentEpoch == Guid.Empty)
            {
                _currentEpoch = token.RuntimeEpoch;
            }
            else if (token.RuntimeEpoch != _currentEpoch)
            {
                return RuntimeStateAdmission.RejectedStaleSourceGeneration;
            }

            TrackKey key = TrackKey.From(token);
            if (_tracks.TryGetValue(key, out TrackState? state) &&
                !IsNewerGeneration(token, state))
            {
                return RuntimeStateAdmission.RejectedStaleSourceGeneration;
            }

            _tracks[key] = new TrackState(token.SourceGeneration, token.ProfileRevision);
            return RuntimeStateAdmission.Accepted;
        }
    }

    public RuntimeStateAdmission AdmitTranslation(TranslationStreamSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return AdmitTranslation(snapshot.ExecutionToken);
    }

    public RuntimeStateAdmission AdmitTranslation(TranslationOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return AdmitTranslation(output.ExecutionToken);
    }

    /// <summary>
    /// Registers the current channel run for its display slot in the current generation (used on a
    /// translator-group switch or channel restart). Subsequent stage updates from any other run for
    /// that slot are then rejected as stale.
    /// </summary>
    public RuntimeStateAdmission AdmitChannelRun(ChannelExecutionToken channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            if (!TryGetCurrentTrack(channel.Source, out TrackState? state))
            {
                return RuntimeStateAdmission.RejectedStaleSourceGeneration;
            }

            state.Slots[channel.ImmutableSlotId] = new SlotState(channel.ChannelRunId);
            return RuntimeStateAdmission.Accepted;
        }
    }

    /// <summary>
    /// Admits a translation/stream stage update. Rejected when its source generation is not current,
    /// when its channel run is not the current run for the display slot, or when its stage/attempt/
    /// stream position is not ahead of what has already been observed for that slot.
    /// </summary>
    public RuntimeStateAdmission AdmitTranslation(StageExecutionToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            ChannelExecutionToken channel = token.Channel;
            if (!TryGetCurrentTrack(channel.Source, out TrackState? state))
            {
                return RuntimeStateAdmission.RejectedStaleSourceGeneration;
            }

            if (!state.Slots.TryGetValue(channel.ImmutableSlotId, out SlotState? slot))
            {
                // First result for the slot in this generation establishes the current run.
                slot = new SlotState(channel.ChannelRunId);
                state.Slots[channel.ImmutableSlotId] = slot;
            }
            else if (slot.ChannelRunId != channel.ChannelRunId)
            {
                return RuntimeStateAdmission.RejectedStaleChannel;
            }

            if (!slot.TryAdvanceStage(token))
            {
                return RuntimeStateAdmission.RejectedStaleStage;
            }

            return RuntimeStateAdmission.Accepted;
        }
    }

    private bool TryGetCurrentTrack(SourceGenerationToken source, out TrackState state)
    {
        if (_currentEpoch != Guid.Empty && source.RuntimeEpoch != _currentEpoch)
        {
            state = null!;
            return false;
        }

        if (_tracks.TryGetValue(TrackKey.From(source), out TrackState? existing) &&
            existing.SourceGeneration == source.SourceGeneration &&
            existing.ProfileRevision == source.ProfileRevision)
        {
            state = existing;
            return true;
        }

        state = null!;
        return false;
    }

    private static bool IsNewerGeneration(SourceGenerationToken token, TrackState state)
    {
        if (token.ProfileRevision != state.ProfileRevision)
        {
            return token.ProfileRevision > state.ProfileRevision;
        }

        return token.SourceGeneration > state.SourceGeneration;
    }

    private readonly record struct TrackKey(
        Guid RuntimeEpoch,
        TargetInstanceId TargetInstanceId,
        CaptureAreaKey Area,
        TextTrackId TextTrackId)
    {
        public static TrackKey From(SourceGenerationToken token) => new(
            token.RuntimeEpoch,
            token.TargetInstanceId,
            token.Area,
            token.TextTrackId);
    }

    private sealed class TrackState(long sourceGeneration, long profileRevision)
    {
        public long SourceGeneration { get; } = sourceGeneration;

        public long ProfileRevision { get; } = profileRevision;

        public Dictionary<Guid, SlotState> Slots { get; } = [];
    }

    private sealed class SlotState(Guid channelRunId)
    {
        private bool _hasStage;
        private int _stageSequence;
        private int _attempt;
        private long _streamSequence;

        public Guid ChannelRunId { get; } = channelRunId;

        // A stage update is admitted only when it is strictly ahead: a later stage in the pipeline,
        // a newer attempt of the current stage, or a higher stream revision of the current attempt.
        public bool TryAdvanceStage(StageExecutionToken token)
        {
            if (!_hasStage)
            {
                Set(token);
                return true;
            }

            if (token.StageSequence != _stageSequence)
            {
                if (token.StageSequence < _stageSequence)
                {
                    return false;
                }

                Set(token);
                return true;
            }

            if (token.Attempt != _attempt)
            {
                if (token.Attempt < _attempt)
                {
                    return false;
                }

                Set(token);
                return true;
            }

            if (token.StreamSequence <= _streamSequence)
            {
                return false;
            }

            _streamSequence = token.StreamSequence;
            return true;
        }

        private void Set(StageExecutionToken token)
        {
            _hasStage = true;
            _stageSequence = token.StageSequence;
            _attempt = token.Attempt;
            _streamSequence = token.StreamSequence;
        }
    }
}
