using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.App.Tests;

public sealed class FakeServiceContractTests
{
    [Fact]
    public async Task Profile_fake_returns_deterministic_seed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeProfileService service = new();

        IReadOnlyList<ProfileCard> first = await service.GetProfilesAsync(ct);
        IReadOnlyList<ProfileCard> second = await service.GetProfilesAsync(ct);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Profile_fake_save_and_delete_round_trip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeProfileService service = new();
        var draft = new ProfileEditModel(
            Guid.Empty, "New profile", "ja", "zh-Hans", Guid.NewGuid(), "Target", "Window",
            "1920x1080", "translation.deepl", [new ProfileRegionDraft("Dialogue", RegionPriorityLevel.P0)]);

        Guid id = await service.SaveAsync(draft, ct);
        Assert.NotEqual(Guid.Empty, id);
        Assert.Contains(await service.GetProfilesAsync(ct), card => card.ProfileId == id);

        ProfileEditModel? loaded = await service.LoadForEditAsync(id, ct);
        Assert.NotNull(loaded);
        Assert.Equal("New profile", loaded!.Name);

        await service.DeleteAsync(id, ct);
        Assert.DoesNotContain(await service.GetProfilesAsync(ct), card => card.ProfileId == id);
    }

    [Fact]
    public async Task Runtime_control_fake_walks_the_engine_state_machine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeRuntimeControlService service = new();
        List<EngineRuntimeStatus> observed = [];
        service.StatusChanged += (_, change) => observed.Add(change.Status);

        Assert.Equal(EngineRuntimeStatus.Stopped, service.Status);
        Assert.Empty(service.GetRunningTargets());
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(Guid.Empty, ct));

        await service.StartAsync(Guid.NewGuid(), ct);
        Assert.Equal(EngineRuntimeStatus.Running, service.Status);
        Assert.NotEmpty(service.GetRunningTargets());
        Assert.Equal(service.GetRunningTargets(), service.GetRunningTargets());
        Assert.Equal(
            [EngineRuntimeStatus.Locating, EngineRuntimeStatus.Starting, EngineRuntimeStatus.Running],
            observed);

        await service.SetPausedAsync(true, ct);
        Assert.True(service.IsPaused);
        await service.SetOverlayVisibleAsync(false, ct);
        Assert.False(service.IsOverlayVisible);

        await service.RequestManualOcrAsync(ct);

        await service.StopAsync(ct);
        Assert.Equal(EngineRuntimeStatus.Stopped, service.Status);
        Assert.Empty(service.GetRunningTargets());
    }

    [Fact]
    public async Task History_fake_returns_deterministic_seed_with_channels()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeHistoryService service = new();

        IReadOnlyList<HistoryEvent> events = await service.GetEventsAsync(ct);

        Assert.NotEmpty(events);
        Assert.Equal(events, await service.GetEventsAsync(ct));
        Assert.All(events, evt => Assert.NotEmpty(evt.Channels));
    }

    [Fact]
    public async Task Diagnostics_fake_returns_deterministic_seed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeDiagnosticsService service = new();

        Assert.NotEmpty(await service.GetEventsAsync(ct));
        Assert.Equal(await service.GetEventsAsync(ct), await service.GetEventsAsync(ct));
    }

    [Fact]
    public async Task Glossary_fake_returns_deterministic_seed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeGlossaryService service = new();

        GlossarySnapshot snapshot = await service.GetEntriesAsync(ct);
        Assert.True(snapshot.HasActiveProfile);
        Assert.NotEmpty(snapshot.Entries);
        Assert.Equal(snapshot.Entries, (await service.GetEntriesAsync(ct)).Entries);
    }

    [Fact]
    public async Task Glossary_fake_add_and_remove_mutate_snapshot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeGlossaryService service = new();
        var entry = new GlossaryEntry("newword-src", "newword-dst", "Profile", CaseSensitive: false, Protected: true, "note");

        await service.AddOrUpdateAsync(entry, replacingSourceTerm: null, ct);
        Assert.Contains((await service.GetEntriesAsync(ct)).Entries, e => e.SourceTerm == "newword-src");

        await service.RemoveAsync("newword-src", ct);
        Assert.DoesNotContain((await service.GetEntriesAsync(ct)).Entries, e => e.SourceTerm == "newword-src");
    }

    [Fact]
    public async Task Settings_fake_returns_default_settings_and_providers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeSettingsService service = new();

        Assert.NotEmpty(await service.GetProvidersAsync(ct));

        ApplicationSettings settings = await service.GetSettingsAsync(ct);
        Assert.Equal(UiThemePreference.System, settings.Theme);
        Assert.False(settings.StrictOffline);
        Assert.Equal(HistoryRetention.Days30, settings.HistoryRetention);
        Assert.Equal("en-US", settings.UiLanguage);
    }

    [Fact]
    public async Task Settings_fake_update_round_trips()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeSettingsService service = new();
        ApplicationSettings updated = (await service.GetSettingsAsync(ct)) with
        {
            Theme = UiThemePreference.Dark,
            StrictOffline = true,
            HistoryRetention = HistoryRetention.Off,
        };

        await service.UpdateAsync(updated, ct);

        Assert.Equal(updated, await service.GetSettingsAsync(ct));
    }

    [Fact]
    public async Task Settings_fake_update_rejects_null()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeSettingsService service = new();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.UpdateAsync(null!, ct));
    }

    [Fact]
    public async Task Secret_reference_fake_exposes_only_metadata_never_raw_material()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeSecretReferenceService service = new();

        // The presentation contract carries only identifiers, a provider id, a storage-location
        // descriptor, and a presence flag - never the secret value itself.
        string[] expectedProperties =
        [
            nameof(SecretReference.ReferenceId),
            nameof(SecretReference.ProviderId),
            nameof(SecretReference.StorageLocation),
            nameof(SecretReference.IsPresent),
        ];
        string[] actualProperties =
        [
            .. typeof(SecretReference)
                .GetProperties()
                .Where(property => !property.GetIndexParameters().Any())
                .Select(property => property.Name),
        ];
        Assert.Equal(expectedProperties.Order(), actualProperties.Order());

        foreach (SecretReference reference in await service.GetReferencesAsync(ct))
        {
            Assert.False(string.IsNullOrWhiteSpace(reference.ReferenceId));
            Assert.False(string.IsNullOrWhiteSpace(reference.ProviderId));
            // The storage location names a store, not a credential value.
            Assert.Equal("Windows Credential Manager", reference.StorageLocation);
        }
    }

    [Fact]
    public async Task Secret_reference_fake_reports_presence_case_insensitively()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeSecretReferenceService service = new();

        Assert.True(await service.HasSecretAsync("DeepL", ct));
        Assert.True(await service.HasSecretAsync("deepl", ct));
        Assert.False(await service.HasSecretAsync("Baidu Translate", ct));
        Assert.False(await service.HasSecretAsync("unknown-provider", ct));
    }

    [Fact]
    public async Task Secret_reference_fake_rejects_blank_provider_id()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeSecretReferenceService service = new();

        await Assert.ThrowsAsync<ArgumentException>(() => service.HasSecretAsync(" ", ct));
    }
}
