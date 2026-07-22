using System.Net;
using System.Security.Cryptography;

namespace InfiniTranseon.Core.Translation.Local;

public sealed record ModelDownloadRequest(
    VerifiedModelCatalog Catalog,
    string ModelId,
    string ModelVersion,
    string RelativePath,
    Uri Origin,
    bool UserApproved,
    bool StrictOffline = false);

public sealed class ModelDownloadService
{
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly string _managedRoot;

    public ModelDownloadService(Func<HttpClient> httpClientFactory, string managedRoot)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _httpClientFactory = httpClientFactory;
        _managedRoot = Path.GetFullPath(managedRoot);
    }

    public async ValueTask<string> DownloadAsync(
        ModelDownloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserApproved)
            throw new InvalidOperationException("Model downloads require explicit user approval.");
        if (request.StrictOffline)
            throw new InvalidOperationException(
                "Model downloads are disabled while strict-offline mode is active.");
        ArgumentNullException.ThrowIfNull(request.Catalog);
        ModelCatalogEntry model = request.Catalog.ResolveModel(
            request.ModelId, request.ModelVersion);
        ModelCatalogFile file = model.Files.SingleOrDefault(candidate =>
                string.Equals(candidate.RelativePath, request.RelativePath, StringComparison.Ordinal)) ??
            throw new InvalidDataException("The requested file is absent from the verified model entry.");
        if (!model.DownloadOrigins.Contains(request.Origin) ||
            request.Origin.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Model download is not authorized by the verified catalog.");
        string destination = ModelPathPolicy.ResolveManagedPath(_managedRoot, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        destination = ModelPathPolicy.ResolveManagedPath(_managedRoot, file.RelativePath);
        var uri = new Uri(request.Origin, file.RelativePath);
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpClient httpClient = _httpClientFactory() ??
            throw new InvalidOperationException("Model HTTP client factory returned no client.");
        using HttpResponseMessage response = await httpClient.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is not Uri finalUri || finalUri != uri)
            throw new InvalidDataException(
                "Model HTTP client performed an automatic redirect; automatic redirects must be disabled.");
        if (
            finalUri.Scheme != uri.Scheme || finalUri.Host != uri.Host ||
            finalUri.Port != uri.Port)
            throw new InvalidDataException(
                "Model download left the origin authorized by the verified catalog.");
        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            throw new InvalidDataException("Model download redirects are rejected.");
        if (response.StatusCode != HttpStatusCode.OK) response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != file.ByteSize)
            throw new InvalidDataException("Model download size does not match the signed catalog.");

        string temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > file.ByteSize)
                    throw new InvalidDataException("Model download exceeded the signed byte size.");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != file.ByteSize ||
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Model download checksum verification failed.");
            output.Close();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
