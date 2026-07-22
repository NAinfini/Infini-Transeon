using System.Text;
using System.Text.RegularExpressions;
using InfiniTranseon.Core.Storage;
using Microsoft.Data.Sqlite;

namespace InfiniTranseon.Core.Translation;

public sealed record TranslationMemoryOptions(
    int MaximumEntries = 10_000,
    long MaximumMemoryBytes = 32 * 1024 * 1024,
    bool PersistentEnabled = false,
    long MaximumPersistentBytes = 500 * 1024 * 1024,
    bool FuzzyEnabled = false,
    double MinimumSimilarity = 0.92);

public sealed record TranslationMemoryHit(
    string Translation,
    bool Exact,
    double Similarity,
    string ProviderId,
    string ModelId,
    bool Persistent);

public sealed class TranslationMemory
{
    private const int MaximumFuzzyCandidates = 256;
    private readonly record struct MemoryKey(Guid ProfileId, TranslationCacheKey Translation);

    private sealed record Entry(
        Guid ProfileId,
        TranslationCacheKey Key,
        string Translation,
        long ByteSize,
        LinkedListNode<MemoryKey> Node);

    private static readonly Regex UnsafeFuzzy = new(
        "^(?:[A-Za-z_][A-Za-z0-9_.:-]*|[\\d\\s.,:+%/-]+)$",
        RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly Dictionary<MemoryKey, Entry> _entries = [];
    private readonly LinkedList<MemoryKey> _lru = [];
    private readonly TranslationMemoryOptions _options;
    private readonly string? _databasePath;
    private long _memoryBytes;

    public TranslationMemory(TranslationMemoryOptions options, string? databasePath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumMemoryBytes, 1024);
        if (options.MinimumSimilarity is < 0.8 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.PersistentEnabled)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                options.MaximumPersistentBytes, 1024);
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            _databasePath = DatabasePath.Normalize(databasePath);
            new DatabaseMigrator().EnsureMigrated(_databasePath);
        }
        _options = options;
    }

    public async ValueTask<TranslationMemoryHit?> FindAsync(
        Guid profileId,
        TranslationCacheKey key,
        CancellationToken cancellationToken,
        bool allowFuzzy = true,
        bool allowPersistent = true)
    {
        ValidateProfile(profileId);
        ArgumentNullException.ThrowIfNull(key);
        var memoryKey = new MemoryKey(profileId, key);
        lock (_gate)
        {
            if (_entries.TryGetValue(memoryKey, out Entry? entry))
            {
                Touch(entry);
                return new TranslationMemoryHit(
                    entry.Translation, true, 1, key.ProviderId, key.ModelId, false);
            }
            if (allowFuzzy && _options.FuzzyEnabled && IsFuzzyEligible(key.NormalizedSource))
            {
                TranslationMemoryHit? fuzzy = FindFuzzy(profileId, key);
                if (fuzzy is not null) return fuzzy;
            }
        }
        if (!allowPersistent || !_options.PersistentEnabled) return null;
        return await FindPersistentAsync(profileId, key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StoreAsync(
        Guid profileId,
        TranslationCacheKey key,
        string translation,
        CancellationToken cancellationToken,
        bool persist = true)
    {
        ValidateProfile(profileId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(translation);
        long byteSize = Encoding.UTF8.GetByteCount(key.NormalizedSource) +
            Encoding.UTF8.GetByteCount(translation) + 512;
        var memoryKey = new MemoryKey(profileId, key);
        lock (_gate)
        {
            if (_entries.Remove(memoryKey, out Entry? old))
            {
                _lru.Remove(old.Node);
                _memoryBytes -= old.ByteSize;
            }
            LinkedListNode<MemoryKey> node = _lru.AddFirst(memoryKey);
            _entries[memoryKey] = new Entry(profileId, key, translation, byteSize, node);
            _memoryBytes += byteSize;
            EvictMemory();
        }
        if (persist && _options.PersistentEnabled)
            await StorePersistentAsync(profileId, key, translation, byteSize, cancellationToken).ConfigureAwait(false);
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    private TranslationMemoryHit? FindFuzzy(Guid profileId, TranslationCacheKey requested)
    {
        Entry? best = null;
        double bestScore = _options.MinimumSimilarity;
        int scoredCandidates = 0;
        for (LinkedListNode<MemoryKey>? node = _lru.First;
             node is not null && scoredCandidates < MaximumFuzzyCandidates;
             node = node.Next)
        {
            Entry candidate = _entries[node.Value];
            if (candidate.ProfileId != profileId || !SameScope(candidate.Key, requested) ||
                !SameScript(candidate.Key.NormalizedSource, requested.NormalizedSource)) continue;
            int maximumLength = Math.Max(
                candidate.Key.NormalizedSource.Length,
                requested.NormalizedSource.Length);
            int maximumDistance = (int)Math.Floor((1d - bestScore) * maximumLength + 1e-12);
            scoredCandidates++;
            int distance = BoundedLevenshteinDistance(
                candidate.Key.NormalizedSource,
                requested.NormalizedSource,
                maximumDistance);
            if (distance < 0) continue;
            double score = 1d - (double)distance / maximumLength;
            if (score > bestScore || best is null && score >= bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        if (best is null) return null;
        Touch(best);
        return new TranslationMemoryHit(
            best.Translation, false, bestScore, best.Key.ProviderId, best.Key.ModelId, false);
    }

    private async ValueTask<TranslationMemoryHit?> FindPersistentAsync(
        Guid profileId,
        TranslationCacheKey key,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath!);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_text, translated_text, provider_id, model_id
            FROM translation_memory
            WHERE profile_id = $profile AND cache_key = $key;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.Add("$key", SqliteType.Blob).Value = key.ToDigest();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        string source = Encoding.UTF8.GetString((byte[])reader[0]);
        if (!source.Equals(key.NormalizedSource, StringComparison.Ordinal)) return null;
        string translation = Encoding.UTF8.GetString((byte[])reader[1]);
        string providerId = reader.GetString(2);
        string modelId = reader.GetString(3);
        await reader.DisposeAsync().ConfigureAwait(false);
        await using SqliteCommand touch = connection.CreateCommand();
        touch.CommandText = """
            UPDATE translation_memory SET last_used_at_utc = $now
            WHERE profile_id = $profile AND cache_key = $key;
            """;
        touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        touch.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        touch.Parameters.Add("$key", SqliteType.Blob).Value = key.ToDigest();
        await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new TranslationMemoryHit(
            translation,
            true,
            1,
            providerId,
            modelId,
            true);
    }

    private async ValueTask StorePersistentAsync(
        Guid profileId,
        TranslationCacheKey key,
        string translation,
        long byteSize,
        CancellationToken cancellationToken)
    {
        byte[] source = Encoding.UTF8.GetBytes(key.NormalizedSource);
        byte[] translated = Encoding.UTF8.GetBytes(translation);
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath!);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO translation_memory(
                profile_id, cache_key, source_text, translated_text, provider_id, model_id,
                created_at_utc, last_used_at_utc, byte_size)
            VALUES ($profile, $key, $source, $translation, $provider, $model, $now, $now, $bytes)
            ON CONFLICT(profile_id, cache_key) DO UPDATE SET
                source_text=excluded.source_text,
                translated_text=excluded.translated_text,
                provider_id=excluded.provider_id,
                model_id=excluded.model_id,
                last_used_at_utc=excluded.last_used_at_utc,
                byte_size=excluded.byte_size;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.Add("$key", SqliteType.Blob).Value = key.ToDigest();
        command.Parameters.Add("$source", SqliteType.Blob).Value = source;
        command.Parameters.Add("$translation", SqliteType.Blob).Value = translated;
        command.Parameters.AddWithValue("$provider", key.ProviderId);
        command.Parameters.AddWithValue("$model", key.ModelId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$bytes", byteSize);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand evict = connection.CreateCommand();
        evict.Transaction = transaction;
        evict.CommandText = """
            DELETE FROM translation_memory
            WHERE profile_id = $profile AND cache_key IN (
                SELECT cache_key
                FROM (
                    SELECT cache_key,
                        SUM(byte_size) OVER (
                            ORDER BY last_used_at_utc DESC, created_at_utc DESC, cache_key DESC
                            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                        ) AS newest_first_bytes
                    FROM translation_memory
                    WHERE profile_id = $profile
                )
                WHERE newest_first_bytes > $limit
            );
            """;
        evict.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        evict.Parameters.AddWithValue("$limit", _options.MaximumPersistentBytes);
        await evict.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EvictMemory()
    {
        while (_entries.Count > _options.MaximumEntries || _memoryBytes > _options.MaximumMemoryBytes)
        {
            LinkedListNode<MemoryKey>? node = _lru.Last;
            if (node is null) return;
            Entry entry = _entries[node.Value];
            _entries.Remove(node.Value);
            _lru.RemoveLast();
            _memoryBytes -= entry.ByteSize;
        }
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private static bool SameScope(TranslationCacheKey left, TranslationCacheKey right) =>
        left with { NormalizedSource = string.Empty } == right with { NormalizedSource = string.Empty };

    private static bool IsFuzzyEligible(string source) => source.Length >= 8 && source.Length <= 256 &&
        !UnsafeFuzzy.IsMatch(source);

    private static bool SameScript(string left, string right) => Script(left) == Script(right);

    private static int Script(string value)
    {
        int result = 0;
        foreach (char character in value)
        {
            if (character is >= '\u3400' and <= '\u9fff') result |= 1;
            else if (char.IsAsciiLetter(character)) result |= 2;
        }
        return result;
    }

    private static int BoundedLevenshteinDistance(
        string left,
        string right,
        int maximumDistance)
    {
        if (maximumDistance < 0 || left.Length > 256 || right.Length > 256 ||
            Math.Abs(left.Length - right.Length) > maximumDistance)
            return -1;
        int sentinel = maximumDistance + 1;
        Span<int> previous = stackalloc int[right.Length + 1];
        Span<int> current = stackalloc int[right.Length + 1];
        previous.Fill(sentinel);
        current.Fill(sentinel);
        for (int column = 0; column <= Math.Min(right.Length, maximumDistance); column++)
            previous[column] = column;
        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row <= maximumDistance ? row : sentinel;
            int firstColumn = Math.Max(1, row - maximumDistance);
            int lastColumn = Math.Min(right.Length, row + maximumDistance);
            if (firstColumn > 1) current[firstColumn - 1] = sentinel;
            if (lastColumn < right.Length) current[lastColumn + 1] = sentinel;
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }
            Span<int> swap = previous;
            previous = current;
            current = swap;
        }
        return previous[right.Length] <= maximumDistance ? previous[right.Length] : -1;
    }

    private static void ValidateProfile(Guid profileId)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
    }
}
