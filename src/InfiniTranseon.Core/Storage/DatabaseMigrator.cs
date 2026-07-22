using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

[assembly: InternalsVisibleTo("InfiniTranseon.Core.Tests")]

namespace InfiniTranseon.Core.Storage;

public enum DatabaseJournalMode
{
    Wal,
    Delete,
}

public sealed record DatabaseMigrationResult(
    int SchemaVersion,
    DatabaseJournalMode JournalMode,
    string? JournalModeReason,
    DatabaseRecoveryResult Recovery);

public sealed record DatabaseRecoveryResult(
    bool BackupCreated,
    bool BackupRestored,
    string? BackupPath);

internal sealed record DatabaseMigration(int Version, IReadOnlyList<string> Statements);

public sealed class DatabaseMigrationException : Exception
{
    public DatabaseMigrationException(
        string message,
        Exception innerException,
        DatabaseRecoveryResult recovery)
        : base(message, innerException)
    {
        Recovery = recovery;
    }

    public DatabaseRecoveryResult Recovery { get; }
}

public sealed class DatabaseMigrator
{
    public const int CurrentSchemaVersion = 3;

    private static readonly DatabaseMigration VersionOne = new(1,
    [
        """
        CREATE TABLE schema_journal (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        ) STRICT;
        """,
        """
        CREATE TABLE profiles (
            profile_id TEXT NOT NULL PRIMARY KEY,
            schema_version INTEGER NOT NULL,
            name TEXT NOT NULL,
            document BLOB NOT NULL,
            updated_at_utc TEXT NOT NULL
        ) STRICT;
        """,
        """
        CREATE TABLE application_settings (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            schema_version INTEGER NOT NULL,
            document BLOB NOT NULL,
            updated_at_utc TEXT NOT NULL
        ) STRICT;
        """,
    ]);

    private static readonly DatabaseMigration VersionTwo = new(2,
    [
        """
        CREATE TABLE translation_memory (
            profile_id TEXT NOT NULL,
            cache_key BLOB NOT NULL,
            source_text BLOB NOT NULL,
            translated_text BLOB NOT NULL,
            provider_id TEXT NOT NULL,
            model_id TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            last_used_at_utc TEXT NOT NULL,
            byte_size INTEGER NOT NULL CHECK (byte_size >= 0),
            PRIMARY KEY (profile_id, cache_key)
        ) STRICT;
        """,
        "CREATE INDEX ix_translation_memory_lru ON translation_memory(profile_id, last_used_at_utc ASC);",
        """
        CREATE TABLE translation_corrections (
            correction_id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NOT NULL,
            region_id TEXT NULL,
            source_language TEXT NOT NULL,
            target_language TEXT NOT NULL,
            glossary_version TEXT NOT NULL,
            source_text BLOB NOT NULL,
            corrected_text BLOB NOT NULL,
            authored_at_utc TEXT NOT NULL,
            undone_at_utc TEXT NULL
        ) STRICT;
        """,
    ]);

    private static readonly DatabaseMigration VersionThree = new(3,
    [
        """
        CREATE TABLE translation_history (
            profile_id TEXT NOT NULL,
            source_event_id TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            source_text BLOB NOT NULL,
            results_document BLOB NOT NULL,
            byte_size INTEGER NOT NULL CHECK (byte_size >= 0),
            PRIMARY KEY (profile_id, source_event_id)
        ) STRICT;
        """,
        """
        CREATE INDEX ix_translation_history_page
        ON translation_history(profile_id, captured_at_utc DESC, source_event_id DESC);
        """,
    ]);

    private readonly IReadOnlyList<DatabaseMigration> _migrations;

    public DatabaseMigrator() : this([]) { }

