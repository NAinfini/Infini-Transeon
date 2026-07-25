using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.App.Tests;

public sealed class ProviderCompositionTests
{
    [Fact]
    public void SetupCatalogOffersAllOnlineTranslatorsButNoOcrProviders()
    {
        CatalogProvider[] choices = ProviderCatalog.Default
            .Where(provider =>
                provider.IsSelectable &&
                provider.Capability == CatalogProviderCapability.Translation)
            .ToArray();

        Assert.Equal(15, choices.Length);
        Assert.Contains(choices, provider => provider.DisplayName == "DeepL");
        Assert.Contains(choices, provider => provider.DisplayName == "DeepL API Free");
        Assert.Contains(choices, provider => provider.DisplayName == "Baidu Translate");
        Assert.Contains(choices, provider => provider.DisplayName == "Alibaba Cloud Translation");
        Assert.Contains(choices, provider => provider.DisplayName == "Azure AI Translator");
        Assert.Contains(choices, provider => provider.DisplayName == "OpenAI compatible");
        Assert.Contains(choices, provider => provider.DisplayName == "DeepSeek");
        Assert.Contains(choices, provider => provider.DisplayName == "Qwen / Model Studio");
        Assert.Contains(choices, provider => provider.DisplayName == "Baidu Qianfan");
        Assert.Contains(choices, provider => provider.DisplayName == "Anthropic Claude");
        Assert.Contains(choices, provider => provider.DisplayName == "Google Gemini");
        Assert.Contains(choices, provider => provider.DisplayName == "Youdao Zhiyun");
        Assert.Contains(choices, provider => provider.DisplayName == "NiuTrans");
        Assert.Contains(choices, provider => provider.DisplayName == "Yandex Cloud Translate");
        Assert.Contains(choices, provider => provider.DisplayName == "Google Cloud Translation");
        Assert.DoesNotContain(choices, provider => provider.Kind.StartsWith("OCR", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeRegistryContainsAmericanAndChineseOnlineProviders()
    {
        var registry = EngineRuntimeComposition.BuildProviderRegistry(new EmptyCredentialStore());

        string[] ids = registry.Descriptors.Select(descriptor => descriptor.Id).ToArray();
        Assert.Contains("translation.deepl", ids);
        Assert.Contains("translation.deepl-free", ids);
        Assert.Contains("translation.baidu", ids);
        Assert.Contains("translation.alibaba", ids);
        Assert.Contains("translation.azure-ai", ids);
        Assert.Contains("translation.youdao", ids);
        Assert.Contains("translation.niutrans", ids);
        Assert.Contains("translation.yandex", ids);
        Assert.Contains("translation.google-cloud", ids);
        Assert.Contains("llm.openai", ids);
        Assert.Contains("llm.deepseek", ids);
        Assert.Contains("llm.qwen-model-studio", ids);
        Assert.Contains("llm.baidu-qianfan", ids);
        Assert.Contains("llm.anthropic", ids);
        Assert.Contains("llm.gemini", ids);
        Assert.All(ids, id => Assert.Contains(
            ProviderCatalog.Default,
            provider => provider.Id == id));
    }

    [Fact]
    public void EveryRuntimeProviderFactoryCanBeConstructed()
    {
        var registry = EngineRuntimeComposition.BuildProviderRegistry(new EmptyCredentialStore());

        foreach (string providerId in registry.Descriptors.Select(descriptor => descriptor.Id))
        {
            Assert.True(registry.TryGet(providerId, out Core.Translation.ProviderRegistration? registration));
            Assert.NotNull(registration!.Factory());
        }
    }

    [Fact]
    public void CatalogCredentialReferencesAreGloballyUniqueAndBoundToTheirProvider()
    {
        CatalogCredential[] credentials = ProviderCatalog.Default
            .SelectMany(provider => provider.Credentials)
            .ToArray();

        Assert.Equal(
            credentials.Length,
            credentials.Select(credential => credential.Reference)
                .Distinct(StringComparer.Ordinal)
                .Count());
        foreach (CatalogProvider provider in ProviderCatalog.Default)
        {
            Assert.All(provider.Credentials, credential =>
                Assert.Equal(provider.Id, credential.Binding.ProviderId));
        }
    }

    [Fact]
    public void ShippedNmtAdaptersAndCredentialCatalogUseTheSameBindings()
    {
        CatalogProvider baidu = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "translation.baidu");
        CatalogProvider alibaba = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "translation.alibaba");
        CatalogProvider azure = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "translation.azure-ai");

        Assert.Collection(
            baidu.Credentials,
            credential => Assert.Equal(
                Core.Translation.BaiduTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.BaiduTranslationOptions,
                    "app-id"),
                credential.Binding),
            credential => Assert.Equal(
                Core.Translation.BaiduTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.BaiduTranslationOptions,
                    "secret"),
                credential.Binding));
        Assert.Collection(
            alibaba.Credentials,
            credential => Assert.Equal(
                Core.Translation.AlibabaTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.AlibabaTranslationOptions,
                    "access-key-id"),
                credential.Binding),
            credential => Assert.Equal(
                Core.Translation.AlibabaTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.AlibabaTranslationOptions,
                    "access-key-secret"),
                credential.Binding));
        Assert.Equal(
            Core.Translation.AzureTranslatorProvider.CreateCredentialBinding(
                EngineRuntimeComposition.AzureTranslationOptions),
            azure.Binding);
    }

    [Fact]
    public void CloudOcrRegistryAndCredentialCatalogUseTheSameGoogleBinding()
    {
        IReadOnlyList<Core.Ocr.OcrProviderRegistration> registrations =
            EngineRuntimeComposition.BuildCloudOcrRegistrations(
                new EmptyCredentialStore(),
                AzureVisionEndpoint());
        CatalogProvider catalog = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "ocr.google-cloud-vision");

        Assert.Contains(registrations, registration =>
            registration.ProviderId == "ocr.google-cloud-vision" &&
            registration.RequiresNetwork);
        Assert.Contains(registrations, registration =>
            registration.ProviderId == "ocr.baidu" &&
            registration.RequiresNetwork);
        Assert.Contains(registrations, registration =>
            registration.ProviderId == "ocr.tencent-cloud" &&
            registration.RequiresNetwork);
        Assert.Contains(registrations, registration =>
            registration.ProviderId == "ocr.azure-ai-vision" &&
            registration.RequiresNetwork);
        Assert.Equal(
            Core.Ocr.GoogleVisionOcrProvider.CreateCredentialBinding(
                EngineRuntimeComposition.GoogleVisionOptions),
            catalog.Binding);
    }

    [Fact]
    public void EverySelectableBuiltInCloudProviderHasExactlyOneRuntimeRegistration()
    {
        var translationRegistry =
            EngineRuntimeComposition.BuildProviderRegistry(new EmptyCredentialStore());
        IReadOnlyList<Core.Ocr.OcrProviderRegistration> ocrRegistrations =
            EngineRuntimeComposition.BuildCloudOcrRegistrations(
                new EmptyCredentialStore(),
                AzureVisionEndpoint());

        string[] runtimeIds =
        [
            .. translationRegistry.Descriptors.Select(descriptor => descriptor.Id),
            .. ocrRegistrations.Select(registration => registration.ProviderId),
        ];
        string[] catalogIds = ProviderCatalog.Default
            .Where(provider =>
                provider.IsSelectable &&
                !provider.IsCustom &&
                !provider.Id.StartsWith("translation.local.", StringComparison.Ordinal))
            .Select(provider => provider.Id)
            .ToArray();

        Assert.Equal(
            runtimeIds.Order(StringComparer.Ordinal),
            catalogIds.Order(StringComparer.Ordinal));
        Assert.Equal(runtimeIds.Length, runtimeIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ChineseCloudOcrAdaptersExposeEveryRequiredBoundCredential()
    {
        CatalogProvider baidu = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "ocr.baidu");
        CatalogProvider tencent = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "ocr.tencent-cloud");

        Assert.Collection(
            baidu.Credentials,
            credential => Assert.Equal(
                Core.Ocr.BaiduOcrProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.BaiduOcrOptions,
                    "client-id"),
                credential.Binding),
            credential => Assert.Equal(
                Core.Ocr.BaiduOcrProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.BaiduOcrOptions,
                    "client-secret"),
                credential.Binding));
        Assert.Collection(
            tencent.Credentials,
            credential => Assert.Equal(
                Core.Ocr.TencentCloudOcrProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.TencentOcrOptions,
                    "secret-id"),
                credential.Binding),
            credential => Assert.Equal(
                Core.Ocr.TencentCloudOcrProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.TencentOcrOptions,
                    "secret-key"),
                credential.Binding));
    }

    [Fact]
    public void HandwrittenLlmAdaptersAndCredentialCatalogUseTheSameBindings()
    {
        CatalogProvider anthropic = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "llm.anthropic");
        CatalogProvider gemini = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "llm.gemini");

        Assert.Equal(
            Core.Translation.AnthropicTranslationProvider.CreateCredentialBinding(
                EngineRuntimeComposition.AnthropicOptions),
            anthropic.Binding);
        Assert.Equal(
            Core.Translation.GeminiTranslationProvider.CreateCredentialBinding(
                EngineRuntimeComposition.GeminiOptions),
            gemini.Binding);
    }

    [Fact]
    public async Task AzureVisionCredentialIsBoundToTheConfiguredResourceEndpoint()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "infini-provider-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string databasePath = Path.Combine(root, "settings.db");
            var settings =
                new Core.Settings.ApplicationSettingsRepository(databasePath);
            await settings.SaveAsync(
                new Core.Settings.ApplicationSettings
                {
                    ProviderEndpoints = AzureVisionEndpoint(),
                },
                TestContext.Current.CancellationToken);
            var credentials = new BoundCredentialStore(new MemoryCredentialStore());
            var secrets = new RealSecretReferenceService(
                credentials,
                settings,
                () => ProviderCatalog.Default);
            CatalogProvider azure = Assert.Single(
                ProviderCatalog.Default,
                provider => provider.Id == "ocr.azure-ai-vision");
            CatalogCredential apiKey = Assert.Single(azure.Credentials);

            await secrets.SetSecretAsync(
                azure.Id,
                apiKey.Reference,
                "test-key",
                TestContext.Current.CancellationToken);

            var options = EngineRuntimeComposition.AzureVisionPlaceholderOptions with
            {
                Endpoint = new Uri(AzureVisionEndpoint()[azure.Id]),
            };
            string? stored = await credentials.ReadAsync(
                apiKey.Reference,
                Core.Ocr.AzureVisionOcrProvider.CreateCredentialBinding(options),
                TestContext.Current.CancellationToken);
            Assert.Equal("test-key", stored);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

    private static IReadOnlyDictionary<string, string> AzureVisionEndpoint() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ocr.azure-ai-vision"] =
                "https://example-vision-resource.cognitiveservices.azure.com/",
        };
}
