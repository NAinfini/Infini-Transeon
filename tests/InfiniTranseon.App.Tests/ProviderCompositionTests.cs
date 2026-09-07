using System.Xml.Linq;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Core.Privacy;
using ModelReasoningEffort = InfiniTranseon.Contracts.Translation.ModelReasoningEffort;

namespace InfiniTranseon.App.Tests;

public sealed class ProviderCompositionTests
{
    // A catalog entry names its taxonomy, description and unavailable state by resource key. A key
    // that is not declared throws only when a user opens the providers page in that language, so the
    // whole catalog is checked against both tables here instead.
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void Every_catalog_resource_key_is_declared(string culture)
    {
        HashSet<string> declared = XDocument.Load(AppSourcePaths.ResourcesFile(culture)).Root!
            .Elements("data")
            .Select(data => (string)data.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);

        List<string> missing =
        [
            .. ProviderCatalog.Default
                .SelectMany(provider => new[]
                {
                    provider.KindResourceKey,
                    provider.DetailResourceKey,
                    provider.UnavailableStateResourceKey,
                })
                .Where(key => key is not null)
                .Select(key => key!)
                .Distinct(StringComparer.Ordinal)
                .Where(key => !declared.Contains(key)),
        ];

        Assert.True(missing.Count == 0, $"Missing in {culture}: [{string.Join(", ", missing)}].");
    }

    [Fact]
    public void SetupCatalogOffersAllOnlineTranslatorsButNoOcrProviders()
    {
        CatalogProvider[] choices = ProviderCatalog.Default
            .Where(provider =>
                provider.IsSelectable &&
                provider.Capability == CatalogProviderCapability.Translation)
            .ToArray();

        Assert.Equal(17, choices.Length);
        Assert.Contains(choices, provider => provider.DisplayName == "DeepL");
        Assert.Contains(choices, provider => provider.DisplayName == "DeepL API Free");
        Assert.Contains(choices, provider => provider.DisplayName == "Baidu Translate");
        Assert.Contains(choices, provider => provider.DisplayName == "Alibaba Cloud Translation");
        Assert.Contains(choices, provider => provider.DisplayName == "Azure AI Translator");
        Assert.Contains(choices, provider => provider.DisplayName == "OpenAI compatible");
        Assert.Contains(choices, provider => provider.DisplayName == "xAI Grok");
        Assert.Contains(choices, provider => provider.DisplayName == "OpenRouter");
        Assert.Contains(choices, provider => provider.DisplayName == "DeepSeek");
        Assert.Contains(choices, provider => provider.DisplayName == "Qwen / Model Studio");
        Assert.Contains(choices, provider => provider.DisplayName == "Baidu Qianfan");
        Assert.Contains(choices, provider => provider.DisplayName == "Anthropic Claude");
        Assert.Contains(choices, provider => provider.DisplayName == "Google Gemini");
        Assert.Contains(choices, provider => provider.DisplayName == "Youdao Zhiyun");
        Assert.Contains(choices, provider => provider.DisplayName == "NiuTrans");
        Assert.Contains(choices, provider => provider.DisplayName == "Yandex Cloud Translate");
        Assert.Contains(choices, provider => provider.DisplayName == "Google Cloud Translation");
        Assert.DoesNotContain(choices, provider => provider.Capability == CatalogProviderCapability.Ocr);
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
        Assert.Contains("llm.grok", ids);
        Assert.Contains("llm.openrouter", ids);
        Assert.Contains("llm.deepseek", ids);
        Assert.Contains("llm.qwen-model-studio", ids);
        Assert.Contains("llm.baidu-qianfan", ids);
        Assert.Contains("llm.anthropic", ids);
        Assert.Contains("llm.gemini", ids);
        Assert.Equal(
            EngineRuntimeComposition.GrokDefaultModel,
            registry.Descriptors.Single(descriptor => descriptor.Id == "llm.grok").ModelId);
        Assert.Equal(
            EngineRuntimeComposition.OpenRouterDefaultModel,
            registry.Descriptors.Single(descriptor => descriptor.Id == "llm.openrouter").ModelId);
        Assert.All(ids, id => Assert.Contains(
            ProviderCatalog.Default,
            provider => provider.Id == id));
    }

