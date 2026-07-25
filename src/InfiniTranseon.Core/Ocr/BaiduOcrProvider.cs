using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Ocr;

public sealed record BaiduOcrOptions(
    Uri TokenEndpoint,
    Uri OcrEndpoint,
    string ClientIdReference,
    string ClientSecretReference,
    ProxyPolicy ProxyPolicy,
    bool IncludeLocation = true,
    string LanguageType = "CHN_ENG",
    int MaximumRequestBytes = 4 * 1024 * 1024,
    int MaximumResponseBytes = 2 * 1024 * 1024,
    Func<DateTimeOffset>? Clock = null);

public sealed class BaiduOcrProvider : IOcrProvider, IDisposable
{
    private const string ProviderId = "ocr.baidu";
    private readonly BaiduOcrOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AccessToken? _accessToken;
    private bool _disposed;

    public BaiduOcrProvider(
        BaiduOcrOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ValidateEndpoint(options.TokenEndpoint, nameof(options));
        ValidateEndpoint(options.OcrEndpoint, nameof(options));
        if (!SameOrigin(options.TokenEndpoint, options.OcrEndpoint))
            throw new ArgumentException("Baidu token and OCR endpoints must use the same origin.", nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientIdReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientSecretReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LanguageType);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRequestBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding(string purpose) =>
        CreateCredentialBinding(_options, purpose);

    public static CredentialBinding CreateCredentialBinding(BaiduOcrOptions options, string purpose)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (purpose is not ("client-id" or "client-secret"))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        return new CredentialBinding(
            ProviderId,
            purpose,
            "https",
            options.TokenEndpoint.IdnHost,
            options.TokenEndpoint.IsDefaultPort ? 443 : options.TokenEndpoint.Port,
            "oauth2-client-credentials",
            options.ProxyPolicy);
    }

