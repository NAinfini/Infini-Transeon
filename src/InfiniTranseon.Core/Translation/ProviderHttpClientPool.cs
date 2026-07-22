using System.Net;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.Core.Translation;

public sealed record ProviderHttpOrigin(
    string ProviderId,
    Uri Origin,
    ProxyPolicy ProxyPolicy,
    Uri? ExplicitProxy = null);

public sealed class ProviderHttpClientPool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ProviderHttpOrigin, HttpClient> _clients = [];
    private bool _disposed;

    public HttpClient GetClient(ProviderHttpOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin.ProviderId);
        if (!Enum.IsDefined(origin.ProxyPolicy))
            throw new ArgumentException("Provider proxy policy is invalid.", nameof(origin));
        if (!origin.Origin.IsAbsoluteUri || origin.Origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.Origin.UserInfo) || origin.Origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Origin.Query) || !string.IsNullOrEmpty(origin.Origin.Fragment))
            throw new ArgumentException("Provider origin must be an HTTPS origin without path or user info.", nameof(origin));
        if (origin.ProxyPolicy == ProxyPolicy.Explicit && origin.ExplicitProxy is null)
            throw new ArgumentException("Explicit proxy policy requires a proxy URI.", nameof(origin));
        if (origin.ProxyPolicy != ProxyPolicy.Explicit && origin.ExplicitProxy is not null)
            throw new ArgumentException("Proxy URI is only allowed for explicit proxy policy.", nameof(origin));
        if (origin.ExplicitProxy is Uri proxy &&
            (!proxy.IsAbsoluteUri ||
                !(proxy.Scheme == Uri.UriSchemeHttp || proxy.Scheme == Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(proxy.UserInfo) || proxy.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(proxy.Query) || !string.IsNullOrEmpty(proxy.Fragment)))
            throw new ArgumentException(
                "Explicit proxy must be an HTTP(S) origin without embedded credentials.", nameof(origin));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(origin, out HttpClient? existing)) return existing;
            HttpClient client = CreateClient(origin);
            _clients.Add(origin, client);
            return client;
        }
    }

    public void Dispose()
    {
        HttpClient[] clients;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }
        foreach (HttpClient client in clients) client.Dispose();
    }

    internal static bool ResponseLeftRequestedUri(
        HttpRequestMessage request,
        HttpResponseMessage response) =>
        request.RequestUri is not Uri requested ||
        response.RequestMessage?.RequestUri is Uri actual && actual != requested;

    private static HttpClient CreateClient(ProviderHttpOrigin key)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 32,
            UseProxy = key.ProxyPolicy != ProxyPolicy.None,
            Proxy = key.ProxyPolicy == ProxyPolicy.Explicit ? new WebProxy(key.ExplicitProxy!) : null,
            Credentials = null,
            DefaultProxyCredentials = null,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