    [Fact]
    public void LlmCatalogDefaultsAndOverridesDriveRuntimeIdentityAndCredentialOrigin()
    {
        CatalogProvider deepSeek = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "llm.deepseek");
        var endpoints = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [deepSeek.Id] = "https://gateway.example.com/v1/chat/completions",
        };
        var models = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [deepSeek.Id] = "vendor/deepseek-chat",
        };

        Assert.True(deepSeek.CanOverrideEndpoint);
        Assert.True(deepSeek.CanOverrideModel);
        Assert.True(deepSeek.CanOverrideReasoningEffort);
        Assert.Equal(EngineRuntimeComposition.DeepSeekOptions.Endpoint, deepSeek.DefaultEndpoint);
        Assert.Equal(EngineRuntimeComposition.DeepSeekDefaultModel, deepSeek.DefaultModel);
        Assert.Equal(new Uri(endpoints[deepSeek.Id]), deepSeek.ResolveEndpoint(endpoints));
        Assert.Equal(models[deepSeek.Id], deepSeek.ResolveModel(models));

        Core.Translation.ProviderRegistry registry =
            EngineRuntimeComposition.BuildProviderRegistry(
                new EmptyCredentialStore(),
                providerEndpoints: endpoints,
                providerModels: models);
        Assert.Equal(
            models[deepSeek.Id],
            registry.Descriptors.Single(descriptor => descriptor.Id == deepSeek.Id).ModelId);
        Assert.Equal(
            "gateway.example.com",
            Assert.Single(deepSeek.Credentials).ResolveBinding(endpoints).Host);
    }

    [Fact]
    public void OpenRouterDefaultsToFreeRouterAndAcceptsSpecificFreeModelSlugs()
    {
        CatalogProvider openRouter = Assert.Single(
            ProviderCatalog.Default,
            provider => provider.Id == "llm.openrouter");
        const string freeModel = "vendor/model:free";
        var models = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [openRouter.Id] = freeModel,
        };

        Assert.Equal(
            new Uri("https://openrouter.ai/api/v1/chat/completions"),
            openRouter.DefaultEndpoint);
        Assert.Equal("openrouter/free", openRouter.DefaultModel);
        Assert.True(openRouter.CanOverrideEndpoint);
        Assert.True(openRouter.CanOverrideModel);
        Assert.Equal(freeModel, openRouter.ResolveModel(models));

        Core.Translation.ProviderRegistry registry =
            EngineRuntimeComposition.BuildProviderRegistry(
                new EmptyCredentialStore(),
                providerModels: models);
        Assert.Equal(
            freeModel,
            registry.Descriptors.Single(descriptor => descriptor.Id == openRouter.Id).ModelId);
    }

    [Fact]
    public void EveryCloudLlmShowsItsDefaultModelAndOnlyCompatibleProtocolsExposeEndpointOverride()
    {
        CatalogProvider[] llms = ProviderCatalog.Default
            .Where(provider => provider.Id.StartsWith("llm.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(8, llms.Length);
        Assert.All(llms, provider =>
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.DefaultModel));
            Assert.True(provider.CanOverrideModel);
        });
        CatalogProvider gemini = llms.Single(provider => provider.Id == "llm.gemini");
        Assert.Null(gemini.DefaultEndpoint);
        Assert.False(gemini.CanOverrideEndpoint);
        Assert.All(
            llms.Where(provider => provider != gemini),
            provider =>
            {
                Assert.NotNull(provider.DefaultEndpoint);
                Assert.True(provider.CanOverrideEndpoint);
            });
    }

    [Fact]
    public async Task ProviderRowsShowEffectiveValuesAndKeepBuiltInDefaultsForRestore()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "infini-provider-row-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string databasePath = Path.Combine(root, "settings.db");
            var repository = new Core.Settings.ApplicationSettingsRepository(databasePath);
            await repository.SaveAsync(
                new Core.Settings.ApplicationSettings
                {
                    ProviderEndpoints = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["llm.deepseek"] =
                            "https://gateway.example.com/v1/chat/completions",
                    },
                    ProviderModels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["llm.deepseek"] = "vendor/deepseek-chat",
                    },
                    ProviderReasoningEfforts =
                        new Dictionary<string, ModelReasoningEffort>(StringComparer.Ordinal)
                        {
                            ["llm.deepseek"] = ModelReasoningEffort.High,
                        },
                },
                TestContext.Current.CancellationToken);
            var secretStore = new BoundCredentialStore(new MemoryCredentialStore());
            var secrets = new RealSecretReferenceService(
                secretStore,
                repository,
                () => ProviderCatalog.Default);
            var service = new RealSettingsService(
                repository,
                secrets,
                TestResourceText.Lookup);

            ProviderRow deepSeek = Assert.Single(
                await service.GetProvidersAsync(TestContext.Current.CancellationToken),
                provider => provider.Id == "llm.deepseek");

            Assert.Equal(
                "https://gateway.example.com/v1/chat/completions",
                deepSeek.Endpoint);
            Assert.Equal(
                EngineRuntimeComposition.DeepSeekOptions.Endpoint.AbsoluteUri,
                deepSeek.DefaultEndpoint);
            Assert.Equal("vendor/deepseek-chat", deepSeek.Model);
            Assert.Equal(EngineRuntimeComposition.DeepSeekDefaultModel, deepSeek.DefaultModel);
            Assert.True(deepSeek.IsEndpointOverridden);
            Assert.True(deepSeek.IsModelOverridden);
            Assert.Equal(ModelReasoningEffort.High, deepSeek.ReasoningEffort);
            Assert.True(deepSeek.IsReasoningEffortOverridden);
            Assert.True(deepSeek.CanOverrideReasoningEffort);
            Assert.True(deepSeek.CanConfigure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task User_can_add_multiple_openai_compatible_providers_to_catalog_and_runtime()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "infini-custom-provider-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string providerStorePath = Path.Combine(root, "providers.json");
            var store = new CustomRestAdapterStore(providerStorePath);
            var repository = new Core.Settings.ApplicationSettingsRepository(
                Path.Combine(root, "settings.db"));
            var credentialStore = new BoundCredentialStore(new MemoryCredentialStore());
            var secrets = new RealSecretReferenceService(
                credentialStore,
                repository,
                () => ProviderCatalog.Default.Concat(store.GetCatalogProviders()).ToArray());
            var settings = new RealSettingsService(
                repository,
                secrets,
                TestResourceText.Lookup,
                store);

            ProviderRow first = await settings.AddOpenAiCompatibleProviderAsync(
                "First Gateway",
                new Uri("https://first.example.test/v1/chat/completions"),
                "vendor/first",
                ModelReasoningEffort.Medium,
                TestContext.Current.CancellationToken);
            ProviderRow second = await settings.AddOpenAiCompatibleProviderAsync(
                "Second Gateway",
                new Uri("https://second.example.test/v1/chat/completions"),
                "vendor/second",
                reasoningEffort: null,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(first.Id, second.Id);
            ProviderRow configured = Assert.Single(
                await settings.GetProvidersAsync(TestContext.Current.CancellationToken),
                provider => provider.Id == first.Id);
            Assert.Equal(ModelReasoningEffort.Medium, configured.ReasoningEffort);
            Assert.True(configured.IsCustom);
            Assert.Equal("vendor/first", configured.Model);
            Assert.Equal($"{first.Id}.api-key", Assert.Single(configured.Credentials).ReferenceId);
            Assert.Equal("first.example.test", new Uri(configured.Endpoint!).Host);
            await secrets.SetSecretAsync(
                first.Id,
                Assert.Single(configured.Credentials).ReferenceId,
                "test-secret",
                TestContext.Current.CancellationToken);
            Assert.True(await secrets.HasSecretAsync(
                first.Id,
                TestContext.Current.CancellationToken));
            Assert.DoesNotContain(
                "test-secret",
                File.ReadAllText(providerStorePath),
                StringComparison.Ordinal);

            Core.Translation.ProviderRegistry registry =
                EngineRuntimeComposition.BuildProviderRegistry(
                    credentialStore,
                    providerReasoningEfforts:
                        (await settings.GetSettingsAsync(TestContext.Current.CancellationToken))
                        .EffectiveProviderReasoningEfforts,
                    customOpenAiProviders: store.LoadOpenAiCompatible());
            Assert.Contains(registry.Descriptors, descriptor =>
                descriptor.Id == first.Id && descriptor.ModelId == "vendor/first");
            Assert.Contains(registry.Descriptors, descriptor =>
                descriptor.Id == second.Id && descriptor.ModelId == "vendor/second");
            Assert.IsType<Core.Translation.OpenAiCompatibleProvider>(
                registry.Descriptors
                    .Select(descriptor => registry.TryGet(
                        descriptor.Id,
                        out Core.Translation.ProviderRegistration? registration)
                        ? registration
                        : null)
                    .Single(registration => registration?.Descriptor.Id == first.Id)!
                    .Factory());

            await settings.RemoveCustomProviderAsync(
                first.Id,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(
                first.Id,
                (await settings.GetSettingsAsync(TestContext.Current.CancellationToken))
                .EffectiveProviderReasoningEfforts.Keys);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StrictOfflineRuntimeCompositionRegistersOnlyLocalProviders()
    {
        var local = new Core.Translation.ProviderRegistration(
            new Core.Translation.ProviderDescriptor(
                "translation.local.test",
                Contracts.Translation.ProviderKind.Translation,
                RequiresNetwork: false,
                SupportsStreaming: false,
                SupportsContext: false,
                SupportsGlossary: false),
            () => throw new InvalidOperationException("Factory must not run during composition."));

        Core.Translation.ProviderRegistry registry =
            EngineRuntimeComposition.BuildProviderRegistry(
                new EmptyCredentialStore(),
                additionalRegistrations: [local],
                strictOffline: true);
        IReadOnlyList<Core.Ocr.OcrProviderRegistration> cloudOcr =
            EngineRuntimeComposition.BuildCloudOcrRegistrations(
                new EmptyCredentialStore(),
                AzureVisionEndpoint(),
                strictOffline: true);

        Core.Translation.ProviderDescriptor descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("translation.local.test", descriptor.Id);
        Assert.False(descriptor.RequiresNetwork);
        Assert.Empty(cloudOcr);
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
    public void BaiduOcrCompositionUsesTheAccurateEndpointForAutomaticDetection()
    {
        Assert.Equal(
            "https://aip.baidubce.com/rest/2.0/ocr/v1/accurate",
            EngineRuntimeComposition.BaiduOcrOptions.OcrEndpoint.AbsoluteUri.TrimEnd('/'));
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
