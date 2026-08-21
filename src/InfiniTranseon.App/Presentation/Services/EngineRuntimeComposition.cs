using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Settings;
using InfiniTranseon.Core.Translation;
using InfiniTranseon.Core.Translation.Local;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// A locally installed model, wired as a translation provider and held open. The worker process and
/// the usage lease live exactly as long as the caller keeps this: dropping it lets the model be
/// updated or removed again.
/// </summary>
public sealed class LocalTranslationProviderLease : IDisposable
{
    private readonly LocalWorkerSessionManager _sessions;
    private readonly IDisposable _usage;
    private readonly Task _warm;

    public LocalTranslationProviderLease(
        ProviderRegistration registration,
        LocalWorkerSessionManager sessions,
        IDisposable usage)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(usage);
        Registration = registration;
        _sessions = sessions;
        _usage = usage;
        _warm = WarmAsync(sessions);
    }

    public ProviderRegistration Registration { get; }

    /// <summary>
    /// This lease exists because something is about to translate with the model, so the worker is
    /// launched either way; starting it here moves the multi-gigabyte model read off the first
    /// translation instead of leaving the first line of a session seconds behind every later one.
    /// A failure is not reported here because nothing has been translated yet: the first
    /// translation repeats the same launch and surfaces the real error code through the provider.
    /// </summary>
    private static async Task WarmAsync(LocalWorkerSessionManager sessions)
    {
        try
        {
            await sessions.WarmAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _warm.GetAwaiter().GetResult();
            _sessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _usage.Dispose();
        }
    }
}

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

    /// <summary>Provider ids for models that run on this machine. The provider list writes these
    /// ids, profiles store them, and the runtime resolves them, so the shape lives in one place.
    /// </summary>
    public const string LocalTranslationProviderIdPrefix = "translation.local.";

    public static string LocalTranslationProviderId(string modelId) =>
        LocalTranslationProviderIdPrefix + modelId;

    /// <summary>DeepL Pro endpoint definition shared by runtime and credential catalog.</summary>
    public static DeclarativeRestAdapterDefinition DeepLDefinition { get; } =
        BuiltInProviderDefinitions.DeepL(freeEndpoint: false);

    /// <summary>DeepL API Free endpoint. A free key is rejected by the Pro host, so it is a separate
    /// provider with its own id and credential slot rather than a hidden endpoint switch.</summary>
    public static DeclarativeRestAdapterDefinition DeepLFreeDefinition { get; } =
        BuiltInProviderDefinitions.DeepL(freeEndpoint: true);

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
        ProxyPolicy.System);

    /// <summary>Baidu accurate OCR with automatic language detection and position data.</summary>
    public static BaiduOcrOptions BaiduOcrOptions { get; } = new(
        new Uri("https://aip.baidubce.com/oauth/2.0/token"),
        new Uri("https://aip.baidubce.com/rest/2.0/ocr/v1/accurate"),
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
        IReadOnlyList<ProviderRegistration>? additionalRegistrations = null,
        bool strictOffline = false)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        List<ProviderRegistration> registrations = strictOffline
            ? []
            : new List<ProviderRegistration>(
                BuiltInProviderSpecs.CreateTranslationRegistrations(credentials));
        if (!strictOffline)
        {
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
        }
        registrations.AddRange((additionalRegistrations ?? []).Where(registration =>
            !strictOffline || !registration.Descriptor.RequiresNetwork));

        return new ProviderRegistry(registrations);
    }

    public static IReadOnlyList<OcrProviderRegistration> BuildCloudOcrRegistrations(
        IBoundCredentialStore credentials,
        IReadOnlyDictionary<string, string>? providerEndpoints = null,
        bool strictOffline = false)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (strictOffline) return [];
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
        AppDataOptions? appData = null,
        OcrBackendPreference ocrBackend = OcrBackendPreference.Automatic)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        LocalProviderResources local = CreateLocalProviders(binding, localModels, appData);
        bool strictOffline = binding.Profile.StrictOffline;
        var paddleCatalog = appData is null
            ? null
            : new ManagedPaddleOcrModelCatalog(appData.ModelDirectory);
        var ocrAvailability = new WindowsOcrLanguageAvailability(
            installedModelLanguages: () => paddleCatalog?.InstalledLanguageTags ?? []);
        OnlineProviderService? providers = null;
        try
        {
            providers = new OnlineProviderService(
                BuildProviderRegistry(
                    credentials,
                    customAdapters,
                    local.Registrations,
                    strictOffline),
                new ProviderServiceLimits());
            EngineRuntimeBackendFactory backendFactory = EngineRuntimeBackendAssembler.CreateFactory(
                new EngineRuntimeBackendOptions(binding, providers, CommandTimeout, Stabilizer)
                {
                    HistorySink = historySink,
                    CloudOcrProviders = BuildCloudOcrRegistrations(
                        credentials,
                        providerEndpoints,
                        strictOffline),
                    TranslationMemory = new TranslationMemory(
                        new TranslationMemoryOptions(
                            PersistentEnabled: UsesPersistentTranslationMemory(binding.Profile),
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
                    OcrBackendResolver = language => OcrRuntimeBackendResolver.Resolve(
                        ocrBackend,
                        ocrAvailability,
                        language),
                    LocalOcrProbeFactory = paddleCatalog is null || localModels is null
                        ? null
                        : () => new PaddleOcrProbe(
                            new ManagedPaddleOcrModelCatalog(appData!.ModelDirectory),
                            modelSet => AcquirePaddleOcrUsage(localModels, modelSet)),
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

    private static IDisposable AcquirePaddleOcrUsage(
        LocalModelManagementService localModels,
        PaddleOcrModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(localModels);
        ArgumentNullException.ThrowIfNull(modelSet);
        var identities = new HashSet<(string ModelId, string Version)>();
        foreach (string path in new[]
        {
            modelSet.DetectionModelPath,
            modelSet.RecognitionModelPath,
            modelSet.ClassificationModelPath,
        }.OfType<string>())
        {
            DirectoryInfo slot = Directory.GetParent(path)
                ?? throw new InvalidDataException("OCR model path has no package slot.");
            DirectoryInfo version = slot.Parent
                ?? throw new InvalidDataException("OCR model path has no package version.");
            DirectoryInfo model = version.Parent
                ?? throw new InvalidDataException("OCR model path has no package identity.");
            identities.Add((model.Name, version.Name));
        }

        var leases = new List<IDisposable>(identities.Count);
        try
        {
            foreach ((string modelId, string version) in identities)
                leases.Add(localModels.AcquireUsage(modelId, version));
            return new OwnedModelLeases(leases);
        }
        catch (LocalModelRemovalInProgressException exception)
        {
            foreach (IDisposable lease in leases) lease.Dispose();
            throw new PaddleOcrUnavailableException(
                PaddleOcrUnavailableException.ModelBusyCode,
                exception.Message);
        }
        catch
        {
            foreach (IDisposable lease in leases) lease.Dispose();
            throw;
        }
    }

    private sealed class OwnedModelLeases(IReadOnlyList<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            for (int index = leases.Count - 1; index >= 0; index--)
                leases[index].Dispose();
        }
    }

    internal static HashSet<string> ReferencedLocalProviderIds(ProfileDocument profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return EnabledTranslationChannels(profile)
            .SelectMany(channel => new[] { channel.InitialProviderId }
                .Concat(channel.FallbackProviderIds)
                .Concat(channel.RefinementSteps.Select(step => step.ProviderId)))
            .Where(providerId => providerId.StartsWith(
                LocalTranslationProviderIdPrefix,
                StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    internal static bool UsesPersistentTranslationMemory(ProfileDocument profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return EnabledTranslationChannels(profile).Any(channel => channel.PersistentCacheEnabled);
    }

    private static IEnumerable<ProfileTranslationChannel> EnabledTranslationChannels(
        ProfileDocument profile) => profile.Targets
        .Where(target => target.Enabled)
        .SelectMany(target => target.Regions.Concat(target.RemainingAreaRegion is null
            ? []
            : [target.RemainingAreaRegion]))
        .Where(region => region.Enabled && region.TranslationEnabled)
        .SelectMany(region => region.TranslationChannels)
        .Where(channel => channel.Enabled);

    private static LocalProviderResources CreateLocalProviders(
        RuntimeProfileBinding binding,
        LocalModelManagementService? localModels,
        AppDataOptions? appData)
    {
        if (localModels is null || appData is null)
            return new LocalProviderResources();

        HashSet<string> referenced = ReferencedLocalProviderIds(binding.Profile);
        if (referenced.Count == 0)
            return new LocalProviderResources();

        var resources = new LocalProviderResources();
        try
        {
            foreach (string providerId in referenced)
            {
                if (CreateLocalTranslationProvider(providerId, localModels, appData) is { } lease)
                    resources.Add(lease);
            }
            return resources;
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Serves <paramref name="providerId"/> from a model installed on this machine, or returns null
    /// when no installed package answers to that id. Disposing releases the worker and the usage
    /// lease that keeps the package from being removed underneath it.
    ///
    /// Both the running engine and the wizard's translation test resolve local translators here, so
    /// a model the provider list offers is a model both of them can actually run.
    /// </summary>
    public static LocalTranslationProviderLease? CreateLocalTranslationProvider(
        string providerId,
        LocalModelManagementService localModels,
        AppDataOptions appData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(localModels);
        ArgumentNullException.ThrowIfNull(appData);

        LocalModelPackageView? model = localModels.GetSnapshot().Packages.FirstOrDefault(package =>
            package.State == LocalModelInstallState.Installed &&
            string.Equals(
                LocalTranslationProviderId(package.ModelId),
                providerId,
                StringComparison.Ordinal));
        if (model is null)
            return null;

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
        // Pinned rather than idle-retired: the model this worker holds is a multi-gigabyte file, and
        // reloading it costs seconds that land inside a single translation attempt's timeout. A
        // player who reads one line, plays for a minute and reads the next would pay that reload
        // every time. The lease below is what bounds the worker's life instead — it is disposed with
        // the runtime session that resolved this provider, so the memory is released when the user
        // stops translating rather than between two sentences.
        var sessions = new LocalWorkerSessionManager(
            async cancellationToken => await LocalWorkerSandboxLauncher.LaunchAsync(
                sandbox,
                cancellationToken).ConfigureAwait(false),
            new LocalWorkerSessionManagerOptions(
                IdleTimeout: TimeSpan.FromSeconds(30),
                PinWarm: true));
        try
        {
            return new LocalTranslationProviderLease(
                new ProviderRegistration(
                    new LocalTranslationProvider(providerId, model.ModelId, sessions).Descriptor,
                    () => new LocalTranslationProvider(providerId, model.ModelId, sessions)),
                sessions,
                localModels.AcquireUsage(model.ModelId, model.Version));
        }
        catch
        {
            sessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        private readonly List<LocalTranslationProviderLease> _leases = [];

        public List<ProviderRegistration> Registrations { get; } = [];

        public void Add(LocalTranslationProviderLease lease)
        {
            Registrations.Add(lease.Registration);
            _leases.Add(lease);
        }

        public void Dispose()
        {
            foreach (LocalTranslationProviderLease lease in _leases)
                lease.Dispose();
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
