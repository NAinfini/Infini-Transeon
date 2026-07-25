using System.Net;
using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Ocr;

public sealed record GoogleVisionOcrOptions(
    Uri Endpoint,
    string CredentialReference,
    ProxyPolicy ProxyPolicy,
    IReadOnlyList<string> LanguageHints,
    int MaximumRequestBytes = 10 * 1024 * 1024,
    int MaximumResponseBytes = 4 * 1024 * 1024);

public sealed class GoogleVisionOcrProvider : IOcrProvider
{
    private const string ProviderId = "ocr.google-cloud-vision";
    private readonly GoogleVisionOcrOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public GoogleVisionOcrProvider(
        GoogleVisionOcrOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options.LanguageHints);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialReference);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) || !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment))
            throw new ArgumentException("Google Vision endpoint must be an HTTPS URI.", nameof(options));
        if (options.LanguageHints.Count > 16 || options.LanguageHints.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Google Vision language hints are invalid.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRequestBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding() => CreateCredentialBinding(_options);

    public static CredentialBinding CreateCredentialBinding(GoogleVisionOcrOptions options)
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

    public async ValueTask<OcrResultSnapshot> RecognizeAsync(
        CloudOcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EncodedCrop.IsEmpty || request.EncodedCrop.Length > _options.MaximumRequestBytes)
            throw new OcrRoutingException("ocr.google.requestTooLarge", "OCR crop exceeds the Google request limit.");
        string key;
        try
        {
            key = await _credentials.ReadAsync(
                _options.CredentialReference, CreateCredentialBinding(), cancellationToken)
                .ConfigureAwait(false) ?? "";
        }
        catch (CredentialBindingException exception)
        {
            throw new OcrRoutingException("ocr.credentialReconfirmationRequired", exception.Message);
        }
        if (string.IsNullOrWhiteSpace(key))
            throw new OcrRoutingException("ocr.credentialMissing", "Google Vision credential is missing.");

        byte[] body = CreateBody(request.EncodedCrop.Span);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using HttpResponseMessage response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response) ||
            (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            throw new OcrRoutingException("ocr.redirectRejected", "OCR redirects are not allowed.");
        if (!response.IsSuccessStatusCode)
            throw new OcrRoutingException(
                $"ocr.google.http{(int)response.StatusCode}", "Google Vision returned an HTTP failure.");
        byte[] responseBody;
        try
        {
            responseBody = await ProviderResponseReader.ReadBoundedAsync(
                response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new OcrRoutingException("ocr.responseLimit", exception.Message);
        }
        return Parse(responseBody, request);
    }

    private byte[] CreateBody(ReadOnlySpan<byte> crop)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("requests");
            writer.WriteStartObject();
            writer.WriteStartObject("image");
            writer.WriteBase64String("content", crop);
            writer.WriteEndObject();
            writer.WriteStartArray("features");
            writer.WriteStartObject();
            writer.WriteString("type", "DOCUMENT_TEXT_DETECTION");
            writer.WriteNumber("maxResults", RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult);
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (_options.LanguageHints.Count > 0)
            {
                writer.WriteStartObject("imageContext");
                writer.WriteStartArray("languageHints");
                foreach (string hint in _options.LanguageHints) writer.WriteStringValue(hint);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new OcrRoutingException("ocr.network", exception.Message);
        }
    }

    private static OcrResultSnapshot Parse(byte[] body, CloudOcrProviderRequest request)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            if (!document.RootElement.TryGetProperty("responses", out JsonElement responses) ||
                responses.ValueKind != JsonValueKind.Array || responses.GetArrayLength() != 1)
                throw new OcrRoutingException("ocr.malformedResponse", "Google Vision response is missing.");
            JsonElement response = responses[0];
            if (response.TryGetProperty("error", out JsonElement error))
            {
                string code = error.TryGetProperty("code", out JsonElement codeElement)
                    ? codeElement.ToString() : "Unknown";
                throw new OcrRoutingException("ocr.google." + code, "Google Vision rejected the request.");
            }
            var lines = new List<TextLine>();
            if (response.TryGetProperty("fullTextAnnotation", out JsonElement annotation) &&
                annotation.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement page in pages.EnumerateArray())
                foreach (JsonElement block in RequiredArray(page, "blocks"))
                foreach (JsonElement paragraph in RequiredArray(block, "paragraphs"))
                {
                    CloudOcrResponseParser.EnsureCapacity(lines.Count, "Google Vision");
                    if (!paragraph.TryGetProperty("boundingBox", out JsonElement boundingBox) ||
                        !boundingBox.TryGetProperty("vertices", out JsonElement vertices))
                        throw new OcrRoutingException("ocr.malformedResponse", "Google Vision bounds are missing.");
                    string text = CloudOcrResponseParser.GoogleParagraphText(paragraph);
                    double confidence = paragraph.TryGetProperty("confidence", out JsonElement confidenceElement) &&
                        confidenceElement.TryGetDouble(out double raw) ? Math.Clamp(raw, 0, 1) : 0;
                    lines.Add(new TextLine(
                        text,
                        CloudOcrResponseParser.Polygon(vertices, request.PixelWidth, request.PixelHeight),
                        confidence));
                }
            }
            return new OcrResultSnapshot(request.ExecutionToken, lines, ProviderId, "v1", true, null);
        }
        catch (JsonException exception)
        {
            throw new OcrRoutingException("ocr.malformedJson", exception.Message);
        }
    }

    private static JsonElement.ArrayEnumerator RequiredArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new OcrRoutingException("ocr.malformedResponse", "Google Vision hierarchy is malformed.");
        return value.EnumerateArray();
    }
}
