using System.Net;
using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Ocr;

public sealed record AzureVisionOcrOptions(
    Uri Endpoint,
    string CredentialReference,
    ProxyPolicy ProxyPolicy,
    int MaximumRequestBytes = 20 * 1024 * 1024,
    int MaximumResponseBytes = 2 * 1024 * 1024);

public sealed class AzureVisionOcrProvider : IOcrProvider
{
    private const string ProviderId = "ocr.azure-ai-vision";
    private readonly AzureVisionOcrOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public AzureVisionOcrProvider(
        AzureVisionOcrOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialReference);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) || options.Endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(options.Endpoint.Query) || !string.IsNullOrEmpty(options.Endpoint.Fragment))
            throw new ArgumentException("Azure Vision endpoint must be an HTTPS origin.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRequestBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding() => new(
        ProviderId,
        "api-key",
        "https",
        _options.Endpoint.IdnHost,
        _options.Endpoint.IsDefaultPort ? 443 : _options.Endpoint.Port,
        "ocp-apim-subscription-key",
        _options.ProxyPolicy);

    public async ValueTask<OcrResultSnapshot> RecognizeAsync(
        CloudOcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EncodedCrop.IsEmpty || request.EncodedCrop.Length > _options.MaximumRequestBytes)
            throw new OcrRoutingException("ocr.azure.requestTooLarge", "OCR crop exceeds the Azure request limit.");
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
            throw new OcrRoutingException("ocr.credentialMissing", "Azure Vision credential is missing.");

        Uri endpoint = new(_options.Endpoint,
            "computervision/imageanalysis:analyze?api-version=2024-02-01&features=read");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ReadOnlyMemoryContent(request.EncodedCrop),
        };
        message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
        message.Content.Headers.ContentType = new(request.MimeType);
        using HttpResponseMessage response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response))
            throw new OcrRoutingException("ocr.redirectRejected", "OCR redirects are not allowed.");
        RejectFailure(response);
        byte[] body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return Parse(body, request);
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

    private static void RejectFailure(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            throw new OcrRoutingException("ocr.redirectRejected", "OCR redirects are not allowed.");
        if (!response.IsSuccessStatusCode)
            throw new OcrRoutingException(
                $"ocr.azure.http{(int)response.StatusCode}", "Azure Vision returned an HTTP failure.");
    }

    private async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProviderResponseReader.ReadBoundedAsync(
                response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new OcrRoutingException("ocr.responseLimit", exception.Message);
        }
    }

    private static OcrResultSnapshot Parse(byte[] body, CloudOcrProviderRequest request)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("readResult", out JsonElement read) ||
                !read.TryGetProperty("blocks", out JsonElement blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
                throw new OcrRoutingException("ocr.malformedResponse", "Azure Vision read result is missing.");
            var lines = new List<TextLine>();
            foreach (JsonElement block in blocks.EnumerateArray())
            {
                if (!block.TryGetProperty("lines", out JsonElement blockLines) ||
                    blockLines.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement line in blockLines.EnumerateArray())
                {
                    CloudOcrResponseParser.EnsureCapacity(lines.Count, "Azure Vision");
                    string text = CloudOcrResponseParser.RequiredText(line, "text", "Azure Vision");
                    NormalizedRect bounds = CloudOcrResponseParser.Polygon(
                        line.GetProperty("boundingPolygon"), request.PixelWidth, request.PixelHeight);
                    double confidence = AverageWordConfidence(line);
                    lines.Add(new TextLine(text, bounds, confidence));
                }
            }
            string version = root.TryGetProperty("modelVersion", out JsonElement versionElement) &&
                versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()!
                    : "2024-02-01";
            return new OcrResultSnapshot(request.ExecutionToken, lines, ProviderId, version, true, null);
        }
        catch (JsonException exception)
        {
            throw new OcrRoutingException("ocr.malformedJson", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw new OcrRoutingException("ocr.malformedResponse", exception.Message);
        }
    }

    private static double AverageWordConfidence(JsonElement line)
    {
        if (!line.TryGetProperty("words", out JsonElement words) || words.ValueKind != JsonValueKind.Array)
            return 0;
        double total = 0;
        int count = 0;
        foreach (JsonElement word in words.EnumerateArray())
        {
            if (word.TryGetProperty("confidence", out JsonElement value) && value.TryGetDouble(out double confidence))
            {
                total += Math.Clamp(confidence, 0, 1);
                count++;
            }
        }
        return count == 0 ? 0 : total / count;
    }
}
