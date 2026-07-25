using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record TencentTranslationOptions(
    Uri Endpoint,
    string? Region,
    string SecretIdReference,
    string SecretKeyReference,
    string? SessionTokenReference,
    ProxyPolicy ProxyPolicy,
    int MaximumResponseBytes = 1024 * 1024,
    Func<DateTimeOffset>? Clock = null);

public sealed class TencentTranslationProvider : ITranslationProvider
{
    private const string ProviderId = "translation.tencent-tmt";
    private readonly TencentTranslationOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public TencentTranslationProvider(
        TencentTranslationOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretIdReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretKeyReference);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
            options.Endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment))
            throw new ArgumentException("Tencent TMT endpoint must be an HTTPS origin.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding(string purpose)
        => CreateCredentialBinding(_options, purpose);

    public static CredentialBinding CreateCredentialBinding(
        TencentTranslationOptions options,
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
        if (request.SourceText.Length is 0 or > 5000)
            return [new ProviderWireFailure("provider.tencent.requestTooLarge", false)];
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
        catch (CredentialBindingException)
        {
            return [new ProviderWireFailure("provider.credentialReconfirmationRequired", false)];
        }
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey) ||
            _options.SessionTokenReference is not null && string.IsNullOrWhiteSpace(sessionToken))
            return [new ProviderWireFailure("provider.credentialMissing", false)];

        byte[] body = CreateBody(request);
        DateTimeOffset now = (_options.Clock ?? (() => DateTimeOffset.UtcNow))();
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
            "tmt",
            "TextTranslate",
            "2018-03-21",
            _options.Region,
            sessionToken,
            now);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [new ProviderWireCancelled("provider.cancelled")];
        }
        catch (HttpRequestException)
        {
            return [new ProviderWireFailure("provider.network", true)];
        }
        using (response)
        {
            if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response))
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            if (!response.IsSuccessStatusCode)
            {
                bool retryable = response.StatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout;
                return [new ProviderWireFailure(
                    $"provider.tencent.http{(int)response.StatusCode}", retryable)];
            }
            byte[] responseBody;
            try
            {
                responseBody = await ProviderResponseReader.ReadBoundedAsync(
                    response.Content, _options.MaximumResponseBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return [new ProviderWireFailure("provider.responseLimit", false)];
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    responseBody, new JsonDocumentOptions { MaxDepth = 16 });
                if (!document.RootElement.TryGetProperty("Response", out JsonElement root))
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
                if (root.TryGetProperty("Error", out JsonElement error))
                {
                    string code = error.TryGetProperty("Code", out JsonElement codeElement)
                        ? codeElement.GetString() ?? "Unknown"
                        : "Unknown";
                    bool retryable = code.Contains("LimitExceeded", StringComparison.Ordinal) ||
                        code.Contains("InternalError", StringComparison.Ordinal) ||
                        code.Contains("RequestLimitExceeded", StringComparison.Ordinal);
                    return [new ProviderWireFailure("provider.tencent." + code, retryable)];
                }
                if (!root.TryGetProperty("TargetText", out JsonElement target) ||
                    target.ValueKind != JsonValueKind.String)
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
                string translated = target.GetString()!;
                if (translated.Length > request.MaximumOutputCharacters)
                    return [new ProviderWireFailure("provider.outputLimit", false)];
                return
                [
                    new ProviderDelta(1, translated),
                    new ProviderDone(1, new ProviderUsage(
                        request.SourceText.Length,
                        translated.Length,
                        request.CostReservation.BillingUnit)),
                ];
            }
            catch (JsonException)
            {
                return [new ProviderWireFailure("provider.malformedJson", false)];
            }
        }
    }

    private static byte[] CreateBody(TranslationRequest request)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("SourceText", request.SourceText);
            writer.WriteString("Source", request.SourceLanguage);
            writer.WriteString("Target", request.TargetLanguage);
            writer.WriteNumber("ProjectId", 0);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }
}

public static class TencentCloudSigner
{
    public static void Sign(
        HttpRequestMessage request,
        ReadOnlySpan<byte> body,
        string secretId,
        string secretKey,
        string service,
        string action,
        string version,
        string? region,
        string? sessionToken,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Uri uri = request.RequestUri ?? throw new ArgumentException("Request URI is required.", nameof(request));
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Tencent Cloud signing requires an HTTPS URI.", nameof(request));

        long unixSeconds = timestamp.ToUnixTimeSeconds();
        string date = timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string host = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
        string contentType = request.Content?.Headers.ContentType?.ToString() ??
            "application/json; charset=utf-8";
        string canonicalRequest = string.Concat(
            request.Method.Method, "\n",
            string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath, "\n",
            uri.Query.TrimStart('?'), "\n",
            "content-type:", contentType, "\n",
            "host:", host, "\n\n",
            "content-type;host\n",
            Sha256Hex(body));
        string scope = $"{date}/{service}/tc3_request";
        string stringToSign = string.Concat(
            "TC3-HMAC-SHA256\n",
            unixSeconds.ToString(CultureInfo.InvariantCulture), "\n",
            scope, "\n",
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));
        byte[] dateKey = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        byte[] serviceKey = Hmac(dateKey, service);
        byte[] signingKey = Hmac(serviceKey, "tc3_request");
        byte[] signature = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign));
        try
        {
            request.Headers.TryAddWithoutValidation("Authorization",
                $"TC3-HMAC-SHA256 Credential={secretId}/{scope}, SignedHeaders=content-type;host, Signature={Convert.ToHexString(signature).ToLowerInvariant()}");
            request.Headers.TryAddWithoutValidation("X-TC-Action", action);
            request.Headers.TryAddWithoutValidation("X-TC-Version", version);
            request.Headers.TryAddWithoutValidation("X-TC-Timestamp",
                unixSeconds.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(region))
                request.Headers.TryAddWithoutValidation("X-TC-Region", region);
            if (!string.IsNullOrWhiteSpace(sessionToken))
                request.Headers.TryAddWithoutValidation("X-TC-Token", sessionToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dateKey);
            CryptographicOperations.ZeroMemory(serviceKey);
            CryptographicOperations.ZeroMemory(signingKey);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string Sha256Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

internal static class ProviderResponseReader
{
    internal static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentEncoding.Count > 0 ||
            content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidDataException("Provider response exceeds its safety limits.");
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Provider response exceeds its safety limits.");
            output.Write(buffer, 0, read);
        }
    }
}
