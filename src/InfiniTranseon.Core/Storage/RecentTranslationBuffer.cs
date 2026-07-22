using System.Text;

namespace InfiniTranseon.Core.Storage;

public sealed record RecentTranslationEntry(
    Guid SourceEventId,
    string SourceText,
    IReadOnlyList<string> Translations,
    DateTimeOffset CapturedAtUtc);

public sealed class RecentTranslationBuffer : IDisposable
{
    private const int MaximumEvents = 200;
    private const long MaximumBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LinkedList<(RecentTranslationEntry Entry, long Bytes)>> _profiles = [];
    private bool _disposed;

    public void Add(Guid profileId, RecentTranslationEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(entry);
        long bytes = Encoding.UTF8.GetByteCount(entry.SourceText) +
            entry.Translations.Sum(translation => Encoding.UTF8.GetByteCount(translation)) + 128;
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out LinkedList<(RecentTranslationEntry, long)>? list))
            {
                list = [];
                _profiles.Add(profileId, list);
            }
            list.AddFirst((entry, bytes));
            long total = list.Sum(item => item.Item2);
            while (list.Count > MaximumEvents || total > MaximumBytes)
            {
                LinkedListNode<(RecentTranslationEntry, long)>? last = list.Last;
                if (last is null) break;
                total -= last.Value.Item2;
                list.RemoveLast();
            }
        }
    }

    public IReadOnlyList<RecentTranslationEntry> Snapshot(Guid profileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            return _profiles.TryGetValue(profileId, out LinkedList<(RecentTranslationEntry, long)>? list)
                ? list.Select(item => item.Item1).ToArray()
                : [];
        }
    }

    public void Clear(Guid profileId)
    {
        lock (_gate) _profiles.Remove(profileId);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _profiles.Clear();
            _disposed = true;
        }
    }
}
