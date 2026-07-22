using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Storage;
using ApplicationSettingsRepository = InfiniTranseon.Core.Settings.ApplicationSettingsRepository;

namespace InfiniTranseon.App.Tests;

// Round-trip tests exercising the real Core-backed services against an isolated temp-directory
// database, so persistence is verified end to end without touching the user's real data root. Each
// test creates its own directory and deletes it afterward; the credential-store test uses a
// test-prefixed reference and always removes what it writes.
public sealed class RealServiceIntegrationTests
{
    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "InfiniTranseonTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProfileEditModel NewDraft(string name = "Elden Ring") =>
        new(Guid.Empty, name, "ja", "zh-Hans", Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "ELDEN RING", "Window", "3840x2160 - 144dpi", "DeepL",
            [new ProfileRegionDraft("Dialogue", RegionPriorityLevel.P0)]);

    [Fact]
    public async Task Profile_saves_and_survives_a_new_repository_instance()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var service = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            Guid id = await service.SaveAsync(NewDraft(), ct);
            Assert.NotEqual(Guid.Empty, id);

            // Simulate an app restart: a brand-new repository over the same database file.
            var reopened = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            IReadOnlyList<ProfileCard> cards = await reopened.GetProfilesAsync(ct);
            Assert.Contains(cards, card => card.ProfileId == id && card.Name == "Elden Ring");

            ProfileEditModel? loaded = await reopened.LoadForEditAsync(id, ct);
            Assert.NotNull(loaded);
            Assert.Equal("ja", loaded!.SourceLanguage);
            Assert.Equal("zh-Hans", loaded.TargetLanguage);
            Assert.Single(loaded.Regions);

            await reopened.DeleteAsync(id, ct);
            Assert.Empty(await reopened.GetProfilesAsync(ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Glossary_entry_persists_into_active_profile_and_survives_reopen()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var profiles = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            await profiles.SaveAsync(NewDraft(), ct);

            var glossary = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            var entry = new GlossaryEntry("term-src", "term-dst", "Profile", CaseSensitive: false, Protected: true, "item");
            await glossary.AddOrUpdateAsync(entry, replacingSourceTerm: null, ct);

            var reopened = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            GlossarySnapshot snapshot = await reopened.GetEntriesAsync(ct);
            Assert.True(snapshot.HasActiveProfile);
            Assert.Contains(snapshot.Entries, e => e.SourceTerm == "term-src" && e.Protected);

            await reopened.RemoveAsync("term-src", ct);
            Assert.DoesNotContain((await reopened.GetEntriesAsync(ct)).Entries, e => e.SourceTerm == "term-src");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Glossary_edit_without_a_profile_fails_loudly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var glossary = new RealGlossaryService(new ProfileRepository(options.DatabasePath));
            var entry = new GlossaryEntry("x", "y", "Profile", CaseSensitive: false, Protected: false, "");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => glossary.AddOrUpdateAsync(entry, replacingSourceTerm: null, ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_round_trip_across_new_store_instances()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            ISettingsService Make() => new RealSettingsService(
                new ApplicationSettingsRepository(options.DatabasePath),
                new UiPreferencesStore(options.UiPreferencesPath),
                new FakeSecretReferenceService());

            var settings = new ApplicationSettings(
                UiThemePreference.Dark, StrictOffline: true, HistoryRetention.Days90, "zh-CN");
            await Make().UpdateAsync(settings, ct);

            ApplicationSettings loaded = await Make().GetSettingsAsync(ct);
            Assert.Equal(UiThemePreference.Dark, loaded.Theme);
            Assert.True(loaded.StrictOffline);
            Assert.Equal(HistoryRetention.Days90, loaded.HistoryRetention);
            Assert.Equal("zh-CN", loaded.UiLanguage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_center_reports_empty_state_on_fresh_database()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var service = new RealProfileService(new ProfileRepository(options.DatabasePath), options.DatabasePath);
            var viewModel = new ProfileCenterViewModel(service);

            await viewModel.InitializeAsync(ct);

            Assert.False(viewModel.HasError);
            Assert.Empty(viewModel.Profiles);
            Assert.True(viewModel.IsEmpty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Glossary_reports_no_active_profile_on_fresh_database()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = NewTempRoot();
        var options = new AppDataOptions(root);
        try
        {
            var viewModel = new GlossaryViewModel(new RealGlossaryService(new ProfileRepository(options.DatabasePath)));

            await viewModel.InitializeAsync(ct);

            Assert.False(viewModel.HasError);
            Assert.False(viewModel.HasActiveProfile);
            Assert.True(viewModel.NoActiveProfile);
            Assert.Empty(viewModel.Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Setup_wizard_saves_a_profile_built_from_the_selected_target_and_regions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var spy = new SpyProfileService();
        var wizard = new SetupWizardViewModel(
            new FakeCaptureProbe(), new FakeSettingsService(), new FakeSecretReferenceService(), spy);
        await wizard.InitializeAsync(ct);

        wizard.ProfileName = "My profile";
        Assert.NotNull(wizard.SelectedTarget);
        wizard.AddRegion("HUD", RegionPriorityLevel.P1);
        Assert.True(wizard.CanSave);

        await wizard.SaveCommand.ExecuteAsync(null);

        Assert.False(wizard.HasError);
        Assert.NotEqual(Guid.Empty, wizard.SavedProfileId);
        Assert.NotNull(spy.LastSaved);
        Assert.Equal("My profile", spy.LastSaved!.Name);
        Assert.Equal(wizard.SelectedTarget!.DisplayName, spy.LastSaved.TargetName);
        Assert.Contains(spy.LastSaved.Regions, region => region.Name == "HUD");
    }

    [Fact]
    public async Task Secret_reference_service_round_trips_through_credential_store_with_test_prefix()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A test-only catalog whose credential reference is prefixed so it can never collide with a
        // user's real provider credential in Windows Credential Manager.
        string reference = "test-wp5-" + Guid.NewGuid().ToString("N");
        var binding = new CredentialBinding(
            reference, "translation", "https", "api.example.test", 443, "bearer", ProxyPolicy.System);
        var catalog = new List<CatalogProvider>
        {
            new(reference, "Test Provider", "NMT - cloud", reference, binding, "test only"),
        };
        var service = new RealSecretReferenceService(new GenericCredentialStore(), catalog);

        try
        {
            Assert.False(await service.HasSecretAsync(reference, ct));

            await service.SetSecretAsync(reference, "s3cr3t-value", ct);
            Assert.True(await service.HasSecretAsync(reference, ct));

            IReadOnlyList<SecretReference> references = await service.GetReferencesAsync(ct);
            Assert.Contains(references, item => item.ReferenceId == reference && item.IsPresent);
        }
        finally
        {
            // Always remove the credential this test wrote, even if an assertion above failed.
            await service.ClearSecretAsync(reference, ct);
        }

        Assert.False(await service.HasSecretAsync(reference, ct));
    }

    private sealed class SpyProfileService : IProfileService
    {
        public ProfileEditModel? LastSaved { get; private set; }

        public Task<IReadOnlyList<ProfileCard>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProfileCard>>([]);

        public Task<ProfileEditModel?> LoadForEditAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProfileEditModel?>(null);

        public Task<Guid> SaveAsync(ProfileEditModel profile, CancellationToken cancellationToken = default)
        {
            LastSaved = profile;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
