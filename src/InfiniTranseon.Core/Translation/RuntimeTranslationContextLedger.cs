using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Translation;

/// <summary>
/// Keeps a bounded, in-memory context per running profile. It never persists OCR text and is
/// cleared when the final target for that profile stops.
/// </summary>
public sealed class RuntimeTranslationContextLedger
{
    private const int MaximumItems = 8;
    private const int MaximumCharactersPerItem = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, State> _states = [];

    public void ObserveRole(Guid profileId, ProfileRegionContextRole role, string text)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(text);
        string bounded = Bound(text);
        if (bounded.Length == 0) return;
        lock (_gate)
        {
            State state = GetOrAdd(profileId);
            if (role == ProfileRegionContextRole.Speaker) state.Speaker = bounded;
            if (role == ProfileRegionContextRole.Scene) state.Scene = bounded;
        }
    }

    public TranslationRunOptions Apply(
        TranslationRunOptions options,
        int recentLineCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        int count = Math.Clamp(recentLineCount, 0, MaximumItems);
        lock (_gate)
        {
            if (!_states.TryGetValue(options.ProfileId, out State? state))
                return options;
            return options with
            {
                Context = options.Context with
                {
                    Scene = state.Scene ?? options.Context.Scene,
                    Speaker = state.Speaker ?? options.Context.Speaker,
                    RecentSource = state.RecentSource.TakeLast(count).ToArray(),
                    RecentTranslation = state.RecentTranslation.TakeLast(count).ToArray(),
                },
            };
        }
    }

    public void Append(Guid profileId, string source, string? translation)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            State state = GetOrAdd(profileId);
            AppendBounded(state.RecentSource, source);
            if (!string.IsNullOrWhiteSpace(translation))
                AppendBounded(state.RecentTranslation, translation);
        }
    }

    public void Clear(Guid profileId)
    {
        lock (_gate) _states.Remove(profileId);
    }

    private State GetOrAdd(Guid profileId)
    {
        if (!_states.TryGetValue(profileId, out State? state))
        {
            state = new State();
            _states.Add(profileId, state);
        }
        return state;
    }

    private static void AppendBounded(List<string> values, string value)
    {
        string bounded = Bound(value);
        if (bounded.Length == 0) return;
        values.Add(bounded);
        while (values.Count > MaximumItems) values.RemoveAt(0);
    }

    private static string Bound(string value)
    {
        string trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, MaximumCharactersPerItem)];
    }

    private sealed class State
    {
        public string? Speaker { get; set; }
        public string? Scene { get; set; }
        public List<string> RecentSource { get; } = [];
        public List<string> RecentTranslation { get; } = [];
    }
}
