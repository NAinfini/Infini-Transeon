using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record YoudaoTranslationOptions(
    Uri Endpoint,
    string AppKeyReference,
    string AppSecretReference,
    ProxyPolicy ProxyPolicy,
    string Domain = "game",
    int MaximumResponseBytes = 1024 * 1024,
    Func<DateTimeOffset>? Clock = null);

/// <summary>
/// Youdao Zhiyun text translation v3. The app key and secret remain in the bound credential
/// store; only the signed form request crosses the provider boundary.
/// </summary>
public sealed class YoudaoTranslationProvider : ITranslationProvider
{
    private const string ProviderId = "translation.youdao";
    private readonly YoudaoTranslationOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public YoudaoTranslationProvider(
        YoudaoTranslationOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppKeyReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppSecretReference);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment))
        {
            throw new ArgumentException(
                "Youdao translation endpoint must be an absolute HTTPS URI.",
                nameof(options));
        }
        if (options.Domain is not ("general" or "computers" or "medicine" or "finance" or "game"))
            throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateBinding(string purpose) =>
        CreateCredentialBinding(_options, purpose);

    public static CredentialBinding CreateCredentialBinding(
        YoudaoTranslationOptions options,
        string purpose)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (purpose is not ("app-key" or "app-secret"))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        return new CredentialBinding(
            ProviderId,
            purpose,
            options.Endpoint.Scheme,
            options.Endpoint.IdnHost,
            options.Endpoint.IsDefaultPort ? 443 : options.Endpoint.Port,
            "youdao-sha256-v3",
            options.ProxyPolicy);
    }

    public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderWireEvent> events =
            await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (ProviderWireEvent providerEvent in events)
            yield return providerEvent;
    }

    private async Task<IReadOnlyList<ProviderWireEvent>> ExecuteAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        string? appKey;
        string? appSecret;
        try
        {
            appKey = await _credentials.ReadAsync(
                _options.AppKeyReference,
                CreateBinding("app-key"),
                cancellationToken).ConfigureAwait(false);
            appSecret = await _credentials.ReadAsync(
                _options.AppSecretReference,
                CreateBinding("app-secret"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialBindingException)
        {
            return [new ProviderWireFailure("provider.credentialReconfirmationRequired", false)];
        }
        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(appSecret))
            return [new ProviderWireFailure("provider.credentialMissing", false)];

        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string currentTime = ((_options.Clock ?? (() => DateTimeOffset.UtcNow))())
            .ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        string signature = ComputeSignature(
            appKey, request.SourceText, salt, currentTime, appSecret);
        var fields = new Dictionary<string, string>
        {
            ["q"] = request.SourceText,
            ["from"] = ProviderLanguageCodes.ForYoudao(request.SourceLanguage),
            ["to"] = ProviderLanguageCodes.ForYoudao(request.TargetLanguage),
            ["appKey"] = appKey,
            ["salt"] = salt,
            ["sign"] = signature,
            ["signType"] = "v3",
            ["curtime"] = currentTime,
            ["domain"] = _options.Domain,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
            if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response) ||
                (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            if (!response.IsSuccessStatusCode)
            {
                bool retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500;
                return [new ProviderWireFailure(
                    retryable ? "provider.youdao.unavailable" : "provider.youdao.httpError",
                    retryable)];
            }

            byte[] bytes;
            try
            {
                bytes = await ReadBoundedAsync(
                    response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return [new ProviderWireFailure("provider.responseLimit", false)];
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                string errorCode = root.TryGetProperty("errorCode", out JsonElement error)
                    ? error.GetString() ?? error.GetRawText()
                    : string.Empty;
                if (errorCode != "0")
                    return [new ProviderWireFailure(
                        string.IsNullOrWhiteSpace(errorCode)
                            ? "provider.malformedResponse"
                            : "provider.youdao." + errorCode,
                        false)];
                if (!root.TryGetProperty("translation", out JsonElement translations) ||
                    translations.ValueKind != JsonValueKind.Array ||
                    translations.GetArrayLength() == 0)
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
                string translated = string.Join('\n', translations.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()));
                if (string.IsNullOrEmpty(translated))
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
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

    public static string ComputeSignature(
        string appKey,
        string text,
        string salt,
        string currentTime,
        string appSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(salt);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTime);
        ArgumentException.ThrowIfNullOrWhiteSpace(appSecret);
        string input = appKey + TruncateForSignature(text) + salt + currentTime + appSecret;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public static string TruncateForSignature(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= 20 ? text : text[..10] +
            text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            text[^10..];
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentEncoding.Count > 0)
            throw new InvalidDataException("Compressed Youdao responses are not accepted.");
        if (content.Headers.ContentLength is long length && length > maximumResponseBytes)
            throw new InvalidDataException("Youdao response exceeds its size limit.");
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumResponseBytes)
                throw new InvalidDataException("Youdao response exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
    }
}
