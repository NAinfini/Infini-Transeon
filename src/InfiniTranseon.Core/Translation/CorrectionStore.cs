using System.Text;
using InfiniTranseon.Core.Storage;
using Microsoft.Data.Sqlite;

namespace InfiniTranseon.Core.Translation;

public sealed record CorrectionScope(
    Guid ProfileId,
    Guid? RegionId,
    string SourceLanguage,
    string TargetLanguage,
    string GlossaryVersion);

public sealed record TranslationCorrection(
    Guid CorrectionId,
    CorrectionScope Scope,
    string Source,
    string Corrected,
    DateTimeOffset AuthoredAtUtc,
    DateTimeOffset? UndoneAtUtc);

public sealed class CorrectionStore
{
    private readonly string _databasePath;

    public CorrectionStore(string databasePath)
    {
        _databasePath = DatabasePath.Normalize(databasePath);
        new DatabaseMigrator().EnsureMigrated(_databasePath);
    }

    public async ValueTask<TranslationCorrection> AddAsync(
        CorrectionScope scope,
        string source,
        string corrected,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(corrected);
        var correction = new TranslationCorrection(
            Guid.NewGuid(), scope, source, corrected, DateTimeOffset.UtcNow, null);
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO translation_corrections(
                correction_id, profile_id, region_id, source_language, target_language,
                glossary_version, source_text, corrected_text, authored_at_utc, undone_at_utc)
            VALUES ($id, $profile, $region, $sourceLanguage, $targetLanguage,
                $glossary, $source, $corrected, $authored, NULL);
            """;
        BindScope(command, scope);
        command.Parameters.AddWithValue("$id", correction.CorrectionId.ToString("D"));
        command.Parameters.Add("$source", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(source);
        command.Parameters.Add("$corrected", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(corrected);
        command.Parameters.AddWithValue("$authored", correction.AuthoredAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return correction;
    }

    public async ValueTask<TranslationCorrection?> FindAsync(
        CorrectionScope scope,
        string source,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT correction_id, region_id, corrected_text, authored_at_utc
            FROM translation_corrections
            WHERE profile_id = $profile
              AND source_language = $sourceLanguage
              AND target_language = $targetLanguage
              AND glossary_version = $glossary
              AND source_text = $source
              AND undone_at_utc IS NULL
              AND (region_id = $region OR region_id IS NULL)
            ORDER BY CASE WHEN region_id = $region THEN 0 ELSE 1 END, authored_at_utc DESC
            LIMIT 1;
            """;
        BindScope(command, scope);
        command.Parameters.Add("$source", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(source);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        Guid? region = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
        return new TranslationCorrection(
            Guid.Parse(reader.GetString(0)),
            scope with { RegionId = region },
            source,
            Encoding.UTF8.GetString((byte[])reader[2]),
            DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            null);
    }

    public async ValueTask<bool> UndoAsync(Guid correctionId, CancellationToken cancellationToken)
    {
        if (correctionId == Guid.Empty)
            throw new ArgumentException("Correction ID cannot be empty.", nameof(correctionId));
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE translation_corrections SET undone_at_utc = $now
            WHERE correction_id = $id AND undone_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", correctionId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void BindScope(SqliteCommand command, CorrectionScope scope)
    {
        command.Parameters.AddWithValue("$profile", scope.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$region", (object?)scope.RegionId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceLanguage", scope.SourceLanguage);
        command.Parameters.AddWithValue("$targetLanguage", scope.TargetLanguage);
        command.Parameters.AddWithValue("$glossary", scope.GlossaryVersion);
    }

    private static void ValidateScope(CorrectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProfileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(scope));
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.SourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.TargetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.GlossaryVersion);
    }
}
