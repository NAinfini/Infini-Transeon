using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;

namespace InfiniTranseon.App.Tests;

public sealed class FakeServiceContractTests
{
    [Fact]
    public void Profile_fake_returns_deterministic_seed()
    {
        FakeProfileService service = new();

        IReadOnlyList<ProfileCard> first = service.GetProfiles();
        IReadOnlyList<ProfileCard> second = service.GetProfiles();

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Runtime_control_fake_is_deterministic_and_intents_are_noops()
    {
        FakeRuntimeControlService service = new();

        Assert.Equal(service.GetRunningTargets(), service.GetRunningTargets());

        service.PauseAll();
        service.ToggleOverlay();
        service.RequestManualOcr();
    }

    [Fact]
    public void History_fake_returns_deterministic_seed_with_channels()
    {
        FakeHistoryService service = new();

        IReadOnlyList<HistoryEvent> events = service.GetEvents();

        Assert.NotEmpty(events);
        Assert.Equal(events, service.GetEvents());
        Assert.All(events, evt => Assert.NotEmpty(evt.Channels));
    }

    [Fact]
    public void Diagnostics_fake_returns_deterministic_seed()
    {
        FakeDiagnosticsService service = new();

        Assert.NotEmpty(service.GetEvents());
        Assert.Equal(service.GetEvents(), service.GetEvents());
    }

    [Fact]
    public void Glossary_fake_returns_deterministic_seed()
    {
        FakeGlossaryService service = new();

        Assert.NotEmpty(service.GetEntries());
        Assert.Equal(service.GetEntries(), service.GetEntries());
    }

    [Fact]
    public void Settings_fake_returns_default_settings_and_providers()
    {
        FakeSettingsService service = new();

        Assert.NotEmpty(service.GetProviders());

        ApplicationSettings settings = service.GetSettings();
        Assert.Equal(UiThemePreference.System, settings.Theme);
        Assert.False(settings.StrictOffline);
        Assert.Equal(HistoryRetention.Days30, settings.HistoryRetention);
        Assert.Equal("en-US", settings.UiLanguage);
    }

    [Fact]
    public void Settings_fake_update_round_trips()
    {
        FakeSettingsService service = new();
        ApplicationSettings updated = service.GetSettings() with
        {
            Theme = UiThemePreference.Dark,
            StrictOffline = true,
            HistoryRetention = HistoryRetention.Off,
        };

        service.Update(updated);

        Assert.Equal(updated, service.GetSettings());
    }

    [Fact]
    public void Settings_fake_update_rejects_null()
    {
        FakeSettingsService service = new();

        Assert.Throws<ArgumentNullException>(() => service.Update(null!));
    }

    [Fact]
    public void Secret_reference_fake_exposes_only_metadata_never_raw_material()
    {
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

        foreach (SecretReference reference in service.GetReferences())
        {
            Assert.False(string.IsNullOrWhiteSpace(reference.ReferenceId));
            Assert.False(string.IsNullOrWhiteSpace(reference.ProviderId));
            // The storage location names a store, not a credential value.
            Assert.Equal("Windows Credential Manager", reference.StorageLocation);
        }
    }

    [Fact]
    public void Secret_reference_fake_reports_presence_case_insensitively()
    {
        FakeSecretReferenceService service = new();

        Assert.True(service.HasSecret("DeepL"));
        Assert.True(service.HasSecret("deepl"));
        Assert.False(service.HasSecret("Baidu Translate"));
        Assert.False(service.HasSecret("unknown-provider"));
    }

    [Fact]
    public void Secret_reference_fake_rejects_blank_provider_id()
    {
        FakeSecretReferenceService service = new();

        Assert.Throws<ArgumentException>(() => service.HasSecret(" "));
    }
}
