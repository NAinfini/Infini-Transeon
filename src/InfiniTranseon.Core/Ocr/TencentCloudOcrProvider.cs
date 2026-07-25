using System.Net;
using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Ocr;

public sealed record TencentCloudOcrOptions(
    Uri Endpoint,
    string? Region,
    string SecretIdReference,
    string SecretKeyReference,
    string? SessionTokenReference,
    ProxyPolicy ProxyPolicy,
    string LanguageType = "auto",
    int MaximumResponseBytes = 2 * 1024 * 1024,
    Func<DateTimeOffset>? Clock = null);

public sealed class TencentCloudOcrProvider : IOcrProvider
{
    private const string ProviderId = "ocr.tencent-cloud";
    private readonly TencentCloudOcrOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public TencentCloudOcrProvider(
        TencentCloudOcrOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretIdReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretKeyReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LanguageType);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
            options.Endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment))
            throw new ArgumentException("Tencent OCR endpoint must be an HTTPS origin.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding(string purpose) =>
        CreateCredentialBinding(_options, purpose);

    public static CredentialBinding CreateCredentialBinding(
        TencentCloudOcrOptions options,
        string purpose)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (purpose is not ("secret-id" or "secret-key" or "session-token"))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        return new CredentialBinding(
            ProviderId,
            purpose,
            "https",
            options.Endpoint.IdnHost,
            options.Endpoint.IsDefaultPort ? 443 : options.Endpoint.Port,
            "tc3-hmac-sha256",
            options.ProxyPolicy);
    }

    public async ValueTask<OcrResultSnapshot> RecognizeAsync(
        CloudOcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EncodedCrop.IsEmpty || request.EncodedCrop.Length > 7_500_000)
            throw new OcrRoutingException("ocr.tencent.requestTooLarge", "OCR crop exceeds Tencent's request limit.");
        string? secretId;
        string? secretKey;
        string? sessionToken = null;
        try
        {
            secretId = await _credentials.ReadAsync(
                _options.SecretIdReference,
                CreateCredentialBinding("secret-id"),
                cancellationToken).ConfigureAwait(false);
            secretKey = await _credentials.ReadAsync(
                _options.SecretKeyReference,
                CreateCredentialBinding("secret-key"),
                cancellationToken).ConfigureAwait(false);
            if (_options.SessionTokenReference is not null)
                sessionToken = await _credentials.ReadAsync(
                    _options.SessionTokenReference,
                    CreateCredentialBinding("session-token"),
                    cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialBindingException exception)
        {
            throw new OcrRoutingException(
                "ocr.credentialReconfirmationRequired", exception.Message);
        }
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey) ||
            _options.SessionTokenReference is not null && string.IsNullOrWhiteSpace(sessionToken))
            throw new OcrRoutingException("ocr.credentialMissing", "Tencent OCR credentials are missing.");

        byte[] body = CreateBody(request.EncodedCrop.Span, _options.LanguageType);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        TencentCloudSigner.Sign(
            message,
            body,
            secretId,
            secretKey,
            "ocr",
            "GeneralBasicOCR",
            "2018-11-19",
            _options.Region,
            sessionToken,
            (_options.Clock ?? (() => DateTimeOffset.UtcNow))());

        using HttpResponseMessage response = await SendAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response) ||
            (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            throw new OcrRoutingException("ocr.redirectRejected", "OCR redirects are not allowed.");
        if (!response.IsSuccessStatusCode)
            throw new OcrRoutingException(
                $"ocr.tencent.http{(int)response.StatusCode}",
                "Tencent OCR returned an HTTP failure.");
        byte[] responseBody;
        try
        {
            responseBody = await ProviderResponseReader.ReadBoundedAsync(
                response.Content, _options.MaximumResponseBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new OcrRoutingException("ocr.responseLimit", exception.Message);
        }
        return ParseResponse(responseBody, request);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new OcrRoutingException("ocr.network", exception.Message);
        }
    }

    private static byte[] CreateBody(ReadOnlySpan<byte> crop, string language)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteBase64String("ImageBase64", crop);
            writer.WriteString("LanguageType", language);
            writer.WriteBoolean("IsWords", false);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static OcrResultSnapshot ParseResponse(
        ReadOnlyMemory<byte> body,
        CloudOcrProviderRequest request)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                body, new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("Response", out JsonElement root))
                throw new OcrRoutingException("ocr.malformedResponse", "Tencent OCR response is missing Response.");
            if (root.TryGetProperty("Error", out JsonElement error))
            {
                string code = error.TryGetProperty("Code", out JsonElement value)
                    ? value.GetString() ?? "Unknown"
                    : "Unknown";
                throw new OcrRoutingException("ocr.tencent." + code, "Tencent OCR rejected the request.");
            }
            if (!root.TryGetProperty("TextDetections", out JsonElement detections) ||
                detections.ValueKind != JsonValueKind.Array)
                throw new OcrRoutingException("ocr.malformedResponse", "Tencent OCR detections are missing.");
            var lines = new List<TextLine>();
            foreach (JsonElement detection in detections.EnumerateArray())
            {
                if (lines.Count >= RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult)
                    throw new OcrRoutingException("ocr.provider.tooManyLines", "Tencent OCR returned too many lines.");
                if (!detection.TryGetProperty("DetectedText", out JsonElement textElement) ||
                    textElement.ValueKind != JsonValueKind.String ||
                    !detection.TryGetProperty("ItemPolygon", out JsonElement polygon))
                    throw new OcrRoutingException("ocr.malformedResponse", "Tencent OCR line is malformed.");
                string text = textElement.GetString()!;
                if (text.Length > RuntimeCapabilities.VersionOne.MaxSourceChars)
                    throw new OcrRoutingException("ocr.provider.outputLimit", "Tencent OCR line is too long.");
                double confidence = detection.TryGetProperty("Confidence", out JsonElement confidenceElement) &&
                    confidenceElement.TryGetDouble(out double rawConfidence)
                        ? Math.Clamp(rawConfidence / 100.0, 0, 1)
                        : 0;
                lines.Add(new TextLine(
                    text,
                    NormalizePolygon(polygon, request.PixelWidth, request.PixelHeight),
                    confidence));
            }
            return new OcrResultSnapshot(
                request.ExecutionToken,
                lines,
                ProviderId,
                "2018-11-19",
                true,
                null);
        }
        catch (JsonException exception)
        {
            throw new OcrRoutingException("ocr.malformedJson", exception.Message);
        }
    }

    private static NormalizedRect NormalizePolygon(JsonElement polygon, int width, int height)
    {
        if (!polygon.TryGetProperty("X", out JsonElement xValue) || !xValue.TryGetDouble(out double x) ||
            !polygon.TryGetProperty("Y", out JsonElement yValue) || !yValue.TryGetDouble(out double y) ||
            !polygon.TryGetProperty("Width", out JsonElement widthValue) || !widthValue.TryGetDouble(out double lineWidth) ||
            !polygon.TryGetProperty("Height", out JsonElement heightValue) || !heightValue.TryGetDouble(out double lineHeight))
            throw new OcrRoutingException("ocr.malformedResponse", "Tencent OCR polygon is malformed.");
        double left = Math.Clamp(x / width, 0, 1);
        double top = Math.Clamp(y / height, 0, 1);
        double right = Math.Clamp((x + lineWidth) / width, left, 1);
        double bottom = Math.Clamp((y + lineHeight) / height, top, 1);
        if (right <= left || bottom <= top)
            throw new OcrRoutingException("ocr.malformedResponse", "Tencent OCR polygon is empty.");
        return new NormalizedRect(left, top, right - left, bottom - top);
    }
}
