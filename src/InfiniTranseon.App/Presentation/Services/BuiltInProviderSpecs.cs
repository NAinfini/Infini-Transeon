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
                "NMT · cloud",
                EngineRuntimeComposition.DeepLDefinition.CredentialReferences[0],
                DeclarativeRestProvider.CreateBinding(
                    EngineRuntimeComposition.DeepLDefinition,
                    EngineRuntimeComposition.DeepLDefinition.CredentialReferences[0]),
                "API key stored in Windows Credential Manager"),
            ProviderKind.Translation,
            "deepl-v2",
            credentials => new DeclarativeRestProvider(
                EngineRuntimeComposition.DeepLDefinition,
                EngineRuntimeComposition.ClientFor(
                    EngineRuntimeComposition.DeepLDefinition.Id,
                    EngineRuntimeComposition.DeepLDefinition.Endpoint,
                    ProxyPolicy.System),
                credentials)),
        Translation(
            new CatalogProvider(
                EngineRuntimeComposition.NiuTransDefinition.Id,
                "NiuTrans",
                "NMT · cloud · China",
                EngineRuntimeComposition.NiuTransDefinition.CredentialReferences[0],
                DeclarativeRestProvider.CreateBinding(
                    EngineRuntimeComposition.NiuTransDefinition,
                    EngineRuntimeComposition.NiuTransDefinition.CredentialReferences[0]),
                "Text translation · automatic source detection · optional console glossary and memory"),
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
                "NMT · cloud",
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
                "Translate v2 · automatic source detection · glossary support"),
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
                "NMT · cloud · China",
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
                "General text translation · credentials in Windows Credential Manager"),
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
                "NMT · cloud · China",
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
                "TranslateGeneral · optional game context · RAM credentials recommended"),
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
                "NMT · cloud",
                EngineRuntimeComposition.AzureTranslationOptions.CredentialReference,
                AzureTranslatorProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.AzureTranslationOptions),
                "Translator v3 global endpoint · API key in Windows Credential Manager"),
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
                "NMT · cloud · China",
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
                "Text translation v3 · game domain · source-language auto detection"),
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
                "NMT · cloud",
                EngineRuntimeComposition.GoogleTranslationTokenOptions.CredentialReference,
                GoogleServiceAccountTokenSource.CreateCredentialBinding(
                    EngineRuntimeComposition.GoogleTranslationTokenOptions),
                "Translation v3 · service-account JSON · project ID read from the credential"),
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
            "LLM · cloud",
            "OpenAI endpoint · streaming · key in Windows Credential Manager"),
        OpenAiCompatible(
            EngineRuntimeComposition.DeepSeekOptions,
            "DeepSeek",
            "LLM · cloud · China",
            "OpenAI-compatible streaming · game context enabled"),
        OpenAiCompatible(
            EngineRuntimeComposition.QwenOptions,
            "Qwen / Model Studio",
            "LLM · cloud · China",
            "Alibaba Model Studio · OpenAI-compatible streaming"),
        OpenAiCompatible(
            EngineRuntimeComposition.QianfanOptions,
            "Baidu Qianfan",
            "LLM · cloud · China",
            "ERNIE · OpenAI-compatible streaming"),
        Translation(
            new CatalogProvider(
                "llm.anthropic",
                "Anthropic Claude",
                "LLM · cloud",
                EngineRuntimeComposition.AnthropicOptions.CredentialReference,
                AnthropicTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.AnthropicOptions),
                "Claude Messages API · streaming · game context enabled"),
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
                "LLM · cloud",
                EngineRuntimeComposition.GeminiOptions.CredentialReference,
                GeminiTranslationProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.GeminiOptions),
                "Gemini streaming API · game context enabled"),
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
                "OCR · cloud",
                EngineRuntimeComposition.GoogleVisionOptions.CredentialReference,
                GoogleVisionOcrProvider.CreateCredentialBinding(
                    EngineRuntimeComposition.GoogleVisionOptions),
                "Cloud OCR · explicit per-region consent required")
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
                "OCR · cloud · China",
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
                "Baidu general OCR · explicit per-region consent required")
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
                "OCR · cloud · China",
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
                "Tencent GeneralBasicOCR · explicit per-region consent required")
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

    private static BuiltInProviderSpec OpenAiCompatible(
        OpenAiCompatibleOptions options,
        string displayName,
        string kind,
        string detail) =>
        Translation(
            new CatalogProvider(
                options.ProviderId,
                displayName,
                kind,
                options.CredentialReference,
                OpenAiCompatibleProvider.CreateCredentialBinding(options),
                detail),
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
            "OCR · cloud",
            [credential],
            "Image Analysis 4.0 Read · resource endpoint and explicit per-region consent required")
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
