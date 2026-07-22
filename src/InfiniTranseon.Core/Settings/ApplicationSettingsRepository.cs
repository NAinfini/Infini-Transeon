using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniTranseon.Core.Storage;
using Microsoft.Data.Sqlite;

namespace InfiniTranseon.Core.Settings;

public sealed class ApplicationSettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _databasePath;

    public ApplicationSettingsRepository(string databasePath)
    {
        _databasePath = DatabasePath.Normalize(databasePath);
        MigrationResult = new DatabaseMigrator().EnsureMigrated(_databasePath);
    }

    public DatabaseMigrationResult MigrationResult { get; }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO application_settings(singleton_id, schema_version, document, updated_at_utc)
            VALUES (1, $version, $document, $updated)
            ON CONFLICT(singleton_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                document = excluded.document,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$version", settings.SchemaVersion);
        command.Parameters.Add("$document", SqliteType.Blob).Value = document;
        command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = DatabaseConnection.Open(_databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM application_settings WHERE singleton_id = 1;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not byte[] bytes) return new ApplicationSettings();
        ApplicationSettings settings = JsonSerializer.Deserialize<ApplicationSettings>(bytes, SerializerOptions) ??
            throw new InvalidDataException("Application settings document is empty.");
        settings.Validate();
        return settings;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 32,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
