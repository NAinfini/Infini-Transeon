using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed class BaiduTranslationProvider : ITranslationProvider
{
    private static readonly Uri Endpoint = new("https://fanyi-api.baidu.com/api/trans/vip/translate");
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public BaiduTranslationProvider(HttpClient httpClient, IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateBinding(string purpose) => new(
        "translation.baidu", purpose, "https", Endpoint.Host, 443,
        "baidu-md5-appid-salt-secret", ProxyPolicy.System);

    public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderWireEvent> events = await ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        foreach (ProviderWireEvent providerEvent in events)
            yield return providerEvent;
    }

    private async Task<IReadOnlyList<ProviderWireEvent>> ExecuteAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        string? appId;
        string? secret;
        try
        {
            appId = await _credentials.ReadAsync(
                "app-id", CreateBinding("app-id"), cancellationToken).ConfigureAwait(false);
            secret = await _credentials.ReadAsync(
                "secret", CreateBinding("secret"), cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialBindingException)
        {
            return [new ProviderWireFailure("provider.credentialReconfirmationRequired", false)];
        }
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
            return [new ProviderWireFailure("provider.credentialMissing", false)];
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string sign = ComputeSignature(appId, request.SourceText, salt, secret);
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = request.SourceText,
                ["from"] = request.SourceLanguage,
                ["to"] = request.TargetLanguage,
                ["appid"] = appId,
                ["salt"] = salt,
                ["sign"] = sign,
            }),
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
            if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response))
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            if (!response.IsSuccessStatusCode)
            {
                return [new ProviderWireFailure(
                    response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                        ? "provider.baidu.unavailable"
                        : "provider.baidu.httpError",
                    response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)];
            }
            byte[] bytes;
            try { bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException)
            {
                return [new ProviderWireFailure("provider.responseLimit", false)];
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("error_code", out JsonElement error))
                {
                    string code = error.GetString() ?? error.GetRawText();
                    bool retryable = code is "52001" or "52002" or "54003";
                    return [new ProviderWireFailure("provider.baidu." + code, retryable)];
                }
                if (!root.TryGetProperty("trans_result", out JsonElement translations) ||
                    translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
                string translated = string.Join('\n', translations.EnumerateArray().Select(item =>
                    item.TryGetProperty("dst", out JsonElement destination) &&
                    destination.ValueKind == JsonValueKind.String
                        ? destination.GetString()
                        : null).Where(item => item is not null));
                if (string.IsNullOrEmpty(translated) || translated.Length > request.MaximumOutputCharacters)
                    return [new ProviderWireFailure(
                        translated.Length > request.MaximumOutputCharacters
                            ? "provider.outputLimit"
                            : "provider.malformedResponse",
                        false)];
                return
                [
                    new ProviderDelta(1, translated),
                    new ProviderDone(1, new ProviderUsage(
                        request.SourceText.Length, translated.Length, request.CostReservation.BillingUnit)),
                ];
            }
            catch (JsonException)
            {
                return [new ProviderWireFailure("provider.malformedJson", false)];
            }
        }
    }

    public static string ComputeSignature(string appId, string text, string salt, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(salt);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        byte[] bytes = Encoding.UTF8.GetBytes(appId + text + salt + secret);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentEncoding.Count > 0)
            throw new InvalidDataException("Compressed Baidu responses are not accepted.");
        if (content.Headers.ContentLength is > 1024 * 1024)
            throw new InvalidDataException("Baidu response exceeds its size limit.");
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > 1024 * 1024)
                throw new InvalidDataException("Baidu response exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
    }
}
