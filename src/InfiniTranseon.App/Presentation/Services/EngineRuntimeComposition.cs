using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Translation;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Builds the real EngineHost runtime for a resolved profile binding: the shipped provider
/// registry (DeepL declarative REST and the OpenAI-compatible endpoint; the local model worker
/// is not shipped in M0 and is deliberately NOT registered, so a profile referencing it fails
/// with the explicit <c>provider.unknown</c> code), the backend assembler, and the facade over
/// the launcher/restart machinery. Provider definitions live here as the single source of
/// truth so <see cref="ProviderCatalog"/> writes credentials with exactly the binding the
/// providers read them with.
/// </summary>
public static class EngineRuntimeComposition
{
    // Deliberate, visible configuration defaults — not hidden fallbacks. Every value is a
    // deployment decision documented here and pinned so the credential bindings stay stable.
    public const string OpenAiDefaultModel = "gpt-4o-mini";
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
    public const int MaximumOutputCharacters = 8_192;
    public const int MaximumOutputTokens = 4_096;

    /// <summary>DeepL Pro endpoint definition shared by runtime and credential catalog.</summary>
    public static DeclarativeRestAdapterDefinition DeepLDefinition { get; } =
        BuiltInProviderDefinitions.DeepL(freeEndpoint: false);

    /// <summary>OpenAI-compatible options shared by runtime and credential catalog.</summary>
    public static OpenAiCompatibleOptions OpenAiOptions { get; } =
        BuiltInProviderDefinitions.OpenAi(OpenAiDefaultModel, "llm.openai");

    // Shared client: providers set per-request headers on HttpRequestMessage, never on the client.
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

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

    public static ProviderRegistry BuildProviderRegistry(IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new ProviderRegistry(
        [
            new ProviderRegistration(
                ProviderDescriptor.Online(
                    DeepLDefinition.Id, ProviderKind.Translation, "deepl-v2"),
                () => new DeclarativeRestProvider(DeepLDefinition, SharedHttpClient, credentials)),
            new ProviderRegistration(
                ProviderDescriptor.Online(
                    OpenAiOptions.ProviderId, ProviderKind.LargeLanguageModel, OpenAiOptions.Model),
                () => new OpenAiCompatibleProvider(OpenAiOptions, SharedHttpClient, credentials)),
        ]);
    }

    /// <summary>Creates a one-shot engine runtime for a launch of the given profile binding.</summary>
    public static IEngineRuntime CreateEngine(
        RuntimeProfileBinding binding,
        IBoundCredentialStore credentials,
        IRuntimeTranslationRecordSink? historySink)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(credentials);
        var providers = new OnlineProviderService(
            BuildProviderRegistry(credentials), new ProviderServiceLimits());
        EngineRuntimeBackendFactory backendFactory = EngineRuntimeBackendAssembler.CreateFactory(
            new EngineRuntimeBackendOptions(binding, providers, CommandTimeout, Stabilizer)
            {
                HistorySink = historySink,
            });
        return EngineRuntimeService.CreateForLaunch(
            EngineHostLocator.Locate(),
            HandshakeTimeout,
            backendFactory,
            RestartPolicy);
    }
}
