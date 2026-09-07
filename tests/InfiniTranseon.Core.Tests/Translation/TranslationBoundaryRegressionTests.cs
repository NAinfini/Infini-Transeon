using System.Net;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Probes;
using InfiniTranseon.Core.Translation;
using InfiniTranseon.Core.Translation.Rest;

namespace InfiniTranseon.Core.Tests.Translation;

public sealed class TranslationBoundaryRegressionTests
{
    private const string ProviderId = "test.provider";

    [Fact]
    public async Task DeclarativeJsonExpandsOnlyPlaceholdersFromTheOriginalTemplate()
    {
        const string secret = "actual-secret";
        string? body = null;
        string? header = null;
        var handler = new RecordingHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            header = request.Headers.GetValues("X-Test").Single();
            return JsonResponse();
        });
        var definition = new DeclarativeRestAdapterDefinition(
            1,
            ProviderId,
            "Test",
            new Uri("https://api.example.test/translate"),
            RestHttpMethod.Post,
            new Dictionary<string, string>
            {
                ["X-Test"] = "{{sourceText}}|{{credential:api-key}}",
            },
            "{\"source\":\"{{sourceText}}\",\"sourceLanguage\":\"{{sourceLanguage}}\"," +
            "\"targetLanguage\":\"{{targetLanguage}}\",\"game\":\"{{gameName}}\"," +
            "\"description\":\"{{gameDescription}}\",\"context\":\"{{context}}\"," +
            "\"glossary\":\"{{glossary}}\",\"credential\":\"{{credential:api-key}}\"}",
            "/translation",
            null,
            ["api-key"]);
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var provider = new DeclarativeRestProvider(
            definition, new HttpClient(handler), credentials);
        await credentials.WriteAsync(
            "api-key", secret, provider.CreateBinding("api-key"), CancellationToken.None);
        TranslationRequest request = CreateRequest(
            "{{credential:api-key}} {{targetLanguage}} \"quoted\"",
            "{{credential:api-key}}",
            "{{sourceText}}",
            new TranslationContext(
                "{{credential:api-key}}",
                "{{targetLanguage}}",
                "{{sourceLanguage}}",
                "{{glossary}}",
                [],
                []),
            [new GlossaryEntry("{{credential:api-key}}", "{{sourceText}}")]);

        await CollectWireAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(request.SourceText + "|" + secret, header);
        using JsonDocument document = JsonDocument.Parse(body!);
        JsonElement root = document.RootElement;
        Assert.Equal(request.SourceText, root.GetProperty("source").GetString());
        Assert.Equal(request.SourceLanguage, root.GetProperty("sourceLanguage").GetString());
        Assert.Equal(request.TargetLanguage, root.GetProperty("targetLanguage").GetString());
        Assert.Equal(request.Context.GameName, root.GetProperty("game").GetString());
        Assert.Equal(request.Context.GameDescription, root.GetProperty("description").GetString());
        Assert.Equal(
            "{{credential:api-key}}\n{{targetLanguage}}\n{{sourceLanguage}}\n{{glossary}}",
            root.GetProperty("context").GetString());
        Assert.Equal(
            "{{credential:api-key}}={{sourceText}}",
            root.GetProperty("glossary").GetString());
        Assert.Equal(secret, root.GetProperty("credential").GetString());
    }

    [Fact]
    public async Task DeclarativeFormKeepsEncodingWhileExpandingOnce()
    {
        const string secret = "secret & value";
        string? body = null;
        var handler = new RecordingHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse();
        });
        var definition = new DeclarativeRestAdapterDefinition(
            1,
            ProviderId,
            "Test",
            new Uri("https://api.example.test/translate"),
            RestHttpMethod.Post,
            new Dictionary<string, string>(),
            "text={{sourceText}}&context={{context}}&glossary={{glossary}}&key={{credential:api-key}}",
            "/translation",
            null,
            ["api-key"],
            RestBodyFormat.FormUrlEncodedUtf8);
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var provider = new DeclarativeRestProvider(
            definition, new HttpClient(handler), credentials);
        await credentials.WriteAsync(
            "api-key", secret, provider.CreateBinding("api-key"), CancellationToken.None);
        TranslationRequest request = CreateRequest(
            "hello & {{credential:api-key}}",
            "en",
            "zh-Hans",
            new TranslationContext("game & {{sourceText}}", null, null, null, [], []),
            [new GlossaryEntry("A&B", "{{credential:api-key}}")]);

        await CollectWireAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(
            "text=" + Uri.EscapeDataString(request.SourceText) +
            "&context=" + Uri.EscapeDataString(request.Context.GameName!) +
            "&glossary=" + Uri.EscapeDataString("A&B={{credential:api-key}}") +
            "&key=" + Uri.EscapeDataString(secret),
            body);
    }

    [Fact]
    public async Task OpenAiCompatibleRejectsLengthFinishReason()
    {
        const string response =
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
            "data: [DONE]\n\n";
        OpenAiCompatibleProvider provider = await CreateOpenAiProviderAsync(response);

        IReadOnlyList<ProviderWireEvent> events = await CollectWireAsync(
            provider.StreamAsync(CreateRequest(), CancellationToken.None));

        Assert.Collection(
            events,
            item => Assert.Equal("partial", Assert.IsType<ProviderDelta>(item).Text),
            item => Assert.Equal(
                "provider.openai.finish.length",
                Assert.IsType<ProviderWireFailure>(item).ErrorCode));
    }

    [Fact]
    public async Task OpenAiCompatibleAcceptsStopFinishReason()
    {
        const string response =
            "data: {\"choices\":[{\"delta\":{\"content\":\"complete\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        OpenAiCompatibleProvider provider = await CreateOpenAiProviderAsync(response);

        IReadOnlyList<ProviderWireEvent> events = await CollectWireAsync(
            provider.StreamAsync(CreateRequest(), CancellationToken.None));

        Assert.Collection(
            events,
            item => Assert.Equal("complete", Assert.IsType<ProviderDelta>(item).Text),
            item => Assert.IsType<ProviderDone>(item));
    }

    [Fact]
    public async Task OpenAiCompatibleReportsDisconnectWithoutDoneMarker()
    {
        const string response =
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n";
        OpenAiCompatibleProvider provider = await CreateOpenAiProviderAsync(response);

        IReadOnlyList<ProviderWireEvent> events = await CollectWireAsync(
            provider.StreamAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(
            "provider.streamingDisconnect",
            Assert.IsType<ProviderWireFailure>(events[^1]).ErrorCode);
    }

    [Fact]
    public async Task TranslationProbeRejectsOpenAiDoneWithoutText()
    {
        OpenAiCompatibleProvider provider = await CreateOpenAiProviderAsync("data: [DONE]\n\n");
        var probe = new TranslationProbe(Registry(() => provider), ProviderId);

        TranslationProbeResult result = await probe.TranslateAsync(
            new TranslationProbeRequest("hello", "en", "zh-Hans", null),
            CancellationToken.None);

        Assert.Equal(TranslationProbe.NoOutputCode, result.ErrorCode);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task AnthropicRejectsMaxTokensStopReason()
    {
        const string response =
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"},\"usage\":{\"output_tokens\":1}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        AnthropicTranslationProvider provider = await CreateAnthropicProviderAsync(response);

        IReadOnlyList<ProviderWireEvent> events = await CollectWireAsync(
            provider.StreamAsync(CreateRequest(), CancellationToken.None));

        Assert.Collection(
            events,
            item => Assert.Equal("partial", Assert.IsType<ProviderDelta>(item).Text),
            item => Assert.Equal(
                "provider.anthropic.finish.maxTokens",
                Assert.IsType<ProviderWireFailure>(item).ErrorCode));
    }

    [Fact]
    public async Task OnlineProviderServiceRejectsWhitespaceOnlyCompletion()
    {
        var provider = new StubProvider(
            new ProviderDelta(1, "  \t"),
            new ProviderDone(1, ProviderUsage.None));
        using var service = new OnlineProviderService(
            Registry(() => provider),
            new ProviderServiceLimits());

        IReadOnlyList<ProviderEvent> events = await CollectAsync(
            service.StreamAsync(ProviderId, CreateRequest(), CancellationToken.None));

        Assert.Equal("provider.emptyOutput",
            Assert.IsType<ProviderFailed>(events[^1]).ErrorCode);
    }

    [Fact]
    public async Task TranslationProbeUsesValidatedEventsAndDisposesProvider()
    {
        var provider = new DisposableStubProvider(
            new ProviderDelta(2, "out-of-order"),
            new ProviderDone(2, ProviderUsage.None));
        var probe = new TranslationProbe(Registry(() => provider), ProviderId);

        TranslationProbeResult result = await probe.TranslateAsync(
            new TranslationProbeRequest("hello", "en", "zh-Hans", null),
            CancellationToken.None);

        Assert.Equal("provider.deltaGap", result.ErrorCode);
        Assert.True(provider.Disposed);
    }

    [Fact]
    public async Task TranslationProbeReportsItsTimeoutAsADeadline()
    {
        var probe = new TranslationProbe(
            Registry(() => new BlockingProvider()),
            ProviderId,
            TimeSpan.FromMilliseconds(50));

        TranslationProbeResult result = await probe.TranslateAsync(
            new TranslationProbeRequest("hello", "en", "zh-Hans", null),
            CancellationToken.None);

        Assert.Equal("provider.deadline", result.ErrorCode);
    }

    private static async Task<OpenAiCompatibleProvider> CreateOpenAiProviderAsync(string response)
    {
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var provider = new OpenAiCompatibleProvider(
            new OpenAiCompatibleOptions(
                ProviderId,
                new Uri("https://api.example.test/v1/chat/completions"),
                "test-model",
                "api-key",
                ProxyPolicy.System,
                IncludeGameContext: false,
                IncludeRecentHistory: false),
            new HttpClient(new RecordingHandler(_ => SseResponse(response))),
            credentials);
        await credentials.WriteAsync(
            "api-key", "secret", provider.CreateCredentialBinding(), CancellationToken.None);
        return provider;
    }

    private static async Task<AnthropicTranslationProvider> CreateAnthropicProviderAsync(
        string response)
    {
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var provider = new AnthropicTranslationProvider(
            new AnthropicProviderOptions(
                new Uri("https://api.example.test/v1/messages"),
                "test-model",
                "api-key",
                ProxyPolicy.System,
                IncludeGameContext: false,
                IncludeRecentHistory: false),
            new HttpClient(new RecordingHandler(_ => SseResponse(response))),
            credentials);
        await credentials.WriteAsync(
            "api-key", "secret", provider.CreateCredentialBinding(), CancellationToken.None);
        return provider;
    }

    private static HttpResponseMessage JsonResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"translation\":\"ok\"}", Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage SseResponse(string response) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(response, Encoding.UTF8, "text/event-stream"),
    };

    private static ProviderRegistry Registry(Func<ITranslationProvider> factory) => new(
        [new ProviderRegistration(
            ProviderDescriptor.Online(ProviderId, ProviderKind.Translation),
            factory)]);

    private static TranslationRequest CreateRequest(
        string sourceText = "hello",
        string sourceLanguage = "en",
        string targetLanguage = "zh-Hans",
        TranslationContext? context = null,
        IReadOnlyList<GlossaryEntry>? glossary = null)
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.FullTarget,
            new TextTrackId(Guid.NewGuid()),
            1,
            1);
        var channel = new ChannelExecutionToken(
            source,
            new TranslationChannelId(Guid.NewGuid()),
            Guid.NewGuid(),
            Guid.NewGuid());
        var execution = new StageExecutionToken(channel, Guid.NewGuid(), 1, 1, 1);
        return new TranslationRequest(
            sourceText,
            sourceLanguage,
            targetLanguage,
            context ?? new TranslationContext(null, null, null, null, [], []),
            glossary ?? [],
            execution,
            TimeSpan.FromSeconds(2),
            Guid.NewGuid().ToString("N"),
            8_192,
            4_096,
            new ProviderCostReservation("characters", 100_000, null, null),
            StrictOffline: false);
    }

    private static async Task<IReadOnlyList<ProviderWireEvent>> CollectWireAsync(
        IAsyncEnumerable<ProviderWireEvent> source)
    {
        var result = new List<ProviderWireEvent>();
        await foreach (ProviderWireEvent item in source) result.Add(item);
        return result;
    }

    private static async Task<IReadOnlyList<ProviderEvent>> CollectAsync(
        IAsyncEnumerable<ProviderEvent> source)
    {
        var result = new List<ProviderEvent>();
        await foreach (ProviderEvent item in source) result.Add(item);
        return result;
    }

    private sealed class StubProvider(params ProviderWireEvent[] events) : ITranslationProvider
    {
        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            foreach (ProviderWireEvent item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class DisposableStubProvider(params ProviderWireEvent[] events)
        : ITranslationProvider, IDisposable
    {
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            foreach (ProviderWireEvent item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class BlockingProvider : ITranslationProvider
    {
        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
