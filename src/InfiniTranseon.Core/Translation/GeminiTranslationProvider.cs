using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record GeminiProviderOptions(
    Uri Endpoint,
    string CredentialReference,
    ProxyPolicy ProxyPolicy,
    bool IncludeGameContext,
    bool IncludeRecentHistory,
    TimeSpan? IdleTimeout = null,
    int MaximumSseEvents = 4096,
    int MaximumSseLineCharacters = 65_536,
    long MaximumResponseBytes = 4 * 1024 * 1024,
    int MaximumRequestBytes = 1024 * 1024);

public sealed class GeminiTranslationProvider : ITranslationProvider
{
    private const string ProviderId = "llm.gemini";
    private readonly GeminiProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public GeminiTranslationProvider(
        GeminiProviderOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialReference);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment) ||
            !options.Endpoint.AbsolutePath.Contains(":streamGenerateContent", StringComparison.Ordinal))
            throw new ArgumentException("Gemini endpoint must be an HTTPS streamGenerateContent URI.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumSseEvents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumSseLineCharacters, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRequestBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding() =>
        CreateCredentialBinding(_options);

    public static CredentialBinding CreateCredentialBinding(GeminiProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CredentialBinding(
            ProviderId,
            "api-key",
            "https",
            options.Endpoint.IdnHost,
            options.Endpoint.IsDefaultPort ? 443 : options.Endpoint.Port,
            "x-goog-api-key",
            options.ProxyPolicy);
    }

    public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SendResult send = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (send.Failure is not null)
        {
            yield return send.Failure;
            yield break;
        }
        using HttpResponseMessage response = send.Response!;
        if (response.Content.Headers.ContentEncoding.Count > 0 ||
            response.Content.Headers.ContentLength is long length &&
            length > _options.MaximumResponseBytes)
        {
            yield return new ProviderWireFailure("provider.responseLimit", false);
            yield break;
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var reader = new BoundedSseReader(
            stream,
            _options.MaximumSseLineCharacters,
            _options.MaximumResponseBytes,
            _options.IdleTimeout ?? TimeSpan.FromSeconds(30));
        long sequence = 0;
        int outputCharacters = 0;
        ProviderUsage usage = ProviderUsage.None;
        for (int eventCount = 1; ; eventCount++)
        {
            if (eventCount > _options.MaximumSseEvents)
            {
                yield return new ProviderWireFailure("provider.sseLimit", false);
                yield break;
            }
            ProviderSseEventResult sse = await reader.ReadEventAsync(cancellationToken)
                .ConfigureAwait(false);
            if (sse.Failure is not null)
            {
                yield return sse.Failure;
                yield break;
            }
            if (sse.EndOfStream)
            {
                yield return new ProviderWireFailure("provider.streamingDisconnect", true);
                yield break;
            }
            ParseResult parsed = ParseChunk(sse.Data!);
            if (parsed.Failure is not null)
            {
                yield return parsed.Failure;
                yield break;
            }
            if (parsed.Usage is not null) usage = parsed.Usage;
            foreach (string text in parsed.TextParts)
            {
                if (string.IsNullOrEmpty(text)) continue;
                outputCharacters = checked(outputCharacters + text.Length);
                if (outputCharacters > request.MaximumOutputCharacters)
                {
                    yield return new ProviderWireFailure("provider.outputLimit", false);
                    yield break;
                }
                sequence++;
                yield return new ProviderDelta(sequence, text);
            }
            if (parsed.Done)
            {
                yield return new ProviderDone(sequence, usage);
                yield break;
            }
        }
    }

    private async Task<SendResult> SendAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        string? key;
        try
        {
            key = await _credentials.ReadAsync(
                _options.CredentialReference,
                CreateCredentialBinding(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialBindingException)
        {
            return new SendResult(null,
                new ProviderWireFailure("provider.credentialReconfirmationRequired", false));
        }
        if (string.IsNullOrWhiteSpace(key))
            return new SendResult(null, new ProviderWireFailure("provider.credentialMissing", false));
        byte[] body = CreateBody(request);
        if (body.Length > _options.MaximumRequestBytes)
            return new SendResult(null, new ProviderWireFailure("provider.requestLimit", false));
        var endpoint = new UriBuilder(_options.Endpoint) { Query = "alt=sse" }.Uri;
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        try
        {
            HttpResponseMessage response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response) ||
                status is 301 or 302 or 303 or 307 or 308)
            {
                response.Dispose();
                return new SendResult(null,
                    new ProviderWireFailure("provider.redirectRejected", false));
            }
            if (!response.IsSuccessStatusCode)
            {
                bool retryable = response.StatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout;
                response.Dispose();
                return new SendResult(null,
                    new ProviderWireFailure($"provider.gemini.http{status}", retryable));
            }
            return new SendResult(response, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SendResult(null, new ProviderWireCancelled("provider.cancelled"));
        }
        catch (HttpRequestException)
        {
            return new SendResult(null, new ProviderWireFailure("provider.network", true));
        }
    }

    private byte[] CreateBody(TranslationRequest request)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("systemInstruction");
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("text", TranslationPromptPayload.CreateSystemPrompt(request));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("contents");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("text", TranslationPromptPayload.Create(
                request, _options.IncludeGameContext, _options.IncludeRecentHistory));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("generationConfig");
            writer.WriteNumber("maxOutputTokens", request.MaximumOutputTokens);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static ParseResult ParseChunk(string data)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                data, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out _))
                return new ParseResult([], null, false,
                    new ProviderWireFailure("provider.gemini.error", true));
            var textParts = new List<string>();
            bool done = false;
            ProviderWireEvent? finishFailure = null;
            if (root.TryGetProperty("candidates", out JsonElement candidates) &&
                candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement candidate in candidates.EnumerateArray())
                {
                    if (candidate.TryGetProperty("content", out JsonElement content) &&
                        content.TryGetProperty("parts", out JsonElement parts) &&
                        parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement part in parts.EnumerateArray())
                            if (part.TryGetProperty("text", out JsonElement text) &&
                                text.ValueKind == JsonValueKind.String)
                                textParts.Add(text.GetString()!);
                    }
                    if (candidate.TryGetProperty("finishReason", out JsonElement finish) &&
                        finish.ValueKind == JsonValueKind.String)
                    {
                        string reason = finish.GetString()!;
                        if (reason == "STOP") done = true;
                        else if (!string.IsNullOrEmpty(reason))
                            finishFailure = new ProviderWireFailure(
                                "provider.gemini.finish." + reason, false);
                    }
                }
            }
            ProviderUsage? usage = null;
            if (root.TryGetProperty("usageMetadata", out JsonElement usageElement))
            {
                long input = usageElement.TryGetProperty("promptTokenCount", out JsonElement inputElement) &&
                    inputElement.TryGetInt64(out long inputValue) ? inputValue : 0;
                long output = usageElement.TryGetProperty("candidatesTokenCount", out JsonElement outputElement) &&
                    outputElement.TryGetInt64(out long outputValue) ? outputValue : 0;
                usage = new ProviderUsage(input, output, "tokens");
            }
            return new ParseResult(textParts, usage, done, finishFailure);
        }
        catch (JsonException)
        {
            return new ParseResult([], null, false,
                new ProviderWireFailure("provider.malformedSse", false));
        }
    }

    private sealed record SendResult(HttpResponseMessage? Response, ProviderWireEvent? Failure);
    private sealed record ParseResult(
        IReadOnlyList<string> TextParts,
        ProviderUsage? Usage,
        bool Done,
        ProviderWireEvent? Failure);
}
