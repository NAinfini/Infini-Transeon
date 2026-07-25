using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Probes;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Storage;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// Covers the readiness and error-reporting gaps that let a profile look configured while it could
/// not translate a single line: a translation test that ignored the selected provider, region counts
/// that omitted the remaining-area region, and raw machine codes shown as the only error text.
/// </summary>
public sealed class ReadinessAndProbeRoutingTests
{
    [Fact]
    public async Task Translation_test_without_a_provider_reports_the_missing_selection()
    {
        var probe = NewProbe(new EmptyCredentialStore());

        TranslationProbeResult result = await probe.TranslateAsync(
            new TranslationProbeRequest("hello", "en", "zh-Hans", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(CatalogTranslationProbe.ProviderNotSelectedCode, result.ErrorCode);
    }

    [Fact]
    public async Task Translation_test_reports_an_unknown_provider_instead_of_testing_another()
    {
        var probe = NewProbe(new EmptyCredentialStore());

        TranslationProbeResult result = await probe.TranslateAsync(
            new TranslationProbeRequest(
                "hello", "en", "zh-Hans", null, ProviderId: "translation.does-not-exist"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CatalogTranslationProbe.ProviderUnknownCode, result.ErrorCode);
    }

    /// <summary>The bug this replaces: the probe was wired to one fixed provider, so selecting
    /// Yandex and pressing "test" exercised DeepL's credentials instead.</summary>
    [Theory]
    [InlineData("translation.yandex")]
    [InlineData("translation.deepl-free")]
    [InlineData("translation.baidu")]
    public async Task Translation_test_reports_the_selected_providers_missing_credential(
        string providerId)
    {
        var probe = NewProbe(new EmptyCredentialStore());

        TranslationProbeResult result = await probe.TranslateAsync(
            new TranslationProbeRequest("hello", "en", "zh-Hans", null, ProviderId: providerId),
            TestContext.Current.CancellationToken);

        Assert.Equal(TranslationProbe.CredentialMissingCode, result.ErrorCode);
        Assert.Equal(providerId, result.ProviderId);
    }

    [Fact]
    public async Task Translation_test_reports_a_rebind_when_the_stored_key_is_bound_elsewhere()
    {
        var probe = NewProbe(new RebindRequiredCredentialStore());

        TranslationProbeResult result = await probe.TranslateAsync(
            new TranslationProbeRequest(
                "hello", "en", "zh-Hans", null, ProviderId: "translation.deepl"),
            TestContext.Current.CancellationToken);

        Assert.Equal(TranslationProbe.CredentialRebindCode, result.ErrorCode);
    }

    [Theory]
    [InlineData("provider.deepl.authorization", "ProbeErrorAuthorization")]
    [InlineData("provider.yandex.forbidden", "ProbeErrorAuthorization")]
    [InlineData("provider.http401", "ProbeErrorAuthorization")]
    [InlineData("provider.niutrans.rateLimited", "ProbeErrorRateLimited")]
    [InlineData("provider.http429", "ProbeErrorRateLimited")]
    [InlineData("provider.deepl.quotaExceeded", "ProbeErrorQuotaExceeded")]
    [InlineData("provider.azure.requestTooLarge", "ProbeErrorRequestTooLarge")]
    [InlineData("provider.yandex.unavailable", "ProbeErrorUnavailable")]
    [InlineData("provider.deepl.timeout", "ProbeErrorTimeout")]
    [InlineData("provider.deadline", "ProbeErrorTimeout")]
    [InlineData("provider.http5xx", "ProbeErrorServer")]
    [InlineData("provider.youdao.httpError", "ProbeErrorServer")]
    [InlineData("provider.malformedSse", "ProbeErrorMalformedResponse")]
    [InlineData("provider.sseLimit", "ProbeErrorLimitExceeded")]
    [InlineData("translation.probe.credentialMissing", "ProbeErrorCredentialMissing")]
    [InlineData("provider.credentialMissing", "ProbeErrorCredentialMissing")]
    [InlineData("provider.policy.strictOffline", "ProbeErrorOffline")]
    public void Error_codes_map_onto_a_localizable_key(string errorCode, string expectedKey) =>
        Assert.Equal(expectedKey, ProbeErrorPresenter.ResourceKeyFor(errorCode));

    /// <summary>A code with no family still resolves, so the UI never renders an empty InfoBar.</summary>
    [Fact]
    public void Unmapped_error_codes_fall_back_to_the_generic_key() =>
        Assert.Equal(
            ProbeErrorPresenter.UnknownResourceKey,
            ProbeErrorPresenter.ResourceKeyFor("provider.somethingBrandNew"));

    /// <summary>Debug-first: the machine code must survive localization so a screenshot stays
    /// diagnosable.</summary>
    [Fact]
    public void Described_errors_keep_the_machine_code()
    {
        string described = ProbeErrorPresenter.Describe(
            "provider.deepl.authorization", _ => "The translator rejected the key.");

        Assert.Equal(
            "The translator rejected the key. (provider.deepl.authorization)", described);
    }

    /// <summary>"Scan the remaining area" is a real translatable region. Counting only the explicit
    /// list reported zero regions and the workspace refused to start.</summary>
    [Fact]
    public async Task Remaining_area_region_counts_towards_readiness()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "infini-readiness-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "profiles.db");
        try
        {
            var repository = new ProfileRepository(databasePath);
            ProfileDocument document = ProfileDocument.Create("Remaining only", "ja", "zh-Hans") with
            {
                Targets =
                [
                    ProfileTarget.Create("Game", CaptureTargetKind.Window) with
                    {
                        Regions = [],
                        RemainingAreaRegion = ProfileRegion.Create(
                            "Remaining", new NormalizedRect(0, 0, 1, 1)) with
                        {
                            TranslationChannels =
                            [
                                ProfileTranslationChannel.Create("translation.deepl-free"),
                            ],
                        },
                    },
                ],
            };
            await repository.SaveAsync(document, ct);
            var service = new RealProfileService(repository, databasePath);

            ProfileCard card = Assert.Single(await service.GetProfilesAsync(ct));
            Assert.Equal(1, card.RegionCount);
            Assert.Equal(1, card.ChannelCount);

            IReadOnlyList<string> providers =
                await service.GetTranslationProviderIdsAsync(document.ProfileId, ct);
            Assert.Equal(["translation.deepl-free"], providers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_ids_are_empty_for_an_unknown_profile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "infini-readiness-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "profiles.db");
        try
        {
            var service = new RealProfileService(new ProfileRepository(databasePath), databasePath);
            Assert.Empty(await service.GetTranslationProviderIdsAsync(Guid.NewGuid(), ct));
            Assert.Empty(await service.GetTranslationProviderIdsAsync(Guid.Empty, ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CatalogTranslationProbe NewProbe(IBoundCredentialStore store) => new(
        store,
        new CustomRestAdapterStore(Path.Combine(
            Path.GetTempPath(),
            "infini-adapters-" + Guid.NewGuid().ToString("n"),
            "adapters.json")));

    private sealed class EmptyCredentialStore : IBoundCredentialStore
    {
        public ValueTask<string?> ReadAsync(
            string reference,
            CredentialBinding binding,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask WriteAsync(
            string reference,
            string secret,
            CredentialBinding binding,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RebindRequiredCredentialStore : IBoundCredentialStore
    {
        public ValueTask<string?> ReadAsync(
            string reference,
            CredentialBinding binding,
            CancellationToken cancellationToken = default) =>
            throw new CredentialBindingException(reference);

        public ValueTask WriteAsync(
            string reference,
            string secret,
            CredentialBinding binding,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
