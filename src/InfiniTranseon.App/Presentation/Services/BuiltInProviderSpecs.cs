using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Translation;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// One definition for every built-in online provider. The settings catalog and runtime
/// registries are projections of this list, so a provider cannot silently exist in only one.
/// </summary>
internal sealed record BuiltInProviderSpec(
    CatalogProvider Catalog,
    Func<IBoundCredentialStore, ProviderRegistration>? CreateTranslationRegistration,
    Func<
        IBoundCredentialStore,
        IReadOnlyDictionary<string, string>,
        OcrProviderRegistration?>? CreateOcrRegistration);

internal static class BuiltInProviderSpecs
{
    public static IReadOnlyList<BuiltInProviderSpec> All { get; } =
    [
        Translation(
            new CatalogProvider(
                EngineRuntimeComposition.DeepLDefinition.Id,
                "DeepL",
                "ProviderKindNmtCloud",
                EngineRuntimeComposition.DeepLDefinition.CredentialReferences[0],
                DeclarativeRestProvider.CreateBinding(
                    EngineRuntimeComposition.DeepLDefinition,
                    EngineRuntimeComposition.DeepLDefinition.CredentialReferences[0]),
                "ProviderDetailDeepL"),
            ProviderKind.Translation,
            "deepl-v2",
            credentials => new DeclarativeRestProvider(
                EngineRuntimeComposition.DeepLDefinition,
                EngineRuntimeComposition.ClientFor(
                    EngineRuntimeComposition.DeepLDefinition.Id,
                    EngineRuntimeComposition.DeepLDefinition.Endpoint,
                    ProxyPolicy.System),
                credentials)),
        DeclarativeRestTranslation(
            EngineRuntimeComposition.DeepLFreeDefinition,
            "ProviderKindNmtCloud",
            "deepl-v2-free",
            "ProviderDetailDeepLFree"),
        Translation(
            new CatalogProvider(
                EngineRuntimeComposition.NiuTransDefinition.Id,
                "NiuTrans",
                "ProviderKindNmtCloudChina",
                EngineRuntimeComposition.NiuTransDefinition.CredentialReferences[0],
                DeclarativeRestProvider.CreateBinding(
                    EngineRuntimeComposition.NiuTransDefinition,
                    EngineRuntimeComposition.NiuTransDefinition.CredentialReferences[0]),
                "ProviderDetailNiuTrans"),
            ProviderKind.Translation,
            "niutrans-text",
            credentials => new DeclarativeRestProvider(
                EngineRuntimeComposition.NiuTransDefinition,
                EngineRuntimeComposition.ClientFor(
                    EngineRuntimeComposition.NiuTransDefinition.Id,
                    EngineRuntimeComposition.NiuTransDefinition.Endpoint,
                    ProxyPolicy.System),
                credentials)),
        Translation(
            new CatalogProvider(
                EngineRuntimeComposition.YandexDefinition.Id,
                "Yandex Cloud Translate",
                "ProviderKindNmtCloud",
                EngineRuntimeComposition.YandexDefinition.CredentialReferences.Select(reference =>
                    new CatalogCredential(
                        reference,
                        reference.EndsWith(".api-key", StringComparison.Ordinal)
                            ? "API key"
                            : "Folder ID",
                        DeclarativeRestProvider.CreateBinding(
                            EngineRuntimeComposition.YandexDefinition,
                            reference)))
                    .ToArray(),
                "ProviderDetailYandex"),
            ProviderKind.Translation,
            "translate-v2",
            credentials => new DeclarativeRestProvider(
                EngineRuntimeComposition.YandexDefinition,
                EngineRuntimeComposition.ClientFor(
                    EngineRuntimeComposition.YandexDefinition.Id,
                    EngineRuntimeComposition.YandexDefinition.Endpoint,
                    ProxyPolicy.System),
                credentials)),
        Translation(
            new CatalogProvider(
                "translation.baidu",
                "Baidu Translate",
                "ProviderKindNmtCloudChina",
                [
                    new(
                        EngineRuntimeComposition.BaiduTranslationOptions.AppIdReference,
                        "APP ID",
                        BaiduTranslationProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.BaiduTranslationOptions,
                            "app-id")),
                    new(
                        EngineRuntimeComposition.BaiduTranslationOptions.SecretReference,
                        "Secret",
                        BaiduTranslationProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.BaiduTranslationOptions,
                            "secret")),
                ],
                "ProviderDetailBaidu"),
            ProviderKind.Translation,
            "baidu-general",
            credentials => new BaiduTranslationProvider(
                EngineRuntimeComposition.BaiduTranslationOptions,
                EngineRuntimeComposition.ClientFor(
                    "translation.baidu",
                    EngineRuntimeComposition.BaiduTranslationOptions.Endpoint,
                    EngineRuntimeComposition.BaiduTranslationOptions.ProxyPolicy),
                credentials)),
        Translation(
            new CatalogProvider(
                "translation.alibaba",
                "Alibaba Cloud Translation",
                "ProviderKindNmtCloudChina",
                [
                    new(
                        EngineRuntimeComposition.AlibabaTranslationOptions.AccessKeyIdReference,
                        "AccessKey ID",
                        AlibabaTranslationProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.AlibabaTranslationOptions,
                            "access-key-id")),
                    new(
                        EngineRuntimeComposition.AlibabaTranslationOptions.AccessKeySecretReference,
                        "AccessKey secret",
                        AlibabaTranslationProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.AlibabaTranslationOptions,
                            "access-key-secret")),
                ],
                "ProviderDetailAlibaba"),
            ProviderKind.Translation,
            "translate-general",
            credentials => new AlibabaTranslationProvider(
                EngineRuntimeComposition.AlibabaTranslationOptions,
                EngineRuntimeComposition.ClientFor(
                    "translation.alibaba",
                    EngineRuntimeComposition.AlibabaTranslationOptions.Endpoint,
                    EngineRuntimeComposition.AlibabaTranslationOptions.ProxyPolicy),
                credentials)),
        Translation(
            new CatalogProvider(
                "translation.azure-ai",
                "Azure AI Translator",
                "ProviderKindNmtCloud",
                EngineRuntimeComposition.AzureTranslationOptions.CredentialReference,
                AzureTranslatorProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.AzureTranslationOptions),
                "ProviderDetailAzureTranslator"),
            ProviderKind.Translation,
            "translator-v3",
            credentials => new AzureTranslatorProvider(
                EngineRuntimeComposition.AzureTranslationOptions,
                EngineRuntimeComposition.ClientFor(
                    "translation.azure-ai",
                    EngineRuntimeComposition.AzureTranslationOptions.Endpoint,
                    EngineRuntimeComposition.AzureTranslationOptions.ProxyPolicy),
                credentials)),
        Translation(
            new CatalogProvider(
                "translation.youdao",
                "Youdao Zhiyun",
                "ProviderKindNmtCloudChina",
                [
                    new(
                        EngineRuntimeComposition.YoudaoTranslationOptions.AppKeyReference,
                        "App key",
                        YoudaoTranslationProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.YoudaoTranslationOptions,
                            "app-key")),
                    new(
                        EngineRuntimeComposition.YoudaoTranslationOptions.AppSecretReference,
                        "App secret",
                        YoudaoTranslationProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.YoudaoTranslationOptions,
                            "app-secret")),
                ],
                "ProviderDetailYoudao"),
            ProviderKind.Translation,
            "text-v3-game",
            credentials => new YoudaoTranslationProvider(
                EngineRuntimeComposition.YoudaoTranslationOptions,
                EngineRuntimeComposition.ClientFor(
                    "translation.youdao",
                    EngineRuntimeComposition.YoudaoTranslationOptions.Endpoint,
                    EngineRuntimeComposition.YoudaoTranslationOptions.ProxyPolicy),
                credentials)),
        Translation(
            new CatalogProvider(
                "translation.google-cloud",
                "Google Cloud Translation",
                "ProviderKindNmtCloud",
                EngineRuntimeComposition.GoogleTranslationTokenOptions.CredentialReference,
                GoogleServiceAccountTokenSource.CreateCredentialBinding(
                    EngineRuntimeComposition.GoogleTranslationTokenOptions),
                "ProviderDetailGoogleCloud"),
            ProviderKind.Translation,
            "translate-v3",
            credentials => new GoogleCloudTranslationProvider(
                EngineRuntimeComposition.GoogleTranslationOptions,
                EngineRuntimeComposition.ClientFor(
                    "translation.google-cloud",
                    EngineRuntimeComposition.GoogleTranslationOptions.Endpoint,
                    EngineRuntimeComposition.GoogleTranslationOptions.ProxyPolicy),
                new GoogleServiceAccountTokenSource(
                    EngineRuntimeComposition.GoogleTranslationTokenOptions,
                    EngineRuntimeComposition.ClientFor(
                        "translation.google-cloud.oauth",
                        EngineRuntimeComposition.GoogleTranslationTokenOptions.TokenEndpoint,
                        EngineRuntimeComposition.GoogleTranslationTokenOptions.ProxyPolicy),
                    credentials))),
        OpenAiCompatible(
            EngineRuntimeComposition.OpenAiOptions,
            "OpenAI compatible",
            "ProviderKindLlmCloud",
            "ProviderDetailOpenAi"),
        OpenAiCompatible(
            EngineRuntimeComposition.DeepSeekOptions,
            "DeepSeek",
            "ProviderKindLlmCloudChina",
            "ProviderDetailDeepSeek"),
        OpenAiCompatible(
            EngineRuntimeComposition.QwenOptions,
            "Qwen / Model Studio",
            "ProviderKindLlmCloudChina",
            "ProviderDetailQwen"),
        OpenAiCompatible(
            EngineRuntimeComposition.QianfanOptions,
            "Baidu Qianfan",
            "ProviderKindLlmCloudChina",
            "ProviderDetailQianfan"),
        Translation(
            new CatalogProvider(
                "llm.anthropic",
                "Anthropic Claude",
                "ProviderKindLlmCloud",
                EngineRuntimeComposition.AnthropicOptions.CredentialReference,
                AnthropicTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.AnthropicOptions),
                "ProviderDetailAnthropic"),
            ProviderKind.LargeLanguageModel,
            EngineRuntimeComposition.AnthropicOptions.Model,
            credentials => new AnthropicTranslationProvider(
                EngineRuntimeComposition.AnthropicOptions,
                EngineRuntimeComposition.ClientFor(
                    "llm.anthropic",
                    EngineRuntimeComposition.AnthropicOptions.Endpoint,
                    EngineRuntimeComposition.AnthropicOptions.ProxyPolicy),
                credentials)),
        Translation(
            new CatalogProvider(
                "llm.gemini",
                "Google Gemini",
                "ProviderKindLlmCloud",
                EngineRuntimeComposition.GeminiOptions.CredentialReference,
                GeminiTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.GeminiOptions),
                "ProviderDetailGemini"),
            ProviderKind.LargeLanguageModel,
            EngineRuntimeComposition.GeminiDefaultModel,
            credentials => new GeminiTranslationProvider(
                EngineRuntimeComposition.GeminiOptions,
                EngineRuntimeComposition.ClientFor(
                    "llm.gemini",
                    EngineRuntimeComposition.GeminiOptions.Endpoint,
                EngineRuntimeComposition.GeminiOptions.ProxyPolicy),
                credentials)),
        AzureVisionOcr(),
        Ocr(
            new CatalogProvider(
                "ocr.google-cloud-vision",
                "Google Cloud Vision",
                "ProviderKindOcrCloud",
                EngineRuntimeComposition.GoogleVisionOptions.CredentialReference,
                GoogleVisionOcrProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.GoogleVisionOptions),
                "ProviderDetailGoogleVisionOcr")
            {
                Capability = CatalogProviderCapability.Ocr,
            },
            credentials => new GoogleVisionOcrProvider(
                EngineRuntimeComposition.GoogleVisionOptions,
                EngineRuntimeComposition.ClientFor(
                    "ocr.google-cloud-vision",
                    EngineRuntimeComposition.GoogleVisionOptions.Endpoint,
                    EngineRuntimeComposition.GoogleVisionOptions.ProxyPolicy),
                credentials)),
        Ocr(
            new CatalogProvider(
                "ocr.baidu",
                "Baidu OCR",
                "ProviderKindOcrCloudChina",
                [
                    new(
                        EngineRuntimeComposition.BaiduOcrOptions.ClientIdReference,
                        "Client ID",
                        BaiduOcrProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.BaiduOcrOptions,
                            "client-id")),
                    new(
                        EngineRuntimeComposition.BaiduOcrOptions.ClientSecretReference,
                        "Client secret",
                        BaiduOcrProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.BaiduOcrOptions,
                            "client-secret")),
                ],
                "ProviderDetailBaiduOcr")
            {
                Capability = CatalogProviderCapability.Ocr,
            },
            credentials => new BaiduOcrProvider(
                EngineRuntimeComposition.BaiduOcrOptions,
                EngineRuntimeComposition.ClientFor(
                    "ocr.baidu",
                    EngineRuntimeComposition.BaiduOcrOptions.TokenEndpoint,
                    EngineRuntimeComposition.BaiduOcrOptions.ProxyPolicy),
                credentials)),
        Ocr(
            new CatalogProvider(
                "ocr.tencent-cloud",
                "Tencent Cloud OCR",
                "ProviderKindOcrCloudChina",
                [
                    new(
                        EngineRuntimeComposition.TencentOcrOptions.SecretIdReference,
                        "Secret ID",
                        TencentCloudOcrProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.TencentOcrOptions,
                            "secret-id")),
                    new(
                        EngineRuntimeComposition.TencentOcrOptions.SecretKeyReference,
                        "Secret key",
                        TencentCloudOcrProvider.CreateCredentialBinding(
                            EngineRuntimeComposition.TencentOcrOptions,
                            "secret-key")),
                ],
                "ProviderDetailTencentOcr")
            {
                Capability = CatalogProviderCapability.Ocr,
            },
            credentials => new TencentCloudOcrProvider(
                EngineRuntimeComposition.TencentOcrOptions,
                EngineRuntimeComposition.ClientFor(
                    "ocr.tencent-cloud",
                    EngineRuntimeComposition.TencentOcrOptions.Endpoint,
                    EngineRuntimeComposition.TencentOcrOptions.ProxyPolicy),
                credentials)),
    ];

    public static IReadOnlyList<ProviderRegistration> CreateTranslationRegistrations(
        IBoundCredentialStore credentials) =>
        All.Where(spec => spec.CreateTranslationRegistration is not null)
            .Select(spec => spec.CreateTranslationRegistration!(credentials))
            .ToArray();

    public static IReadOnlyList<OcrProviderRegistration> CreateOcrRegistrations(
        IBoundCredentialStore credentials,
        IReadOnlyDictionary<string, string> providerEndpoints) =>
        All.Where(spec => spec.CreateOcrRegistration is not null)
            .Select(spec => spec.CreateOcrRegistration!(credentials, providerEndpoints))
            .Where(registration => registration is not null)
            .Select(registration => registration!)
            .ToArray();

    private static BuiltInProviderSpec Translation(
        CatalogProvider catalog,
        ProviderKind kind,
        string modelId,
        Func<IBoundCredentialStore, ITranslationProvider> createProvider) =>
        new(
            catalog,
            credentials => new ProviderRegistration(
                ProviderDescriptor.Online(catalog.Id, kind, modelId),
                () => createProvider(credentials)),
            CreateOcrRegistration: null);

    /// <summary>Projects a declarative REST definition into a catalog entry plus runtime
    /// registration, with one catalog credential per declared credential reference.</summary>
    private static BuiltInProviderSpec DeclarativeRestTranslation(
        DeclarativeRestAdapterDefinition definition,
        string kindResourceKey,
        string modelId,
        string detailResourceKey) =>
        Translation(
            new CatalogProvider(
                definition.Id,
                definition.DisplayName,
                kindResourceKey,
                definition.CredentialReferences
                    .Select(reference => new CatalogCredential(
                        reference,
                        "API key",
                        DeclarativeRestProvider.CreateBinding(definition, reference)))
                    .ToArray(),
                detailResourceKey),
            ProviderKind.Translation,
            modelId,
            credentials => new DeclarativeRestProvider(
                definition,
                EngineRuntimeComposition.ClientFor(
                    definition.Id,
                    definition.Endpoint,
                    ProxyPolicy.System),
                credentials));

    private static BuiltInProviderSpec OpenAiCompatible(
        OpenAiCompatibleOptions options,
        string displayName,
        string kindResourceKey,
        string detailResourceKey) =>
        Translation(
            new CatalogProvider(
                options.ProviderId,
                displayName,
                kindResourceKey,
                options.CredentialReference,
                OpenAiCompatibleProvider.CreateCredentialBinding(options),
                detailResourceKey),
            ProviderKind.LargeLanguageModel,
            options.Model,
            credentials => new OpenAiCompatibleProvider(
                options,
                EngineRuntimeComposition.ClientFor(
                    options.ProviderId,
                    options.Endpoint,
                    options.ProxyPolicy),
                credentials));

    private static BuiltInProviderSpec Ocr(
        CatalogProvider catalog,
        Func<IBoundCredentialStore, IOcrProvider> createProvider) =>
        new(
            catalog,
            CreateTranslationRegistration: null,
            (credentials, _) => new OcrProviderRegistration(
                catalog.Id,
                requiresNetwork: true,
                () => createProvider(credentials)));

    private static BuiltInProviderSpec AzureVisionOcr()
    {
        AzureVisionOcrOptions placeholder = EngineRuntimeComposition.AzureVisionPlaceholderOptions;
        var credential = new CatalogCredential(
            placeholder.CredentialReference,
            "API key",
            AzureVisionOcrProvider.CreateCredentialBinding(placeholder))
        {
            BindingResolver = endpoints =>
                AzureVisionOcrProvider.CreateCredentialBinding(
                    ResolveAzureVisionOptions(endpoints)),
        };
        var catalog = new CatalogProvider(
            "ocr.azure-ai-vision",
            "Azure AI Vision OCR",
            "ProviderKindOcrCloud",
            [credential],
            "ProviderDetailAzureVisionOcr")
        {
            Capability = CatalogProviderCapability.Ocr,
            RequiresEndpoint = true,
            EndpointPlaceholder =
                "https://your-resource-name.cognitiveservices.azure.com/",
        };
        return new BuiltInProviderSpec(
            catalog,
            CreateTranslationRegistration: null,
            (credentials, endpoints) =>
            {
                if (!endpoints.ContainsKey(catalog.Id))
                {
                    return null;
                }
                AzureVisionOcrOptions options = ResolveAzureVisionOptions(endpoints);
                return new OcrProviderRegistration(
                    catalog.Id,
                    requiresNetwork: true,
                    () => new AzureVisionOcrProvider(
                        options,
                        EngineRuntimeComposition.ClientFor(
                            catalog.Id,
                            options.Endpoint,
                            options.ProxyPolicy),
                        credentials));
            });
    }

    private static AzureVisionOcrOptions ResolveAzureVisionOptions(
        IReadOnlyDictionary<string, string> providerEndpoints)
    {
        const string providerId = "ocr.azure-ai-vision";
        if (!providerEndpoints.TryGetValue(providerId, out string? endpointText) ||
            !Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException(
                "Azure AI Vision OCR requires a valid HTTPS resource endpoint.");
        }
        return EngineRuntimeComposition.AzureVisionPlaceholderOptions with { Endpoint = endpoint };
    }
}