    internal DatabaseMigrator(IReadOnlyList<DatabaseMigration> additionalMigrations)
    {
        var migrations = new List<DatabaseMigration> { VersionOne, VersionTwo, VersionThree };
        ArgumentNullException.ThrowIfNull(additionalMigrations);
        migrations.AddRange(additionalMigrations);
        migrations.Sort((left, right) => left.Version.CompareTo(right.Version));
        for (int index = 0; index < migrations.Count; index++)
        {
            DatabaseMigration migration = migrations[index];
            int expectedVersion = index + 1;
            if (migration.Version != expectedVersion || migration.Statements.Count == 0 ||
                migration.Statements.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    "Database migrations must be non-empty, unique, and contiguous from version 1.",
                    nameof(additionalMigrations));
            }
        }
        _migrations = migrations.AsReadOnly();
    }

    public DatabaseMigrationResult EnsureMigrated(string databasePath)
    {
        string path = DatabasePath.Normalize(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bool existed = File.Exists(path) && new FileInfo(path).Length > 0;
        string backupPath = path + ".migration-backup";
        var recovery = new DatabaseRecoveryResult(false, false, null);

        try
        {
            using SqliteConnection connection = DatabaseConnection.Open(path);
            (DatabaseJournalMode mode, string? reason) = ConfigureJournal(connection, path);
            int version = ReadSchemaVersion(connection);
            int targetVersion = _migrations[^1].Version;
            if (version > targetVersion)
            {
                throw new InvalidDataException(
                    $"Database schema version {version} is newer than supported version {targetVersion}.");
            }
            if (version == targetVersion)
            {
                return new DatabaseMigrationResult(version, mode, reason, recovery);
            }

            if (existed)
            {
                ExecuteScalarText(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                CreateBackup(connection, backupPath);
                recovery = new DatabaseRecoveryResult(true, false, backupPath);
            }

            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            foreach (DatabaseMigration migration in _migrations.Where(item => item.Version > version))
            {
                foreach (string statement in migration.Statements)
                {
                    Execute(connection, transaction, statement);
                }
                using SqliteCommand journal = connection.CreateCommand();
                journal.Transaction = transaction;
                journal.CommandText =
                    "INSERT INTO schema_journal(version, applied_at_utc) VALUES ($version, $now);";
                journal.Parameters.AddWithValue("$version", migration.Version);
                journal.Parameters.AddWithValue(
                    "$now",
                    DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                journal.ExecuteNonQuery();
                version = migration.Version;
            }

            transaction.Commit();
            return new DatabaseMigrationResult(version, mode, reason, recovery);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Exception cause = error;
            if (recovery.BackupCreated)
            {
                try
                {
                    RestoreBackup(backupPath, path);
                    recovery = recovery with { BackupRestored = true };
                }
                catch (Exception restoreError) when (
                    restoreError is IOException or UnauthorizedAccessException or SqliteException)
                {
                    cause = new AggregateException("Migration and backup restoration both failed.", error, restoreError);
                }
            }
            throw new DatabaseMigrationException($"Database migration failed for '{path}'.", cause, recovery);
        }
    }

    private static void CreateBackup(SqliteConnection source, string backupPath)
    {
        DeleteSqliteFiles(backupPath);
        using SqliteConnection backup = DatabaseConnection.Open(backupPath);
        source.BackupDatabase(backup);
    }

    private static void RestoreBackup(string backupPath, string databasePath)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Database migration backup is missing.", backupPath);
        }
        DeleteSqliteFiles(databasePath);
        File.Copy(backupPath, databasePath, overwrite: false);
    }

    private static void DeleteSqliteFiles(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
    }

    private static (DatabaseJournalMode Mode, string? Reason) ConfigureJournal(
        SqliteConnection connection,
        string path)
    {
        DriveType driveType = GetDriveType(path);
        if (driveType is DriveType.Network or DriveType.Removable)
        {
            string actual = ExecuteScalarText(connection, "PRAGMA journal_mode=DELETE;");
            if (!actual.Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite refused DELETE journal mode and returned '{actual}'.");
            }
            return (DatabaseJournalMode.Delete, $"WAL is disabled for {driveType} storage.");
        }

        string journalMode = ExecuteScalarText(connection, "PRAGMA journal_mode=WAL;");
        if (journalMode.Equals("wal", StringComparison.OrdinalIgnoreCase))
        {
            return (DatabaseJournalMode.Wal, null);
        }

        string fallback = ExecuteScalarText(connection, "PRAGMA journal_mode=DELETE;");
        if (!fallback.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SQLite refused WAL ('{journalMode}') and DELETE ('{fallback}') journal modes.");
        }
        return (DatabaseJournalMode.Delete, $"SQLite refused WAL and returned '{journalMode}'.");
    }

    private static DriveType GetDriveType(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidDataException("Database path has no filesystem root.");
        }
        return new DriveInfo(root).DriveType;
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_journal');";
        bool journalExists = Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (!journalExists) return 0;

        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_journal;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string ExecuteScalarText(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

internal static class DatabasePath
{
    public static string Normalize(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string path = Path.GetFullPath(databasePath);
        if (Directory.Exists(path))
        {
            throw new ArgumentException("Database path must identify a file.", nameof(databasePath));
        }
        return path;
    }
}

internal static class DatabaseConnection
{
    public static SqliteConnection Open(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA cache_size=-65536;";
        command.ExecuteNonQuery();
        return connection;
    }
}
