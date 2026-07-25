using System.Net;
using System.Net.Http.Headers;
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

public sealed record ModelDownloadProgress(long BytesReceived, long TotalBytes);

public sealed class ModelDownloadService
{
    private const int BufferSize = 128 * 1024;
    private const int MaximumRedirects = 5;
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
        CancellationToken cancellationToken) =>
        await DownloadAsync(request, progress: null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> DownloadAsync(
        ModelDownloadRequest request,
        IProgress<ModelDownloadProgress>? progress,
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
        if (File.Exists(destination))
        {
            if (await VerifyFileAsync(
                    destination,
                    file.ByteSize,
                    file.Sha256,
                    cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(new ModelDownloadProgress(file.ByteSize, file.ByteSize));
                return destination;
            }
            File.Delete(destination);
        }

        string partial = destination + ".partial";
        long resumeOffset = 0;
        if (File.Exists(partial))
        {
            if ((File.GetAttributes(partial) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A partial model download cannot be a reparse point.");
            resumeOffset = new FileInfo(partial).Length;
            if (resumeOffset > file.ByteSize)
            {
                File.Delete(partial);
                resumeOffset = 0;
            }
            else if (resumeOffset == file.ByteSize)
            {
                if (await VerifyFileAsync(
                        partial,
                        file.ByteSize,
                        file.Sha256,
                        cancellationToken).ConfigureAwait(false))
                {
                    File.Move(partial, destination);
                    progress?.Report(new ModelDownloadProgress(file.ByteSize, file.ByteSize));
                    return destination;
                }
                File.Delete(partial);
                resumeOffset = 0;
            }
        }

        var uri = new Uri(request.Origin, file.RelativePath);
        using HttpClient httpClient = _httpClientFactory() ??
            throw new InvalidOperationException("Model HTTP client factory returned no client.");
        using HttpResponseMessage response = await SendFollowingSafeRedirectsAsync(
            httpClient,
            uri,
            resumeOffset,
            cancellationToken).ConfigureAwait(false);

        bool append = resumeOffset > 0 &&
            response.StatusCode == HttpStatusCode.PartialContent;
        if (append)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" ||
                range.From != resumeOffset ||
                range.To != file.ByteSize - 1 ||
                range.Length != file.ByteSize)
                throw new InvalidDataException(
                    "Model download returned a range that does not match the signed file.");
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            if (resumeOffset > 0)
            {
                File.Delete(partial);
                resumeOffset = 0;
            }
        }
        else
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException("Model download returned an unsupported response.");
        }

        long expectedResponseBytes = file.ByteSize - resumeOffset;
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != expectedResponseBytes)
            throw new InvalidDataException("Model download size does not match the signed catalog.");

        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (resumeOffset > 0)
                await AppendFileToHashAsync(partial, hash, cancellationToken).ConfigureAwait(false);
            await using Stream input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            byte[] buffer = new byte[BufferSize];
            long total = resumeOffset;
            progress?.Report(new ModelDownloadProgress(total, file.ByteSize));
            await using (var output = new FileStream(
                partial,
                append ? FileMode.Append : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous |
                    FileOptions.SequentialScan |
                    FileOptions.WriteThrough))
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total = checked(total + read);
                    if (total > file.ByteSize)
                        throw new InvalidDataException(
                            "Model download exceeded the signed byte size.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                    progress?.Report(new ModelDownloadProgress(total, file.ByteSize));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (total != file.ByteSize ||
                !Convert.ToHexString(hash.GetHashAndReset())
                    .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Model download checksum verification failed.");
            File.Move(partial, destination);
            return destination;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or OverflowException)
        {
            File.Delete(partial);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendFollowingSafeRedirectsAsync(
        HttpClient httpClient,
        Uri initialUri,
        long resumeOffset,
        CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        var visited = new HashSet<Uri>();
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!visited.Add(current))
                throw new InvalidDataException("Model download redirect loop was rejected.");
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            if (resumeOffset > 0)
                message.Headers.Range = new RangeHeaderValue(resumeOffset, null);
            HttpResponseMessage response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is not Uri responseUri ||
                responseUri != current)
            {
                response.Dispose();
                throw new InvalidDataException(
                    "Model HTTP client performed an automatic redirect; automatic redirects must be disabled.");
            }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                return response;

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (redirect == MaximumRedirects)
                throw new InvalidDataException("Model download exceeded the redirect limit.");
            if (location is null)
                throw new InvalidDataException("Model download redirect has no location.");
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (next.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(next.UserInfo) ||
                !string.IsNullOrEmpty(next.Fragment))
                throw new InvalidDataException("Model download redirect target is unsafe.");
            current = next;
        }
        throw new InvalidDataException("Model download exceeded the redirect limit.");
    }

    private static async Task<bool> VerifyFileAsync(
        string path,
        long expectedByteSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists ||
            file.Length != expectedByteSize ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            return false;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await AppendFileToHashAsync(path, hash, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset())
            .Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AppendFileToHashAsync(
        string path,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            hash.AppendData(buffer.AsSpan(0, read));
        }
    }
}
