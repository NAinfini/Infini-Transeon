using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Settings;
using InfiniTranseon.Core.Translation;
using InfiniTranseon.Core.Translation.Local;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Builds the real EngineHost runtime for a resolved profile binding: the shipped provider
/// registry, cloud OCR router, backend assembler, and the facade over the launcher/restart
/// machinery. <see cref="BuiltInProviderSpecs"/> projects its single provider definition list
/// into both this runtime and <see cref="ProviderCatalog"/>.
/// </summary>
public static class EngineRuntimeComposition
{
    // Deliberate, visible configuration defaults — not hidden fallbacks. Every value is a
    // deployment decision documented here and pinned so the credential bindings stay stable.
    public const string OpenAiDefaultModel = "gpt-4o-mini";
    public const string DeepSeekDefaultModel = "deepseek-v4-flash";
    public const string QwenDefaultModel = "qwen3.7-plus";
    public const string QianfanDefaultModel = "ernie-5.0";
    public const string AnthropicDefaultModel = "claude-sonnet-5";
    public const string GeminiDefaultModel = "gemini-3.6-flash";
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
    public const int MaximumOutputCharacters = 8_192;
    public const int MaximumOutputTokens = 4_096;

    /// <summary>DeepL Pro endpoint definition shared by runtime and credential catalog.</summary>
    public static DeclarativeRestAdapterDefinition DeepLDefinition { get; } =
        BuiltInProviderDefinitions.DeepL(freeEndpoint: false);

    /// <summary>NiuTrans HTTPS text translation endpoint with automatic source detection.</summary>
    public static DeclarativeRestAdapterDefinition NiuTransDefinition { get; } =
        BuiltInProviderDefinitions.NiuTrans();

    /// <summary>Yandex Cloud Translate v2 with source-language auto detection.</summary>
    public static DeclarativeRestAdapterDefinition YandexDefinition { get; } =
        BuiltInProviderDefinitions.Yandex();

    /// <summary>Baidu general text translation endpoint and bound APP ID/secret references.</summary>
    public static BaiduTranslationOptions BaiduTranslationOptions { get; } = new(
        new Uri("https://fanyi-api.baidu.com/api/trans/vip/translate"),
        "translation.baidu.app-id",
        "translation.baidu.secret",
        ProxyPolicy.System);

    /// <summary>Alibaba Cloud TranslateGeneral endpoint and long-lived RAM credentials.</summary>
    public static AlibabaTranslationOptions AlibabaTranslationOptions { get; } = new(
        new Uri("https://mt.aliyuncs.com/"),
        "translation.alibaba.access-key-id",
        "translation.alibaba.access-key-secret",
        SecurityTokenReference: null,
        ProxyPolicy: ProxyPolicy.System,
        IncludeContext: true);

    /// <summary>Azure Translator v3 global endpoint; regionless single-service keys are supported.</summary>
    public static AzureTranslatorOptions AzureTranslationOptions { get; } = new(
        new Uri("https://api.cognitive.microsofttranslator.com/"),
        "translation.azure-ai.api-key",
        Region: null,
        ProxyPolicy: ProxyPolicy.System);

    /// <summary>Youdao Zhiyun text translation v3 with the game domain model.</summary>
    public static YoudaoTranslationOptions YoudaoTranslationOptions { get; } = new(
        new Uri("https://openapi.youdao.com/api"),
        "translation.youdao.app-key",
        "translation.youdao.app-secret",
        ProxyPolicy.System,
        Domain: "game");

    public static GoogleServiceAccountTokenOptions GoogleTranslationTokenOptions { get; } = new(
        new Uri("https://oauth2.googleapis.com/token"),
        "translation.google-cloud.service-account-json",
        ProxyPolicy.System);

    public static GoogleCloudTranslationOptions GoogleTranslationOptions { get; } = new(
        new Uri(
            "https://translation.googleapis.com/v3/projects/_/locations/global:translateText"),
        ProxyPolicy.System);

    /// <summary>OpenAI-compatible options shared by runtime and credential catalog.</summary>
    public static OpenAiCompatibleOptions OpenAiOptions { get; } =
        BuiltInProviderDefinitions.OpenAi(OpenAiDefaultModel, "llm.openai");

    /// <summary>DeepSeek's official OpenAI-compatible endpoint.</summary>
    public static OpenAiCompatibleOptions DeepSeekOptions { get; } =
        BuiltInProviderDefinitions.DeepSeek(DeepSeekDefaultModel, "llm.deepseek");

    /// <summary>Alibaba Model Studio's OpenAI-compatible public endpoint.</summary>
    public static OpenAiCompatibleOptions QwenOptions { get; } =
        BuiltInProviderDefinitions.QwenModelStudio(QwenDefaultModel, "llm.qwen-model-studio");

    /// <summary>Baidu Qianfan's OpenAI-compatible v2 endpoint.</summary>
    public static OpenAiCompatibleOptions QianfanOptions { get; } =
        BuiltInProviderDefinitions.BaiduQianfan(QianfanDefaultModel, "llm.baidu-qianfan");

