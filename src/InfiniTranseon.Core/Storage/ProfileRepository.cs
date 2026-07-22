using System.Text;
using InfiniTranseon.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace InfiniTranseon.Core.Storage;

public sealed class ProfileRepository
{
    private readonly string _databasePath;

    public ProfileRepository(string databasePath)
    {
        _databasePath = DatabasePath.Normalize(databasePath);
        MigrationResult = new DatabaseMigrator().EnsureMigrated(_databasePath);
    }

    public DatabaseMigrationResult MigrationResult { get; }

    public async Task SaveAsync(ProfileDocument profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ProfileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profile));
        if (profile.SchemaVersion != ProfileDocument.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Profile schema version {profile.SchemaVersion} cannot be saved by this application version.");
        }
        byte[] document = Encoding.UTF8.GetBytes(ProfileJson.Serialize(profile));
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO profiles(profile_id, schema_version, name, document, updated_at_utc)
            VALUES ($id, $version, $name, $document, $updated)
            ON CONFLICT(profile_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                name = excluded.name,
                document = excluded.document,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", profile.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$version", profile.SchemaVersion);
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.Add("$document", SqliteType.Blob).Value = document;
        command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfileDocument?> LoadAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM profiles WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] bytes ? Deserialize(bytes) : null;
    }

    public async Task<IReadOnlyList<ProfileDocument>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<ProfileDocument>();
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM profiles ORDER BY updated_at_utc DESC, profile_id ASC;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(Deserialize((byte[])reader[0]));
        }
        return profiles.AsReadOnly();
    }

    private static ProfileDocument Deserialize(byte[] bytes) =>
        new ProfileMigrator().Migrate(Encoding.UTF8.GetString(bytes));
}
