using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Settings;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Storage;

namespace InfiniTranseon.Core.Tests.Storage;

public sealed class ProfileStorageTests
{
    [Fact]
    public async Task MigratorCreatesJournalAndRepositoryAtomicallyRoundTripsUtf8Profile()
    {
        using TempDatabase database = new();
        DatabaseMigrationResult migration = new DatabaseMigrator().EnsureMigrated(database.Path);
        var repository = new ProfileRepository(database.Path);
        ProfileDocument profile = ProfileDocument.Create("ゲーム", "ja", "zh-Hans");

        await repository.SaveAsync(profile, TestContext.Current.CancellationToken);
        ProfileDocument? loaded = await repository.LoadAsync(
            profile.ProfileId,
            TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseMigrator.CurrentSchemaVersion, migration.SchemaVersion);
        Assert.Contains(
            migration.JournalMode,
            new[] { DatabaseJournalMode.Wal, DatabaseJournalMode.Delete });
        Assert.NotNull(loaded);
        Assert.Equal("ゲーム", loaded.Name);
        Assert.Equal("zh-Hans", loaded.TargetLanguage);
    }

    [Fact]
    public async Task ApplicationLanguageAndFormattingRegionAreGlobalSettings()
    {
        using TempDatabase database = new();
        var repository = new ApplicationSettingsRepository(database.Path);
        var settings = new ApplicationSettings
        {
            UiLanguage = "zh-CN",
            FormattingRegionMode = FormattingRegionMode.Explicit,
            FormattingRegion = "en-US",
            Theme = ThemePreference.Dark,
            StrictOffline = true,
            HistoryRetention = HistoryRetentionPolicy.Days90,
            Hotkeys =
            [
                new HotkeySetting("toggleOverlay", "Ctrl + Alt + T", true, "allRunningTargets"),
            ],
            ProviderEndpoints = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ocr.azure-ai-vision"] =
                    "https://example-vision-resource.cognitiveservices.azure.com/",
            },
            ReducedMotion = true,
            CloseToTray = false,
            CloseToTrayConfirmed = true,
            PinnedProfileIds = [Guid.Parse("15c2f43e-170e-4b34-af4c-0dd0c03b6a6e")],
            Performance = new PerformanceRuntimeSettings
            {
                Preset = PerformancePreset.Eco,
                SampleInterval = TimeSpan.FromSeconds(2),
            },
        };

        await repository.SaveAsync(settings, TestContext.Current.CancellationToken);
        ApplicationSettings loaded = await repository.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("zh-CN", loaded.UiLanguage);
        Assert.Equal(FormattingRegionMode.Explicit, loaded.FormattingRegionMode);
        Assert.Equal("en-US", loaded.FormattingRegion);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.True(loaded.StrictOffline);
        Assert.Equal(HistoryRetentionPolicy.Days90, loaded.HistoryRetention);
        HotkeySetting hotkey = Assert.Single(loaded.Hotkeys!);
        Assert.Equal("toggleOverlay", hotkey.Action);
        Assert.Equal("Ctrl + Alt + T", hotkey.Gesture);
        Assert.Equal(
            "https://example-vision-resource.cognitiveservices.azure.com/",
            loaded.ProviderEndpoints["ocr.azure-ai-vision"]);
        Assert.True(loaded.ReducedMotion);
        Assert.False(loaded.CloseToTray);
        Assert.True(loaded.CloseToTrayConfirmed);
        Assert.Equal(
            Guid.Parse("15c2f43e-170e-4b34-af4c-0dd0c03b6a6e"),
            Assert.Single(loaded.PinnedProfileIds));
        Assert.Equal(PerformancePreset.Eco, loaded.Performance.Preset);
        Assert.Equal(TimeSpan.FromSeconds(2), loaded.Performance.SampleInterval);
        Assert.Equal(DatabaseMigrator.CurrentSchemaVersion, repository.MigrationResult.SchemaVersion);
    }

    [Fact]
    public async Task ReplacingProfileUsesOneTransactionAndPreservesIdentity()
    {
        using TempDatabase database = new();
        var repository = new ProfileRepository(database.Path);
        ProfileDocument original = ProfileDocument.Create("Original", "ja", "en");
        ProfileDocument updated = original with { Name = "Updated" };

        await repository.SaveAsync(original, TestContext.Current.CancellationToken);
        await repository.SaveAsync(updated, TestContext.Current.CancellationToken);
        IReadOnlyList<ProfileDocument> profiles = await repository.ListAsync(
            TestContext.Current.CancellationToken);

        ProfileDocument stored = Assert.Single(profiles);
        Assert.Equal(original.ProfileId, stored.ProfileId);
        Assert.Equal("Updated", stored.Name);
    }

    [Fact]
    public async Task FailedPartialMigrationRestoresExistingDatabaseBackup()
    {
        using TempDatabase database = new();
        var repository = new ProfileRepository(database.Path);
        ProfileDocument profile = ProfileDocument.Create("Keep me", "ja", "en");
        await repository.SaveAsync(profile, TestContext.Current.CancellationToken);
        var migrator = new DatabaseMigrator(
        [
            new DatabaseMigration(DatabaseMigrator.CurrentSchemaVersion + 1,
            [
                "CREATE TABLE partial_write(value TEXT) STRICT;",
                "THIS IS NOT VALID SQL;",
            ]),
        ]);

        DatabaseMigrationException error = Assert.Throws<DatabaseMigrationException>(
            () => migrator.EnsureMigrated(database.Path));
        ProfileDocument? restored = await new ProfileRepository(database.Path).LoadAsync(
            profile.ProfileId,
            TestContext.Current.CancellationToken);

        Assert.True(error.Recovery.BackupCreated);
        Assert.True(error.Recovery.BackupRestored);
        Assert.NotNull(restored);
        Assert.Equal("Keep me", restored.Name);
    }

    [Fact]
    public async Task FutureProfileCannotBePersisted()
    {
        using TempDatabase database = new();
        var repository = new ProfileRepository(database.Path);
        ProfileDocument future = ProfileDocument.Create("Future", "ja", "en") with
        {
            SchemaVersion = ProfileDocument.CurrentVersion + 1,
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.SaveAsync(future, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void InvalidFormattingCultureIsRejectedBeforeDatabaseWrite()
    {
        using TempDatabase database = new();
        var repository = new ApplicationSettingsRepository(database.Path);
        var settings = new ApplicationSettings
        {
            UiLanguage = "en-US",
            FormattingRegionMode = FormattingRegionMode.Explicit,
            FormattingRegion = "not a culture!",
        };

        Assert.Throws<InvalidDataException>(
            () => repository.SaveAsync(settings, TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult());
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "InfiniTranseon.Tests",
            Guid.NewGuid().ToString("N"));

        public TempDatabase()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "profiles.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