    /// <summary>Anthropic Messages streaming endpoint.</summary>
    public static AnthropicProviderOptions AnthropicOptions { get; } = new(
        new Uri("https://api.anthropic.com/v1/messages"),
        AnthropicDefaultModel,
        "llm.anthropic",
        ProxyPolicy.System,
        IncludeGameContext: true,
        IncludeRecentHistory: true);

    /// <summary>Google Gemini streaming content endpoint.</summary>
    public static GeminiProviderOptions GeminiOptions { get; } = new(
        new Uri(
            $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiDefaultModel}:streamGenerateContent"),
        "llm.gemini",
        ProxyPolicy.System,
        IncludeGameContext: true,
        IncludeRecentHistory: true);

    /// <summary>Google Vision REST endpoint used by the cloud OCR adapter.</summary>
    public static GoogleVisionOcrOptions GoogleVisionOptions { get; } = new(
        new Uri("https://vision.googleapis.com/v1/images:annotate"),
        "ocr.google-cloud-vision",
        ProxyPolicy.System,
        LanguageHints: []);

    /// <summary>Baidu general OCR with its OAuth client-credential token endpoint.</summary>
    public static BaiduOcrOptions BaiduOcrOptions { get; } = new(
        new Uri("https://aip.baidubce.com/oauth/2.0/token"),
        new Uri("https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic"),
        "ocr.baidu.client-id",
        "ocr.baidu.client-secret",
        ProxyPolicy.System);

    /// <summary>Tencent Cloud GeneralBasicOCR endpoint.</summary>
    public static TencentCloudOcrOptions TencentOcrOptions { get; } = new(
        new Uri("https://ocr.tencentcloudapi.com/"),
        "ap-guangzhou",
        "ocr.tencent-cloud.secret-id",
        "ocr.tencent-cloud.secret-key",
        SessionTokenReference: null,
        ProxyPolicy: ProxyPolicy.System);

    public static AzureVisionOcrOptions AzureVisionPlaceholderOptions { get; } = new(
        new Uri("https://configuration.invalid/"),
        "ocr.azure-ai-vision.api-key",
        ProxyPolicy.System);

    // App-lifetime clients are partitioned by provider origin and proxy policy. The pool disables
    // redirects/cookies and caps connections and response headers; providers add request headers.
    private static readonly ProviderHttpClientPool HttpClients = new();

    private static readonly RuntimeEngineHostRestartPolicy RestartPolicy = new(
        maxAttempts: 3,
        window: TimeSpan.FromMinutes(5),
        initialDelay: TimeSpan.FromSeconds(1),
        maxDelay: TimeSpan.FromSeconds(30));

    // OCR text must hold for 2 consecutive frames (or 2 s) before translation fires.
    private static readonly TextStabilizerOptions Stabilizer = new(
        StableFrameCount: 2,
        MinimumDelay: TimeSpan.FromMilliseconds(150),
        MaximumWait: TimeSpan.FromSeconds(2));

    public static ProviderRegistry BuildProviderRegistry(
        IBoundCredentialStore credentials,
        IReadOnlyList<DeclarativeRestAdapterDefinition>? customAdapters = null,
        IReadOnlyList<ProviderRegistration>? additionalRegistrations = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var registrations =
            new List<ProviderRegistration>(BuiltInProviderSpecs.CreateTranslationRegistrations(credentials));
        foreach (DeclarativeRestAdapterDefinition definition in customAdapters ?? [])
        {
            registrations.Add(new ProviderRegistration(
                ProviderDescriptor.Online(
                    definition.Id,
                    ProviderKind.Translation,
                    $"custom-rest-v{definition.SchemaVersion}"),
                () => new DeclarativeRestProvider(
                    definition,
                    ClientFor(definition.Id, definition.Endpoint, ProxyPolicy.System),
                    credentials)));
        }
        registrations.AddRange(additionalRegistrations ?? []);

        return new ProviderRegistry(registrations);
    }

