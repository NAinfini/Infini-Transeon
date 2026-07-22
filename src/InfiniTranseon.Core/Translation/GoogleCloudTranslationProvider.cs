using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record GoogleCloudTranslationOptions(
    Uri Endpoint,
    ProxyPolicy ProxyPolicy,
    string MimeType = "text/plain",
    int MaximumRequestBytes = 1024 * 1024,
    int MaximumResponseBytes = 2 * 1024 * 1024);

public sealed class GoogleCloudTranslationProvider : ITranslationProvider
{
    private readonly GoogleCloudTranslationOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IGoogleAccessTokenSource _tokenSource;

    public GoogleCloudTranslationProvider(
        GoogleCloudTranslationOptions options,
        HttpClient httpClient,
        IGoogleAccessTokenSource tokenSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenSource);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo) || !string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment) ||
            !options.Endpoint.AbsolutePath.EndsWith(":translateText", StringComparison.Ordinal))
            throw new ArgumentException("Google Translation endpoint must be an HTTPS v3 translateText URI.", nameof(options));
        if (options.MimeType is not ("text/plain" or "text/html"))
            throw new ArgumentException("Google Translation MIME type is unsupported.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRequestBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _tokenSource = tokenSource;
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
        if (request.StrictOffline)
            return [new ProviderWireFailure("provider.policy.strictOffline", false)];
        string token;
        try
        {
            token = await _tokenSource.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [new ProviderWireCancelled("provider.cancelled")];
        }
        catch (InvalidOperationException exception)
        {
            return [new ProviderWireFailure(exception.Message, false)];
        }
        if (string.IsNullOrWhiteSpace(token))
            return [new ProviderWireFailure("google.oauth.credentialMissing", false)];
        byte[] body = CreateBody(request);
        if (body.Length > _options.MaximumRequestBytes)
            return [new ProviderWireFailure("provider.requestLimit", false)];
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                return [new ProviderWireFailure("provider.redirectRejected", false)];
            if (!response.IsSuccessStatusCode)
            {
                bool retryable = response.StatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout;
                return [new ProviderWireFailure(
                    $"provider.google-cloud.http{(int)response.StatusCode}", retryable)];
            }
            byte[] responseBody;
            try
            {
                responseBody = await ProviderResponseReader.ReadBoundedAsync(
                    response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return [new ProviderWireFailure("provider.responseLimit", false)];
            }
            return Parse(responseBody, request);
        }
    }

    private byte[] CreateBody(TranslationRequest request)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("contents");
            writer.WriteStringValue(request.SourceText);
            writer.WriteEndArray();
            if (!string.IsNullOrWhiteSpace(request.SourceLanguage))
                writer.WriteString("sourceLanguageCode", request.SourceLanguage);
            writer.WriteString("targetLanguageCode", request.TargetLanguage);
            writer.WriteString("mimeType", _options.MimeType);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static IReadOnlyList<ProviderWireEvent> Parse(
        byte[] responseBody,
        TranslationRequest request)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                responseBody, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("translations", out JsonElement translations) ||
                translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() != 1 ||
                !translations[0].TryGetProperty("translatedText", out JsonElement translatedElement) ||
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
