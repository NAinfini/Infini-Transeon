using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Fakes;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// A profile stores the catalog provider <em>id</em>; that is the only value
/// <c>ProviderRegistry.TryGet</c> and the <c>translation.local.</c> resource resolver can match.
/// The wizard shows display names, so every hand-off between the two must be explicit. These tests
/// pin both directions, because getting either wrong is silent: a display name written into a
/// profile produces no error until a line fails to translate, and a failed re-selection silently
/// swaps the user's provider on the next save.
/// </summary>
public sealed class SetupWizardProviderIdentityTests
{
    private sealed class SpyProfileService : IProfileService
    {
        private readonly ProfileEditModel? _editModel;

        public SpyProfileService(ProfileEditModel? editModel = null) => _editModel = editModel;

        public ProfileEditModel? Saved { get; private set; }

        public Task<IReadOnlyList<ProfileCard>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProfileCard>>([]);

        public Task<ProfileEditModel?> LoadForEditAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_editModel);

        public Task<IReadOnlyList<string>> GetTranslationProviderIdsAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<Guid> SaveAsync(ProfileEditModel profile, CancellationToken cancellationToken = default)
        {
            Saved = profile;
            return Task.FromResult(profile.ProfileId == Guid.Empty ? Guid.NewGuid() : profile.ProfileId);
        }

        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExportAsync(Guid profileId, Stream destination, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Guid> ImportAsync(Stream source, CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());
    }

    private static SetupWizardViewModel Create(SpyProfileService profiles) => new(
        new FakeCaptureProbe(),
        new FakeOcrProbe(),
        new FakeTranslationProbe(),
        new FakeSettingsService(),
        new FakeSecretReferenceService(),
        profiles);

    private static ProfileEditModel EditModel(string translationProviderId) => new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        "Existing profile",
        "ja",
        "zh-Hans",
        Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
        "Some window",
        "Window",
        "1920×1080",
        translationProviderId,
        []);

    [Fact]
    public async Task Saving_writes_the_catalog_id_not_the_display_name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SpyProfileService profiles = new();
        SetupWizardViewModel viewModel = Create(profiles);
        await viewModel.InitializeAsync(ct);
        viewModel.ProfileName = "Identity test";
        viewModel.SelectedTarget = viewModel.Targets.First();
        viewModel.SetSelectedTargets([viewModel.Targets.First()]);
        ProviderRow deepL = viewModel.Providers.Single(item => item.Id == "translation.deepl");
        viewModel.SelectedProvider = deepL;

        await viewModel.SaveDraftCommand.ExecuteAsync(null);

        Assert.NotNull(profiles.Saved);
        Assert.Equal("translation.deepl", profiles.Saved.TranslationProviderId);
        // Guards the exact regression: the display name differs from the id, and it is what the
        // wizard used to persist.
        Assert.NotEqual(deepL.Name, profiles.Saved.TranslationProviderId);
    }

    [Fact]
    public async Task Editing_a_profile_re_selects_the_provider_it_was_saved_with()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SpyProfileService profiles = new(EditModel("translation.baidu"));
        SetupWizardViewModel viewModel = Create(profiles);

        await viewModel.LoadForEditAsync(EditModel("translation.baidu").ProfileId, ct);

        Assert.Equal("translation.baidu", viewModel.SelectedProvider?.Id);
    }

    [Fact]
    public async Task A_display_name_left_by_an_earlier_build_still_re_selects_correctly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SpyProfileService profiles = new(EditModel("Baidu Translate"));
        SetupWizardViewModel viewModel = Create(profiles);

        await viewModel.LoadForEditAsync(EditModel("Baidu Translate").ProfileId, ct);

        Assert.Equal("translation.baidu", viewModel.SelectedProvider?.Id);
    }

    /// <summary>
    /// Local model packages and imported REST adapters are offered by the wizard but are absent from
    /// <c>ProviderCatalog.Default</c>, so <c>ProviderCatalog.Find</c> cannot rescue a display name for
    /// them. Their id must survive the round trip verbatim or the runtime loads no model at all.
    /// </summary>
    [Fact]
    public async Task An_id_outside_the_built_in_catalog_survives_the_round_trip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SpyProfileService profiles = new();
        SetupWizardViewModel viewModel = Create(profiles);
        await viewModel.InitializeAsync(ct);
        viewModel.ProfileName = "Local model";
        viewModel.SelectedTarget = viewModel.Targets.First();
        viewModel.SetSelectedTargets([viewModel.Targets.First()]);
        var local = new ProviderRow(
            "Local NLLB-200 600M",
            "NMT · local",
            "Installed · ready",
            Controls.StatusSeverity.Success,
            "offline")
        {
            Id = "translation.local.nllb-200-600m",
            IsLocalModel = true,
        };
        viewModel.Providers.Add(local);
        viewModel.SelectedProvider = local;

        await viewModel.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal("translation.local.nllb-200-600m", profiles.Saved?.TranslationProviderId);
    }
}