    public static IReadOnlyList<OcrProviderRegistration> BuildCloudOcrRegistrations(
        IBoundCredentialStore credentials,
        IReadOnlyDictionary<string, string>? providerEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return BuiltInProviderSpecs.CreateOcrRegistrations(
            credentials,
            providerEndpoints ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>Creates a one-shot engine runtime for a launch of the given profile binding.</summary>
    public static IEngineRuntime CreateEngine(
        RuntimeProfileBinding binding,
        IBoundCredentialStore credentials,
        IRuntimeTranslationRecordSink? historySink,
        string databasePath,
        IReadOnlyList<DeclarativeRestAdapterDefinition>? customAdapters = null,
        AppPerformancePreset performancePreset = AppPerformancePreset.Balanced,
        bool reducedMotion = false,
        IReadOnlyDictionary<string, string>? providerEndpoints = null,
        LocalModelManagementService? localModels = null,
        AppDataOptions? appData = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        LocalProviderResources local = CreateLocalProviders(binding, localModels, appData);
        OnlineProviderService? providers = null;
        try
        {
            providers = new OnlineProviderService(
                BuildProviderRegistry(credentials, customAdapters, local.Registrations),
                new ProviderServiceLimits());
            EngineRuntimeBackendFactory backendFactory = EngineRuntimeBackendAssembler.CreateFactory(
                new EngineRuntimeBackendOptions(binding, providers, CommandTimeout, Stabilizer)
                {
                    HistorySink = historySink,
                    CloudOcrProviders = BuildCloudOcrRegistrations(credentials, providerEndpoints),
                    TranslationMemory = new TranslationMemory(
                        new TranslationMemoryOptions(
                            PersistentEnabled: true,
                            FuzzyEnabled: false),
                        databasePath),
                    Corrections = new CorrectionStore(databasePath),
                    PerformanceSettings = new PerformanceRuntimeSettings
                    {
                        Preset = performancePreset switch
                        {
                            AppPerformancePreset.Eco => PerformancePreset.Eco,
                            AppPerformancePreset.Performance => PerformancePreset.Performance,
                            _ => PerformancePreset.Balanced,
                        },
                    },
                    ReducedMotion = reducedMotion,
                });
            return EngineRuntimeService.CreateForLaunch(
                EngineHostLocator.Locate(),
                HandshakeTimeout,
                backendFactory,
                RestartPolicy,
                lifetimeResource: new EngineProviderResources(providers, local));
        }
        catch
        {
            providers?.Dispose();
            local.Dispose();
            throw;
        }
    }

    private static LocalProviderResources CreateLocalProviders(
        RuntimeProfileBinding binding,
        LocalModelManagementService? localModels,
        AppDataOptions? appData)
    {
        if (localModels is null || appData is null)
            return new LocalProviderResources();

        HashSet<string> referenced = binding.Targets
            .Select(target => target.ProfileTarget)
            .SelectMany(target => target.Regions
                .Concat(target.RemainingAreaRegion is null
                    ? []
                    : [target.RemainingAreaRegion]))
            .Where(region => region.Enabled && region.TranslationEnabled)
            .SelectMany(region => region.TranslationChannels)
            .Where(channel => channel.Enabled)
            .SelectMany(channel => new[] { channel.InitialProviderId }
                .Concat(channel.FallbackProviderIds)
                .Concat(channel.RefinementSteps.Select(step => step.ProviderId)))
            .Where(providerId => providerId.StartsWith(
                "translation.local.",
                StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (referenced.Count == 0)
            return new LocalProviderResources();

        var resources = new LocalProviderResources();
        try
        {
            foreach (LocalModelPackageView model in localModels.GetSnapshot().Packages
                .Where(model => model.State == LocalModelInstallState.Installed))
            {
                string providerId = $"translation.local.{model.ModelId}";
                if (!referenced.Remove(providerId))
                    continue;
                string scratch = Path.Combine(
                    appData.ModelDirectory,
                    ".worker-scratch",
                    $"{model.ModelId}-{model.Version}");
                var sandbox = new LocalWorkerSandboxOptions(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        LocalModelRuntimeAvailability.WorkerExecutableName),
                    WorkerAssemblyPath: null,
                    model.PackageDirectory,
                    scratch,
                    LocalWorkerProtocol.DefaultMaximumCommittedBytes,
                    HandshakeTimeout);
                var sessions = new LocalWorkerSessionManager(
                    async cancellationToken => await LocalWorkerSandboxLauncher.LaunchAsync(
                        sandbox,
                        cancellationToken).ConfigureAwait(false),
                    new LocalWorkerSessionManagerOptions(
                        IdleTimeout: TimeSpan.FromSeconds(30),
                        PinWarm: false));
                IDisposable lease = localModels.AcquireUsage(model.ModelId, model.Version);
                resources.Add(
                    new ProviderRegistration(
                        new LocalTranslationProvider(
                            providerId,
                            model.ModelId,
                            sessions).Descriptor,
                        () => new LocalTranslationProvider(
                            providerId,
                            model.ModelId,
                            sessions)),
                    sessions,
                    lease);
            }
            return resources;
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    private sealed class EngineProviderResources(
        OnlineProviderService providers,
        LocalProviderResources local) : IDisposable
    {
        public void Dispose()
        {
            providers.Dispose();
            local.Dispose();
        }
    }

    private sealed class LocalProviderResources : IDisposable
    {
        private readonly List<LocalWorkerSessionManager> _sessions = [];
        private readonly List<IDisposable> _leases = [];

        public List<ProviderRegistration> Registrations { get; } = [];

        public void Add(
            ProviderRegistration registration,
            LocalWorkerSessionManager sessions,
            IDisposable lease)
        {
            Registrations.Add(registration);
            _sessions.Add(sessions);
            _leases.Add(lease);
        }

        public void Dispose()
        {
            try
            {
                foreach (LocalWorkerSessionManager sessions in _sessions)
                    sessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                foreach (IDisposable lease in _leases)
                    lease.Dispose();
            }
        }
    }

    internal static HttpClient ClientFor(
        string providerId,
        Uri endpoint,
        ProxyPolicy proxyPolicy)
    {
        var origin = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return HttpClients.GetClient(new ProviderHttpOrigin(providerId, origin, proxyPolicy));
    }
}
