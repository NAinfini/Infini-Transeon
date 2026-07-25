using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record AlibabaTranslationOptions(
    Uri Endpoint,
    string AccessKeyIdReference,
    string AccessKeySecretReference,
    string? SecurityTokenReference,
    ProxyPolicy ProxyPolicy,
    bool IncludeContext = true,
    int MaximumResponseBytes = 1024 * 1024,
    Func<DateTimeOffset>? Clock = null,
    Func<string>? NonceFactory = null);

public sealed class AlibabaTranslationProvider : ITranslationProvider
{
    private const string ProviderId = "translation.alibaba";
    private readonly AlibabaTranslationOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public AlibabaTranslationProvider(
        AlibabaTranslationOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AccessKeyIdReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AccessKeySecretReference);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
            options.Endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment))
            throw new ArgumentException("Alibaba translation endpoint must be an HTTPS origin.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding(string purpose)
        => CreateCredentialBinding(_options, purpose);

    public static CredentialBinding CreateCredentialBinding(
        AlibabaTranslationOptions options,
        string purpose)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (purpose is not ("access-key-id" or "access-key-secret" or "security-token"))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        return new CredentialBinding(
            ProviderId,
            purpose,
            "https",
            options.Endpoint.IdnHost,
            options.Endpoint.IsDefaultPort ? 443 : options.Endpoint.Port,
            "acs3-hmac-sha256",
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
            return [new ProviderWireFailure("provider.alibaba.requestTooLarge", false)];
        string? accessKeyId;
        string? accessKeySecret;
        string? securityToken = null;
        try
        {
            accessKeyId = await _credentials.ReadAsync(
                _options.AccessKeyIdReference,
                CreateCredentialBinding("access-key-id"),
                cancellationToken).ConfigureAwait(false);
            accessKeySecret = await _credentials.ReadAsync(
                _options.AccessKeySecretReference,
                CreateCredentialBinding("access-key-secret"),
                cancellationToken).ConfigureAwait(false);
            if (_options.SecurityTokenReference is not null)
                securityToken = await _credentials.ReadAsync(
                    _options.SecurityTokenReference,
                    CreateCredentialBinding("security-token"),
                    cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialBindingException)
        {
            return [new ProviderWireFailure("provider.credentialReconfirmationRequired", false)];
        }
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(accessKeySecret) ||
            _options.SecurityTokenReference is not null && string.IsNullOrWhiteSpace(securityToken))
            return [new ProviderWireFailure("provider.credentialMissing", false)];

        string? context = _options.IncludeContext ? BuildContext(request.Context) : null;
        Uri endpoint = context is null
            ? _options.Endpoint
            : new UriBuilder(_options.Endpoint)
            {
                Query = "Context=" + Rfc3986(context),
            }.Uri;
        byte[] body = Encoding.UTF8.GetBytes(string.Join('&', new[]
        {
            "FormatType=text",
            "SourceLanguage=" + Rfc3986(
                ProviderLanguageCodes.ForAlibaba(request.SourceLanguage)),
            "TargetLanguage=" + Rfc3986(
                ProviderLanguageCodes.ForAlibaba(request.TargetLanguage)),
            "SourceText=" + Rfc3986(request.SourceText),
            "Scene=general",
        }));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new("application/x-www-form-urlencoded")
        {
            CharSet = "utf-8",
        };
        AlibabaCloudSigner.Sign(
            message,
            body,
            accessKeyId,
            accessKeySecret,
            "TranslateGeneral",
            "2018-10-12",
            securityToken,
            (_options.Clock ?? (() => DateTimeOffset.UtcNow))(),
            (_options.NonceFactory ?? (() => Guid.NewGuid().ToString("D")))());

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
                    $"provider.alibaba.http{(int)response.StatusCode}", retryable)];
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
                JsonElement root = document.RootElement;
                int code = root.TryGetProperty("Code", out JsonElement codeElement) &&
                    codeElement.TryGetInt32(out int parsedCode) ? parsedCode : 0;
                if (code != 200)
                    return [new ProviderWireFailure(
                        "provider.alibaba." + code.ToString(CultureInfo.InvariantCulture),
                        code is 10010 or 10011 or 10012)];
                if (!root.TryGetProperty("Data", out JsonElement data) ||
                    !data.TryGetProperty("Translated", out JsonElement translatedElement) ||
                    translatedElement.ValueKind != JsonValueKind.String)
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
                string translated = translatedElement.GetString()!;
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

    private static string? BuildContext(TranslationContext context)
    {
        string value = string.Join(" | ", new[]
        {
            context.GameName,
            context.GameDescription,
            context.Scene,
            context.Speaker,
        }.Where(item => !string.IsNullOrWhiteSpace(item)));
        return string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, 1000)];
    }

    private static string Rfc3986(string value) => Uri.EscapeDataString(value)
        .Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);
}

public static class AlibabaCloudSigner
{
    public static void Sign(
        HttpRequestMessage request,
        ReadOnlySpan<byte> body,
        string accessKeyId,
        string accessKeySecret,
        string action,
        string version,
        string? securityToken,
        DateTimeOffset timestamp,
        string nonce)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeySecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        Uri uri = request.RequestUri ?? throw new ArgumentException("Request URI is required.", nameof(request));
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Alibaba Cloud signing requires an HTTPS URI.", nameof(request));
        string bodyHash = Sha256Hex(body);
        string date = timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string host = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-acs-action"] = action,
            ["x-acs-content-sha256"] = bodyHash,
            ["x-acs-date"] = date,
            ["x-acs-signature-nonce"] = nonce,
            ["x-acs-version"] = version,
        };
        if (!string.IsNullOrWhiteSpace(securityToken))
            headers["x-acs-security-token"] = securityToken;
        string signedHeaders = string.Join(';', headers.Keys);
        string canonicalHeaders = string.Concat(headers.Select(item =>
            item.Key + ":" + item.Value.Trim() + "\n"));
        string canonicalRequest = string.Concat(
            request.Method.Method, "\n",
            string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath, "\n",
            CanonicalQuery(uri.Query), "\n",
            canonicalHeaders, "\n",
            signedHeaders, "\n",
            bodyHash);
        string stringToSign = "ACS3-HMAC-SHA256\n" +
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));
        byte[] signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(accessKeySecret),
            Encoding.UTF8.GetBytes(stringToSign));
        try
        {
            request.Headers.TryAddWithoutValidation("x-acs-action", action);
            request.Headers.TryAddWithoutValidation("x-acs-content-sha256", bodyHash);
            request.Headers.TryAddWithoutValidation("x-acs-date", date);
            request.Headers.TryAddWithoutValidation("x-acs-signature-nonce", nonce);
            request.Headers.TryAddWithoutValidation("x-acs-version", version);
            if (!string.IsNullOrWhiteSpace(securityToken))
                request.Headers.TryAddWithoutValidation("x-acs-security-token", securityToken);
            request.Headers.TryAddWithoutValidation("Authorization",
                $"ACS3-HMAC-SHA256 Credential={accessKeyId},SignedHeaders={signedHeaders},Signature={Convert.ToHexString(signature).ToLowerInvariant()}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        return string.Join('&', query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(item => item, StringComparer.Ordinal));
    }

    private static string Sha256Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
