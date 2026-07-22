using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation.Rest;

public sealed class DeclarativeRestProvider : ITranslationProvider
{
    private readonly DeclarativeRestAdapterDefinition _definition;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentialStore;
    private readonly ProxyPolicy _proxyPolicy;

    public DeclarativeRestProvider(
        DeclarativeRestAdapterDefinition definition,
        HttpClient httpClient,
        IBoundCredentialStore credentialStore,
        ProxyPolicy proxyPolicy = ProxyPolicy.System)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentialStore);
        _definition = definition;
        _httpClient = httpClient;
        _credentialStore = credentialStore;
        _proxyPolicy = proxyPolicy;
    }

    public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderWireEvent> events = await ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        foreach (ProviderWireEvent item in events) yield return item;
    }

    private async Task<IReadOnlyList<ProviderWireEvent>> ExecuteAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(
            _definition.ResponseLimits.TimeoutMilliseconds,
            request.Timeout.TotalMilliseconds)));
        HttpRequestMessage message;
        try
        {
            message = await CreateRequestAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (CredentialBindingException)
        {
            return [new ProviderWireFailure("provider.credentialReconfirmationRequired", false)];
        }
        catch (CredentialMissingException)
        {
            return [new ProviderWireFailure("provider.credentialMissing", false)];
        }
        using (message)
        {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [new ProviderWireCancelled("provider.cancelled")];
        }
        catch (OperationCanceledException)
        {
            return [new ProviderWireFailure("provider.deadline", true)];
        }
        catch (HttpRequestException)
        {
            return [new ProviderWireFailure("provider.network", true)];
        }

        using (response)
        {
            if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response))
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            int statusCode = (int)response.StatusCode;
            if (statusCode is 301 or 302 or 303 or 307 or 308)
            {
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            }
            if (!response.IsSuccessStatusCode)
            {
                if (_definition.StatusMappings.TryGetValue(statusCode, out RestStatusMapping? mapping))
                    return [new ProviderWireFailure(mapping.ErrorCode, mapping.Retryable)];
                return [new ProviderWireFailure(
                    MapStatus(response.StatusCode),
                    response.StatusCode is HttpStatusCode.RequestTimeout or
                        HttpStatusCode.TooManyRequests or
                        HttpStatusCode.InternalServerError or
                        HttpStatusCode.BadGateway or
                        HttpStatusCode.ServiceUnavailable or
                        HttpStatusCode.GatewayTimeout)];
            }
            int headerBytes = Encoding.UTF8.GetByteCount(response.Headers.ToString()) +
                Encoding.UTF8.GetByteCount(response.Content.Headers.ToString());
            if (headerBytes > _definition.ResponseLimits.MaximumHeaderBytes)
                return [new ProviderWireFailure("provider.headerLimit", false)];
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength > _definition.ResponseLimits.MaximumCompressedBytes)
            {
                return [new ProviderWireFailure("provider.responseLimit", false)];
            }
            byte[] bytes;
            try
            {
                bytes = await ReadResponseBytesAsync(response.Content, deadline.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return [new ProviderWireFailure("provider.responseLimit", false)];
            }
            catch (NotSupportedException)
            {
                return [new ProviderWireFailure("provider.compressionUnsupported", false)];
            }
            catch (RestIdleTimeoutException)
            {
                return [new ProviderWireFailure("provider.idleTimeout", true)];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return [new ProviderWireCancelled("provider.cancelled")];
            }
            catch (OperationCanceledException)
            {
                return [new ProviderWireFailure("provider.deadline", true)];
            }

            if (_definition.ResponseFormat == RestResponseFormat.ServerSentEvents)
                return ParseSse(bytes, request);
            try
            {
                using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    MaxDepth = _definition.ResponseLimits.MaximumJsonDepth,
                });
                if (!TrySelectString(
                        document.RootElement,
                        _definition.ResponseTextJsonPointer,
                        out string? translated) ||
                    string.IsNullOrEmpty(translated))
                {
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
                }
                return [new ProviderDelta(1, translated), new ProviderDone(1, new ProviderUsage(
                    request.SourceText.Length,
                    translated.Length,
                    request.CostReservation.BillingUnit))];
            }
            catch (JsonException)
            {
                return [new ProviderWireFailure("provider.malformedJson", false)];
            }
        }
        }
    }

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        HttpMethod method = _definition.Method == RestHttpMethod.Get ? HttpMethod.Get : HttpMethod.Post;
        var message = new HttpRequestMessage(method, _definition.Endpoint);
        foreach ((string name, string template) in _definition.Headers)
        {
            string value = await ExpandAsync(template, request, rawValues: true, cancellationToken).ConfigureAwait(false);
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Dispose();
                throw new InvalidOperationException($"Header '{name}' cannot be applied.");
            }
        }
        if (_definition.BodyTemplate is not null)
        {
            string body = await ExpandAsync(_definition.BodyTemplate, request, rawValues: false, cancellationToken)
                .ConfigureAwait(false);
            string mediaType = _definition.BodyFormat == RestBodyFormat.JsonUtf8
                ? "application/json"
                : "application/x-www-form-urlencoded";
            message.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            _definition.ResponseFormat == RestResponseFormat.Json ? "application/json" : "text/event-stream"));
        return message;
    }

    private async ValueTask<string> ExpandAsync(
        string template,
        TranslationRequest request,
        bool rawValues,
        CancellationToken cancellationToken)
    {
        string Encode(string value) => rawValues
            ? value
            : _definition.BodyFormat == RestBodyFormat.JsonUtf8
                ? JsonEncodedText.Encode(value).ToString()
                : Uri.EscapeDataString(value);
        string result = template
            .Replace("{{sourceText}}", Encode(request.SourceText), StringComparison.Ordinal)
            .Replace("{{sourceLanguage}}", Encode(request.SourceLanguage), StringComparison.Ordinal)
            .Replace("{{targetLanguage}}", Encode(request.TargetLanguage), StringComparison.Ordinal)
            .Replace("{{gameName}}", Encode(request.Context.GameName ?? string.Empty), StringComparison.Ordinal)
            .Replace("{{gameDescription}}", Encode(request.Context.GameDescription ?? string.Empty), StringComparison.Ordinal)
            .Replace("{{context}}", Encode(BuildContext(request.Context)), StringComparison.Ordinal)
            .Replace("{{glossary}}", Encode(BuildGlossary(request.Glossary)), StringComparison.Ordinal);
        foreach (string reference in _definition.CredentialReferences)
        {
            string? secret = await _credentialStore.ReadAsync(
                reference,
                CreateBinding(reference),
                cancellationToken)
                .ConfigureAwait(false);
            if (secret is null)
            {
                throw new CredentialMissingException(reference);
            }
            result = result.Replace(
                "{{credential:" + reference + "}}",
                rawValues ? secret : Encode(secret),
                StringComparison.Ordinal);
        }
        return result;
    }

    public CredentialBinding CreateBinding(string reference) =>
        CreateBinding(_definition, reference, _proxyPolicy);

    /// <summary>
    /// The exact binding this provider uses to read <paramref name="reference"/> for
    /// <paramref name="definition"/>. Configuration UIs must write secrets with this same
    /// binding or every read fails the origin check.
    /// </summary>
    public static CredentialBinding CreateBinding(
        DeclarativeRestAdapterDefinition definition,
        string reference,
        ProxyPolicy proxyPolicy = ProxyPolicy.System)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        string authTemplate = string.Join("\n", definition.Headers
            .Where(item => item.Value.Contains("{{credential:" + reference + "}}", StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key + ":" + item.Value));
        if (definition.BodyTemplate?.Contains("{{credential:" + reference + "}}", StringComparison.Ordinal) == true)
            authTemplate += "\nbody:" + definition.BodyTemplate;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authTemplate)));
        int port = definition.Endpoint.IsDefaultPort ? 443 : definition.Endpoint.Port;
        return new CredentialBinding(
            definition.Id,
            reference,
            definition.Endpoint.Scheme,
            definition.Endpoint.IdnHost,
            port,
            "template-sha256-" + digest,
            proxyPolicy);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        int idleTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await ReadWithIdleTimeoutAsync(
                source,
                buffer,
                idleTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Response exceeds the configured limit.");
            }
            destination.Write(buffer, 0, read);
        }
    }

    private async Task<byte[]> ReadResponseBytesAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        byte[] compressed = await ReadBoundedAsync(
            content,
            _definition.ResponseLimits.MaximumCompressedBytes,
            _definition.ResponseLimits.IdleTimeoutMilliseconds,
            cancellationToken).ConfigureAwait(false);
        if (content.Headers.ContentEncoding.Count == 0)
        {
            if (compressed.Length > _definition.ResponseLimits.MaximumDecompressedBytes)
                throw new InvalidDataException("Response exceeds the decompressed size limit.");
            return compressed;
        }
        if (content.Headers.ContentEncoding.Count != 1)
            throw new NotSupportedException("Nested content encodings are not supported.");
        using var input = new MemoryStream(compressed, writable: false);
        using Stream decompressor = content.Headers.ContentEncoding.Single().ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress, leaveOpen: false),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false),
            "br" => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false),
            _ => throw new NotSupportedException("Content encoding is not supported."),
        };
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await decompressor.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > _definition.ResponseLimits.MaximumDecompressedBytes)
                throw new InvalidDataException("Response exceeds the decompressed size limit.");
            output.Write(buffer, 0, read);
        }
    }

    private IReadOnlyList<ProviderWireEvent> ParseSse(byte[] bytes, TranslationRequest request)
    {
        string payload;
        try { payload = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return [new ProviderWireFailure("provider.invalidUtf8", false)]; }
        var events = new List<ProviderWireEvent>();
        long sequence = 0;
        int cumulativeCharacters = 0;
        var data = new StringBuilder();
        foreach (string rawLine in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (Encoding.UTF8.GetByteCount(rawLine) > _definition.ResponseLimits.MaximumSseEventBytes)
                return [new ProviderWireFailure("provider.sseLimit", false)];
            if (rawLine.Length == 0)
            {
                if (data.Length == 0) continue;
                string eventData = data.ToString();
                data.Clear();
                if (eventData == _definition.SseDoneMarker)
                {
                    events.Add(new ProviderDone(sequence, new ProviderUsage(
                        request.SourceText.Length, cumulativeCharacters, request.CostReservation.BillingUnit)));
                    return events;
                }
                try
                {
                    using JsonDocument document = JsonDocument.Parse(eventData, new JsonDocumentOptions
                    {
                        MaxDepth = _definition.ResponseLimits.MaximumJsonDepth,
                    });
                    if (!TrySelectString(document.RootElement, _definition.ResponseTextJsonPointer, out string? text) ||
                        text is null) return [new ProviderWireFailure("provider.malformedSse", false)];
                    cumulativeCharacters += text.Length;
                    if (cumulativeCharacters > Math.Min(
                            request.MaximumOutputCharacters,
                            _definition.ResponseLimits.MaximumCumulativeCharacters))
                        return [new ProviderWireFailure("provider.outputLimit", false)];
                    events.Add(new ProviderDelta(++sequence, text));
                }
                catch (JsonException)
                {
                    return [new ProviderWireFailure("provider.malformedSse", false)];
                }
                continue;
            }
            if (rawLine.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(rawLine[5..].TrimStart());
            }
        }
        return [new ProviderWireFailure("provider.streamingDisconnect", true)];
    }

    private static bool TrySelectString(JsonElement root, string pointer, out string? value)
    {
        JsonElement current = root;
        foreach (string rawSegment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out JsonElement property))
            {
                current = property;
                continue;
            }
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out int index) &&
                index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }
            value = null;
            return false;
        }
        value = current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        return value is not null;
    }

    private static string BuildContext(TranslationContext context) => string.Join("\n", new[]
    {
        context.GameName,
        context.GameDescription,
        context.Scene,
        context.Speaker,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string BuildGlossary(IEnumerable<GlossaryEntry> glossary) =>
        string.Join("\n", glossary.Select(item => $"{item.Source}={item.Target}"));

    private static string MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "provider.http400",
        HttpStatusCode.Unauthorized => "provider.http401",
        HttpStatusCode.Forbidden => "provider.http403",
        HttpStatusCode.TooManyRequests => "provider.http429",
        _ when (int)status >= 500 => "provider.http5xx",
        _ => "provider.httpError",
    };

    private sealed class CredentialMissingException(string reference)
        : Exception($"Credential reference '{reference}' was not found.");

    private static async ValueTask<int> ReadWithIdleTimeoutAsync(
        Stream source,
        Memory<byte> buffer,
        int idleTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeoutMilliseconds);
        try
        {
            return await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RestIdleTimeoutException();
        }
    }

    private sealed class RestIdleTimeoutException : IOException { }
}
