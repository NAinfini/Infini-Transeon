using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record AnthropicProviderOptions(
    Uri Endpoint,
    string Model,
    string CredentialReference,
    ProxyPolicy ProxyPolicy,
    bool IncludeGameContext,
    bool IncludeRecentHistory,
    string ApiVersion = "2023-06-01",
    TimeSpan? IdleTimeout = null,
    int MaximumSseEvents = 4096,
    int MaximumSseLineCharacters = 65_536,
    long MaximumResponseBytes = 4 * 1024 * 1024,
    int MaximumRequestBytes = 1024 * 1024);

public sealed class AnthropicTranslationProvider : ITranslationProvider
{
    private const string ProviderId = "llm.anthropic";
    private readonly AnthropicProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public AnthropicTranslationProvider(
        AnthropicProviderOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiVersion);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment))
            throw new ArgumentException("Anthropic endpoint must be an absolute HTTPS URI.", nameof(options));
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

    public static CredentialBinding CreateCredentialBinding(AnthropicProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CredentialBinding(
            ProviderId,
            "api-key",
            "https",
            options.Endpoint.IdnHost,
            options.Endpoint.IsDefaultPort ? 443 : options.Endpoint.Port,
            "x-api-key",
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
            response.Content.Headers.ContentLength is long contentLength &&
            contentLength > _options.MaximumResponseBytes)
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
        long inputTokens = 0;
        long outputTokens = 0;
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
            ParseResult parsed = ParseEvent(sse.Data!);
            if (parsed.Failure is not null)
            {
                yield return parsed.Failure;
                yield break;
            }
            if (parsed.InputTokens is long parsedInput) inputTokens = parsedInput;
            if (parsed.OutputTokens is long parsedOutput) outputTokens = parsedOutput;
            if (!string.IsNullOrEmpty(parsed.Text))
            {
                outputCharacters = checked(outputCharacters + parsed.Text.Length);
                if (outputCharacters > request.MaximumOutputCharacters)
                {
                    yield return new ProviderWireFailure("provider.outputLimit", false);
                    yield break;
                }
                sequence++;
                yield return new ProviderDelta(sequence, parsed.Text);
            }
            if (parsed.Done)
            {
                yield return new ProviderDone(
                    sequence,
                    new ProviderUsage(inputTokens, outputTokens, "tokens"));
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
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("x-api-key", key);
        message.Headers.TryAddWithoutValidation("anthropic-version", _options.ApiVersion);
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
                    new ProviderWireFailure($"provider.anthropic.http{status}", retryable));
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
            writer.WriteString("model", _options.Model);
            writer.WriteNumber("max_tokens", request.MaximumOutputTokens);
            writer.WriteBoolean("stream", true);
            writer.WriteString("system", TranslationPromptPayload.CreateSystemPrompt(request));
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", TranslationPromptPayload.Create(
                request, _options.IncludeGameContext, _options.IncludeRecentHistory));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static ParseResult ParseEvent(string data)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                data, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
                return new ParseResult(null, null, null, false,
                    new ProviderWireFailure("provider.malformedSse", false));
            string type = typeElement.GetString()!;
            if (type == "error")
                return new ParseResult(null, null, null, false,
                    new ProviderWireFailure("provider.anthropic.error", true));
            string? text = null;
            long? input = null;
            long? output = null;
            if (type == "content_block_delta" &&
                root.TryGetProperty("delta", out JsonElement delta) &&
                delta.TryGetProperty("type", out JsonElement deltaType) &&
                deltaType.GetString() == "text_delta" &&
                delta.TryGetProperty("text", out JsonElement textElement) &&
                textElement.ValueKind == JsonValueKind.String)
                text = textElement.GetString();
            if (type == "message_start" &&
                root.TryGetProperty("message", out JsonElement message) &&
                message.TryGetProperty("usage", out JsonElement startUsage) &&
                startUsage.TryGetProperty("input_tokens", out JsonElement inputElement) &&
                inputElement.TryGetInt64(out long inputValue))
                input = inputValue;
            if (type == "message_delta" &&
                root.TryGetProperty("usage", out JsonElement deltaUsage) &&
                deltaUsage.TryGetProperty("output_tokens", out JsonElement outputElement) &&
                outputElement.TryGetInt64(out long outputValue))
                output = outputValue;
            return new ParseResult(text, input, output, type == "message_stop", null);
        }
        catch (JsonException)
        {
            return new ParseResult(null, null, null, false,
                new ProviderWireFailure("provider.malformedSse", false));
        }
    }

    private sealed record SendResult(HttpResponseMessage? Response, ProviderWireEvent? Failure);
    private sealed record ParseResult(
        string? Text,
        long? InputTokens,
        long? OutputTokens,
        bool Done,
        ProviderWireEvent? Failure);
}
