using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public interface IGoogleAccessTokenSource
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public sealed record GoogleServiceAccountTokenOptions(
    Uri TokenEndpoint,
    string CredentialReference,
    ProxyPolicy ProxyPolicy,
    int MaximumCredentialCharacters = 256 * 1024,
    int MaximumResponseBytes = 64 * 1024,
    Func<DateTimeOffset>? Clock = null);

public sealed class GoogleServiceAccountTokenSource : IGoogleAccessTokenSource, IDisposable
{
    private const string ProviderId = "google.oauth2";
    private const string Scope = "https://www.googleapis.com/auth/cloud-translation";
    private readonly GoogleServiceAccountTokenOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBoundCredentialStore _credentials;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken? _cached;
    private bool _disposed;

    public GoogleServiceAccountTokenSource(
        GoogleServiceAccountTokenOptions options,
        HttpClient httpClient,
        IBoundCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialReference);
        if (!options.TokenEndpoint.IsAbsoluteUri || options.TokenEndpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(options.TokenEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(options.TokenEndpoint.Query) ||
            !string.IsNullOrEmpty(options.TokenEndpoint.Fragment))
            throw new ArgumentException("Google OAuth endpoint must be an HTTPS URI.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumCredentialCharacters, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumResponseBytes, 1024);
        _options = options;
        _httpClient = httpClient;
        _credentials = credentials;
    }

    public CredentialBinding CreateCredentialBinding() => new(
        ProviderId,
        "service-account-json",
        "https",
        _options.TokenEndpoint.IdnHost,
        _options.TokenEndpoint.IsDefaultPort ? 443 : _options.TokenEndpoint.Port,
        "oauth2-jwt-bearer",
        _options.ProxyPolicy);

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = Clock();
        AccessToken? cached = Volatile.Read(ref _cached);
        if (cached is not null && now < cached.RefreshAfterUtc) return cached.Value;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = Clock();
            cached = _cached;
            if (cached is not null && now < cached.RefreshAfterUtc) return cached.Value;
            ServiceAccount account = await ReadServiceAccountAsync(cancellationToken).ConfigureAwait(false);
            string assertion = CreateAssertion(account, now);
            using var message = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
            };
            using HttpResponseMessage response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (ProviderHttpClientPool.ResponseLeftRequestedUri(message, response) ||
                (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                throw new InvalidOperationException("google.oauth.redirectRejected");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "google.oauth.http" + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            byte[] body;
            try
            {
                body = await ProviderResponseReader.ReadBoundedAsync(
                    response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException("google.oauth.responseLimit", exception);
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("error", out JsonElement error))
                    throw new InvalidOperationException("google.oauth." + (error.GetString() ?? "unknown"));
                string value = root.TryGetProperty("access_token", out JsonElement tokenElement) &&
                    tokenElement.ValueKind == JsonValueKind.String
                        ? tokenElement.GetString()!
                        : throw new InvalidOperationException("google.oauth.malformedResponse");
                long expires = root.TryGetProperty("expires_in", out JsonElement expiresElement) &&
                    expiresElement.TryGetInt64(out long parsed) ? parsed : 0;
                if (string.IsNullOrWhiteSpace(value) || expires < 60)
                    throw new InvalidOperationException("google.oauth.malformedResponse");
                var token = new AccessToken(value, now.AddSeconds(Math.Max(1, expires - 60)));
                Volatile.Write(ref _cached, token);
                return token.Value;
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("google.oauth.malformedJson", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cached = null;
        _gate.Dispose();
    }

    private DateTimeOffset Clock() => (_options.Clock ?? (() => DateTimeOffset.UtcNow))();

    private async Task<ServiceAccount> ReadServiceAccountAsync(CancellationToken cancellationToken)
    {
        string value;
        try
        {
            value = await _credentials.ReadAsync(
                _options.CredentialReference, CreateCredentialBinding(), cancellationToken)
                .ConfigureAwait(false) ?? "";
        }
        catch (CredentialBindingException exception)
        {
            throw new InvalidOperationException("google.oauth.credentialReconfirmationRequired", exception);
        }
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("google.oauth.credentialMissing");
        if (value.Length > _options.MaximumCredentialCharacters)
            throw new InvalidOperationException("google.oauth.credentialLimit");
        try
        {
            using JsonDocument document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (Required(root, "type") != "service_account")
                throw new InvalidOperationException("google.oauth.unsupportedCredentialType");
            string email = Required(root, "client_email");
            string privateKey = Required(root, "private_key");
            if (email.Length > 320 || privateKey.Length > 64 * 1024)
                throw new InvalidOperationException("google.oauth.malformedCredential");
            return new ServiceAccount(email, privateKey);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("google.oauth.malformedCredential", exception);
        }
    }

    private string CreateAssertion(ServiceAccount account, DateTimeOffset now)
    {
        long issued = now.ToUnixTimeSeconds();
        string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        byte[] claimsBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = account.Email,
            scope = Scope,
            aud = _options.TokenEndpoint.AbsoluteUri,
            exp = issued + 3600,
            iat = issued,
        });
        string claims = Base64Url(claimsBytes);
        string unsigned = header + "." + claims;
        byte[] signature;
        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(account.PrivateKey);
            signature = rsa.SignData(
                Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("google.oauth.invalidPrivateKey", exception);
        }
        try
        {
            return unsigned + "." + Base64Url(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(claimsBytes);
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
            throw new InvalidOperationException("google.oauth.network", exception);
        }
    }

    private static string Required(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("google.oauth.malformedCredential");
        return value.GetString()!;
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record ServiceAccount(string Email, string PrivateKey);
    private sealed record AccessToken(string Value, DateTimeOffset RefreshAfterUtc);
}