    public async ValueTask<OcrResultSnapshot> RecognizeAsync(
        CloudOcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.EncodedCrop.IsEmpty || request.EncodedCrop.Length > _options.MaximumRequestBytes)
            throw new OcrRoutingException("ocr.baidu.requestTooLarge", "OCR crop exceeds the Baidu request limit.");
        string accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        Uri endpoint = AddQuery(_options.OcrEndpoint, "access_token", accessToken);
        string image = Convert.ToBase64String(request.EncodedCrop.Span);
        var fields = new Dictionary<string, string>
        {
            ["image"] = image,
            ["language_type"] = _options.LanguageType,
            ["detect_direction"] = "true",
            ["probability"] = "true",
            ["vertexes_location"] = _options.IncludeLocation ? "true" : "false",
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        using HttpResponseMessage response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response))
            throw new OcrRoutingException("ocr.redirectRejected", "OCR redirects are not allowed.");
        RejectFailure(response, "ocr.baidu");
        byte[] body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return Parse(body, request);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accessToken = null;
        _tokenGate.Dispose();
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = (_options.Clock ?? (() => DateTimeOffset.UtcNow))();
        AccessToken? cached = Volatile.Read(ref _accessToken);
        if (cached is not null && now < cached.RefreshAfterUtc) return cached.Value;
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = (_options.Clock ?? (() => DateTimeOffset.UtcNow))();
            cached = _accessToken;
            if (cached is not null && now < cached.RefreshAfterUtc) return cached.Value;
            (string clientId, string clientSecret) = await ReadCredentialsAsync(cancellationToken)
                .ConfigureAwait(false);
            Uri endpoint = AddQuery(
                AddQuery(
                    AddQuery(_options.TokenEndpoint, "grant_type", "client_credentials"),
                    "client_id",
                    clientId),
                "client_secret",
                clientSecret);
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent([]),
            };
            using HttpResponseMessage response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response))
                throw new OcrRoutingException("ocr.redirectRejected", "OCR redirects are not allowed.");
            RejectFailure(response, "ocr.baidu.oauth");
            byte[] body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            try
            {
                using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("error", out JsonElement error))
                    throw new OcrRoutingException(
                        "ocr.baidu.oauth." + (error.GetString() ?? "unknown"), "Baidu rejected OAuth credentials.");
                string value = root.TryGetProperty("access_token", out JsonElement tokenElement) &&
                    tokenElement.ValueKind == JsonValueKind.String
                        ? tokenElement.GetString()!
                        : throw new OcrRoutingException("ocr.malformedResponse", "Baidu OAuth token is missing.");
                long expires = root.TryGetProperty("expires_in", out JsonElement expiresElement) &&
                    expiresElement.TryGetInt64(out long parsed) ? parsed : 0;
                if (string.IsNullOrWhiteSpace(value) || expires < 60)
                    throw new OcrRoutingException("ocr.malformedResponse", "Baidu OAuth token lifetime is invalid.");
                var token = new AccessToken(value, now.AddSeconds(Math.Max(1, expires - 60)));
                Volatile.Write(ref _accessToken, token);
                return token.Value;
            }
            catch (JsonException exception)
            {
                throw new OcrRoutingException("ocr.malformedJson", exception.Message);
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<(string ClientId, string ClientSecret)> ReadCredentialsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string id = await _credentials.ReadAsync(
                _options.ClientIdReference, CreateCredentialBinding("client-id"), cancellationToken)
                .ConfigureAwait(false) ?? "";
            string secret = await _credentials.ReadAsync(
                _options.ClientSecretReference, CreateCredentialBinding("client-secret"), cancellationToken)
                .ConfigureAwait(false) ?? "";
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
                throw new OcrRoutingException("ocr.credentialMissing", "Baidu OCR credentials are missing.");
            return (id, secret);
        }
        catch (CredentialBindingException exception)
        {
            throw new OcrRoutingException("ocr.credentialReconfirmationRequired", exception.Message);
        }
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

    private static void RejectFailure(HttpResponseMessage response, string prefix)
    {
        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            throw new OcrRoutingException("ocr.redirectRejected", "OCR redirects are not allowed.");
        if (!response.IsSuccessStatusCode)
            throw new OcrRoutingException(
                prefix + ".http" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                "Baidu returned an HTTP failure.");
    }

    private static OcrResultSnapshot Parse(byte[] body, CloudOcrProviderRequest request)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error_code", out JsonElement error))
                throw new OcrRoutingException("ocr.baidu." + error, "Baidu OCR rejected the request.");
            if (!root.TryGetProperty("words_result", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array)
                throw new OcrRoutingException("ocr.malformedResponse", "Baidu OCR words result is missing.");
            var lines = new List<TextLine>();
            foreach (JsonElement result in results.EnumerateArray())
            {
                CloudOcrResponseParser.EnsureCapacity(lines.Count, "Baidu OCR");
                string text = CloudOcrResponseParser.RequiredText(result, "words", "Baidu OCR");
                double confidence = 0;
                if (result.TryGetProperty("probability", out JsonElement probability) &&
                    probability.TryGetProperty("average", out JsonElement average) &&
                    average.TryGetDouble(out double raw)) confidence = Math.Clamp(raw, 0, 1);
                NormalizedRect bounds = result.TryGetProperty("location", out JsonElement location)
                    ? Rectangle(location, request.PixelWidth, request.PixelHeight)
                    : new NormalizedRect(0, 0, 1, 1);
                lines.Add(new TextLine(text, bounds, confidence));
            }
            return new OcrResultSnapshot(request.ExecutionToken, lines, ProviderId, "v1", true, null);
        }
        catch (JsonException exception)
        {
            throw new OcrRoutingException("ocr.malformedJson", exception.Message);
        }
    }

    private static NormalizedRect Rectangle(JsonElement value, int width, int height)
    {
        double left = RequiredNumber(value, "left");
        double top = RequiredNumber(value, "top");
        double rectangleWidth = RequiredNumber(value, "width");
        double rectangleHeight = RequiredNumber(value, "height");
        double x = Math.Clamp(left / width, 0, 1);
        double y = Math.Clamp(top / height, 0, 1);
        double right = Math.Clamp((left + rectangleWidth) / width, x, 1);
        double bottom = Math.Clamp((top + rectangleHeight) / height, y, 1);
        if (right <= x || bottom <= y)
            throw new OcrRoutingException("ocr.malformedResponse", "Baidu OCR location is empty.");
        return new NormalizedRect(x, y, right - x, bottom - y);
    }

    private static double RequiredNumber(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out JsonElement element) ||
            !element.TryGetDouble(out double result) || !double.IsFinite(result))
            throw new OcrRoutingException("ocr.malformedResponse", "Baidu OCR location is malformed.");
        return result;
    }

    private static Uri AddQuery(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        string item = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        builder.Query = string.IsNullOrEmpty(builder.Query) ? item : builder.Query.TrimStart('?') + "&" + item;
        return builder.Uri;
    }

    private static void ValidateEndpoint(Uri value, string parameter)
    {
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(value.UserInfo) || !string.IsNullOrEmpty(value.Fragment))
            throw new ArgumentException("Baidu endpoint must be an HTTPS URI.", parameter);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;

    private sealed record AccessToken(string Value, DateTimeOffset RefreshAfterUtc);
}
