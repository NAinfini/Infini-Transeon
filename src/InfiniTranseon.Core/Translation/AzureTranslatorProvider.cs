using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record AzureTranslatorOptions(
    Uri Endpoint,
    string CredentialReference,
    string? Region,
    ProxyPolicy ProxyPolicy,
    int MaximumResponseBytes = 1024 * 1024);

public sealed class AzureTranslatorProvider : ITranslationProvider
{
    private readonly AzureTranslatorOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;

    public AzureTranslatorProvider(
        AzureTranslatorOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialReference);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(options.Endpoint.Query) || !string.IsNullOrEmpty(options.Endpoint.Fragment))
            throw new ArgumentException("Azure Translator endpoint must be an absolute HTTPS base URI.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding()
    {
        int port = _options.Endpoint.IsDefaultPort ? 443 : _options.Endpoint.Port;
        return new CredentialBinding(
            "translation.azure-ai",
            "api-key",
            _options.Endpoint.Scheme,
            _options.Endpoint.IdnHost,
            port,
            "ocp-apim-subscription-key",
            _options.ProxyPolicy);
    }

    public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderWireEvent> events = await ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        foreach (ProviderWireEvent providerEvent in events) yield return providerEvent;
    }

    private async Task<IReadOnlyList<ProviderWireEvent>> ExecuteAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceText.Length > 5000)
            return [new ProviderWireFailure("provider.azure.requestTooLarge", false)];
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
            return [new ProviderWireFailure("provider.credentialReconfirmationRequired", false)];
        }
        if (string.IsNullOrWhiteSpace(key))
            return [new ProviderWireFailure("provider.credentialMissing", false)];

        Uri uri = CreateRequestUri(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
        if (!string.IsNullOrWhiteSpace(_options.Region))
            message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", _options.Region);
        message.Headers.TryAddWithoutValidation("X-ClientTraceId", Guid.NewGuid().ToString("D"));
        message.Content = new ByteArrayContent(CreateRequestBody(request.SourceText));
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };

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
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            if (!response.IsSuccessStatusCode)
            {
                bool retryable = response.StatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout;
                return [new ProviderWireFailure($"provider.azure.http{status}", retryable)];
            }
            byte[] responseBytes;
            try
            {
                responseBytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return [new ProviderWireFailure("provider.responseLimit", false)];
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    responseBytes,
                    new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 ||
                    !root[0].TryGetProperty("translations", out JsonElement translations) ||
                    translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0 ||
                    !translations[0].TryGetProperty("text", out JsonElement textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                    return [new ProviderWireFailure("provider.malformedResponse", false)];
                string text = textElement.GetString()!;
                if (text.Length > request.MaximumOutputCharacters)
                    return [new ProviderWireFailure("provider.outputLimit", false)];
                long billedCharacters = request.SourceText.Length;
                if (response.Headers.TryGetValues("X-Metered-Usage", out IEnumerable<string>? usageValues) &&
                    long.TryParse(usageValues.FirstOrDefault(), out long metered) && metered >= 0)
                    billedCharacters = metered;
                return
                [
                    new ProviderDelta(1, text),
                    new ProviderDone(1, new ProviderUsage(billedCharacters, text.Length,
                        request.CostReservation.BillingUnit)),
                ];
            }
            catch (JsonException)
            {
                return [new ProviderWireFailure("provider.malformedJson", false)];
            }
        }
    }

    private Uri CreateRequestUri(TranslationRequest request)
    {
        Uri endpoint = _options.Endpoint.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? _options.Endpoint
            : new Uri(_options.Endpoint.AbsoluteUri + "/");
        var query = new List<string>
        {
            "api-version=3.0",
            "to=" + Uri.EscapeDataString(request.TargetLanguage),
        };
        if (!string.Equals(request.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
            query.Add("from=" + Uri.EscapeDataString(request.SourceLanguage));
        var builder = new UriBuilder(new Uri(endpoint, "translate"))
        {
            Query = string.Join('&', query),
        };
        return builder.Uri;
    }

    private static byte[] CreateRequestBody(string text)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("Text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        return output.ToArray();
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentEncoding.Count > 0 ||
            content.Headers.ContentLength is long length && length > _options.MaximumResponseBytes)
            throw new InvalidDataException("Azure Translator response exceeds its safety limits.");
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > _options.MaximumResponseBytes)
                throw new InvalidDataException("Azure Translator response exceeds its safety limits.");
            output.Write(buffer, 0, read);
        }
    }
}
