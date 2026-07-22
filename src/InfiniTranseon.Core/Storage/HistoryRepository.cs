using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InfiniTranseon.Core.Storage;

public sealed record HistoryOptions(
    bool Enabled = false,
    TimeSpan? Retention = null,
    long MaximumBytes = 500L * 1024 * 1024);

public sealed record HistoryTranslationResult(
    Guid ChannelId,
    string ProviderId,
    string Text,
    string Stage,
    double LatencyMilliseconds,
    decimal? EstimatedCost,
    string? Currency,
    bool CacheHit,
    string? ErrorCode);

public sealed record HistoryRecord(
    Guid ProfileId,
    Guid SourceEventId,
    DateTimeOffset CapturedAtUtc,
    string SourceText,
    IReadOnlyList<HistoryTranslationResult> Results);

public sealed record HistoryCursor(DateTimeOffset CapturedAtUtc, Guid SourceEventId);
public sealed record HistoryPage(IReadOnlyList<HistoryRecord> Items, HistoryCursor? NextCursor);

public sealed class HistoryRepository
{
    private readonly string? _databasePath;
    private readonly HistoryOptions _options;

    public HistoryRepository(string databasePath, HistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumBytes, 1024);
        if (options.Retention is TimeSpan retention &&
            (retention <= TimeSpan.Zero || retention > TimeSpan.FromDays(365_000)))
            throw new ArgumentOutOfRangeException(nameof(options), "History retention must be positive and bounded.");
        _options = options;
        if (!options.Enabled) return;
        _databasePath = DatabasePath.Normalize(databasePath);
        new DatabaseMigrator().EnsureMigrated(_databasePath);
    }

    public async ValueTask SaveAsync(HistoryRecord record, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        Validate(record);
        byte[] source = Encoding.UTF8.GetBytes(record.SourceText);
        byte[] results = JsonSerializer.SerializeToUtf8Bytes(record.Results);
        long bytes = source.Length + results.Length + 128;
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath!);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO translation_history(
                profile_id, source_event_id, captured_at_utc, source_text, results_document, byte_size)
            VALUES ($profile, $event, $captured, $source, $results, $bytes)
            ON CONFLICT(profile_id, source_event_id) DO UPDATE SET
                captured_at_utc=excluded.captured_at_utc,
                source_text=excluded.source_text,
                results_document=excluded.results_document,
                byte_size=excluded.byte_size;
            """;
        command.Parameters.AddWithValue("$profile", record.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$event", record.SourceEventId.ToString("D"));
        command.Parameters.AddWithValue("$captured", record.CapturedAtUtc.ToString("O"));
        command.Parameters.Add("$source", SqliteType.Blob).Value = source;
        command.Parameters.Add("$results", SqliteType.Blob).Value = results;
        command.Parameters.AddWithValue("$bytes", bytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CleanupAsync(connection, transaction, record.ProfileId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<HistoryPage> ReadPageAsync(
        Guid profileId,
        int pageSize,
        HistoryCursor? cursor,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return new HistoryPage([], null);
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 200);
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath!);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_event_id, captured_at_utc, source_text, results_document
            FROM translation_history
            WHERE profile_id = $profile AND (
                $hasCursor = 0 OR captured_at_utc < $captured OR
                (captured_at_utc = $captured AND source_event_id < $event))
            ORDER BY captured_at_utc DESC, source_event_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.AddWithValue("$hasCursor", cursor is null ? 0 : 1);
        command.Parameters.AddWithValue("$captured", cursor?.CapturedAtUtc.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("$event", cursor?.SourceEventId.ToString("D") ?? string.Empty);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var result = new List<HistoryRecord>(pageSize + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new HistoryRecord(
                profileId,
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                Encoding.UTF8.GetString((byte[])reader[2]),
                JsonSerializer.Deserialize<HistoryTranslationResult[]>((byte[])reader[3]) ?? []));
        }
        bool hasMore = result.Count > pageSize;
        if (hasMore) result.RemoveAt(result.Count - 1);
        HistoryRecord? last = result.LastOrDefault();
        return new HistoryPage(
            result,
            hasMore && last is not null ? new HistoryCursor(last.CapturedAtUtc, last.SourceEventId) : null);
    }

    public async ValueTask ExportJsonLinesAsync(
        Guid profileId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new InvalidOperationException("history.disabled");
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("History export path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            await WriteJsonLineAsync(stream, new
            {
                schemaVersion = 1,
                profileId,
                exportedAtUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
            HistoryCursor? cursor = null;
            do
            {
                HistoryPage page = await ReadPageAsync(
                    profileId, 200, cursor, cancellationToken).ConfigureAwait(false);
                foreach (HistoryRecord item in page.Items)
                    await WriteJsonLineAsync(stream, item, cancellationToken).ConfigureAwait(false);
                cursor = page.NextCursor;
            }
            while (cursor is not null);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Close();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async ValueTask<int> ClearProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return 0;
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath!);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM translation_history WHERE profile_id = $profile;";
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteJsonLineAsync<T>(
        Stream destination,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        await destination.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CleanupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM translation_history
            WHERE profile_id = $profile AND captured_at_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow
            .Subtract(_options.Retention ?? TimeSpan.FromDays(30)).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = """
            DELETE FROM translation_history
            WHERE profile_id = $profile AND source_event_id IN (
                SELECT source_event_id
                FROM (
                    SELECT source_event_id,
                        SUM(byte_size) OVER (
                            ORDER BY captured_at_utc DESC, source_event_id DESC
                            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                        ) AS newest_first_bytes
                    FROM translation_history
                    WHERE profile_id = $profile
                )
                WHERE newest_first_bytes > $limit
            );
            """;
        command.Parameters.AddWithValue("$limit", _options.MaximumBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ProfileId == Guid.Empty || record.SourceEventId == Guid.Empty)
            throw new ArgumentException("History identities cannot be empty.", nameof(record));
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SourceText);
        if (record.CapturedAtUtc.Offset != TimeSpan.Zero || record.SourceText.Length > 65_536 ||
            record.Results.Count > 20 || record.Results.Any(result =>
                result.ChannelId == Guid.Empty || string.IsNullOrWhiteSpace(result.ProviderId) ||
                result.ProviderId.Length > 128 || result.Text.Length > 65_536 ||
                string.IsNullOrWhiteSpace(result.Stage) || result.Stage.Length > 64 ||
                !double.IsFinite(result.LatencyMilliseconds) || result.LatencyMilliseconds < 0 ||
                result.EstimatedCost < 0 || result.Currency?.Length > 16 ||
                result.ErrorCode?.Length > 128))
            throw new ArgumentOutOfRangeException(nameof(record));
    }
}
