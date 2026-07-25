using System.Net;
using System.Text.Json;
using InfiniTranseon.Contracts.Security;
using InfiniTranseon.Core.Artifacts;

namespace InfiniTranseon.Core.Updates;

public sealed record UpdateCheckContext(
    bool StrictOffline,
    bool ExplicitUserAction,
    bool CaptureTargetActive,
    bool MainUiVisible,
    Version CurrentVersion,
    string Channel = "stable",
    string Architecture = "win-x64");

public sealed record ReleaseManifestArtifact(
    string FileName,
    long ByteSize,
    string Sha256,
    string CodeSigning,
    string? AuthenticodePublisher);

public sealed record ReleaseManifest(
    int SchemaVersion,
    long ReleaseSequence,
    string ReleaseVersion,
    string Channel,
    string Architecture,
    int MinimumWindowsBuild,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<ReleaseManifestArtifact> Artifacts,
    IReadOnlyList<SignatureEntry> Signatures);

public sealed record UpdateArtifact(
    Uri DownloadUri,
    string FileName,
    long ByteSize,
    string Sha256,
    string CodeSigning,
    string? AuthenticodePublisher);

public static class ArtifactCodeSigningPolicies
{
    public const string NotApplicable = "not-applicable";
    public const string Unsigned = "unsigned";
    public const string Authenticode = "authenticode";
}

public sealed record UpdateMetadata(
    Version Version,
    long ReleaseSequence,
    string Channel,
    string Architecture,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<UpdateArtifact> Artifacts,
    string VerifiedKeyId);

public sealed class UpdatePolicyException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public interface IReleaseUpdateClient
{
    ValueTask<UpdateMetadata?> CheckAsync(
        UpdateCheckContext context,
        CancellationToken cancellationToken);

    ValueTask<string> DownloadApprovedAsync(
        UpdateArtifact artifact,
        string destinationPath,
        bool userApproved,
        CancellationToken cancellationToken);
}

public sealed class GitHubReleaseUpdateService : IReleaseUpdateClient
{
    private const int MaximumGitHubMetadataBytes = 512 * 1024;
    private const int MaximumManifestBytes = 256 * 1024;
    private const long MaximumArtifactBytes = 4L * 1024 * 1024 * 1024;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly Uri _latestReleaseApi;
    private readonly SignatureVerifier _signatureVerifier;
    private readonly ISignedSequenceState _sequence;

    public GitHubReleaseUpdateService(
        Func<HttpClient> httpClientFactory,
        Uri latestReleaseApi,
        SignatureVerifier signatureVerifier,
        ISignedSequenceState sequence)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(latestReleaseApi);
        ArgumentNullException.ThrowIfNull(signatureVerifier);
        ArgumentNullException.ThrowIfNull(sequence);
        if (!latestReleaseApi.IsAbsoluteUri || latestReleaseApi.Scheme != Uri.UriSchemeHttps ||
            latestReleaseApi.Host != "api.github.com")
            throw new ArgumentException("Update metadata must use the GitHub HTTPS API.", nameof(latestReleaseApi));
        _httpClientFactory = httpClientFactory;
        _latestReleaseApi = latestReleaseApi;
        _signatureVerifier = signatureVerifier;
        _sequence = sequence;
    }

    public async ValueTask<UpdateMetadata?> CheckAsync(
        UpdateCheckContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.StrictOffline)
            throw new UpdatePolicyException("update.strictOffline", "Update checks are disabled in strict-offline mode.");
        if (!context.ExplicitUserAction && (context.CaptureTargetActive || !context.MainUiVisible))
            throw new UpdatePolicyException("update.activityBlocked", "Automatic update checks wait for an idle visible main window.");

        using HttpClient client = _httpClientFactory() ??
            throw new InvalidOperationException("Update HTTP client factory returned null.");
        using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseApi);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.UserAgent.ParseAdd("InfiniTranseon-Updater/1");
        byte[] releaseBytes = await GetBoundedAsync(
            client, request, MaximumGitHubMetadataBytes,
            uri => uri.Scheme == Uri.UriSchemeHttps && uri.Host == "api.github.com",
            maximumRedirects: 0, cancellationToken).ConfigureAwait(false);
        using JsonDocument release = JsonDocument.Parse(releaseBytes, new JsonDocumentOptions { MaxDepth = 16 });
        string? releaseTag = release.RootElement.TryGetProperty("tag_name", out JsonElement tagElement)
            ? tagElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(releaseTag))
            throw new InvalidDataException("GitHub release metadata has no tag identity.");
        if (!release.RootElement.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 64)
            throw new InvalidDataException("GitHub release metadata has an invalid asset list.");
        var assetUris = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
                throw new InvalidDataException("GitHub release asset metadata is invalid.");
            assetUris.Add(name, uri);
        }
        if (!assetUris.TryGetValue("release-manifest.json", out Uri? manifestUri))
            throw new InvalidDataException("Signed release manifest asset is missing.");
        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        byte[] manifestBytes = await GetBoundedAsync(
            client, manifestRequest, MaximumManifestBytes, IsTrustedGitHubDownloadUri,
            maximumRedirects: 3, cancellationToken).ConfigureAwait(false);
        string keyId = _signatureVerifier.VerifyCanonicalJson(manifestBytes);
        ReleaseManifest manifest = JsonSerializer.Deserialize<ReleaseManifest>(
            manifestBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
            throw new InvalidDataException("Release manifest is empty.");
        ValidateManifest(manifest, context, assetUris, releaseTag);
        if (!_sequence.TryAccept(manifest.ReleaseSequence))
            throw new InvalidDataException("Release manifest downgrade was rejected.");
        var version = Version.Parse(manifest.ReleaseVersion.Split('-', 2)[0]);
        if (version <= context.CurrentVersion) return null;
        UpdateArtifact[] result = manifest.Artifacts.Select(artifact => new UpdateArtifact(
            assetUris[artifact.FileName], artifact.FileName, artifact.ByteSize,
            artifact.Sha256, artifact.CodeSigning, artifact.AuthenticodePublisher)).ToArray();
        return new UpdateMetadata(
            version, manifest.ReleaseSequence, manifest.Channel, manifest.Architecture,
            manifest.PublishedAtUtc, result, keyId);
    }

    public async ValueTask<string> DownloadApprovedAsync(
        UpdateArtifact artifact,
        string destinationPath,
        bool userApproved,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!userApproved)
            throw new UpdatePolicyException("update.approvalRequired", "Update download requires user approval.");
        ValidateArtifact(artifact);
        using HttpClient client = _httpClientFactory() ??
            throw new InvalidOperationException("Update HTTP client factory returned null.");
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUri);
        using HttpResponseMessage response = await SendWithControlledRedirectsAsync(
            client, request, IsTrustedGitHubDownloadUri, maximumRedirects: 3,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != artifact.ByteSize)
            throw new InvalidDataException("Update artifact size differs from the signed manifest.");
        string destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyAndVerifyAsync(response.Content, temporary, artifact, cancellationToken).ConfigureAwait(false);
            if (artifact.CodeSigning == ArtifactCodeSigningPolicies.Authenticode)
                AuthenticodeVerifier.Verify(temporary, artifact.AuthenticodePublisher!);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void ValidateManifest(
        ReleaseManifest manifest,
        UpdateCheckContext context,
        IReadOnlyDictionary<string, Uri> assetUris,
        string releaseTag)
    {
        if (manifest.SchemaVersion != 1 || manifest.ReleaseSequence < 1 ||
            manifest.Channel != context.Channel || manifest.Architecture != context.Architecture ||
            manifest.MinimumWindowsBuild < 22621 || manifest.Artifacts.Count is < 1 or > 16)
            throw new InvalidDataException("Release manifest policy fields are invalid.");
        if (!releaseTag.Equals("v" + manifest.ReleaseVersion, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub release tag does not match the signed manifest version.");
        if (manifest.Artifacts.Select(artifact => artifact.FileName).Distinct(StringComparer.Ordinal).Count() !=
            manifest.Artifacts.Count)
            throw new InvalidDataException("Release manifest contains duplicate artifact names.");
        foreach (ReleaseManifestArtifact artifact in manifest.Artifacts)
        {
            if (Path.GetFileName(artifact.FileName) != artifact.FileName ||
                !assetUris.ContainsKey(artifact.FileName) || artifact.ByteSize < 1 ||
                artifact.ByteSize > MaximumArtifactBytes || artifact.Sha256.Length != 64 ||
                !artifact.Sha256.All(char.IsAsciiHexDigit) ||
                HasInvalidCodeSigningPolicy(
                    artifact.FileName,
                    artifact.CodeSigning,
                    artifact.AuthenticodePublisher))
                throw new InvalidDataException("Release manifest artifact is invalid.");
        }
    }

    private static async Task<byte[]> GetBoundedAsync(
        HttpClient client,
        HttpRequestMessage request,
        int maximumBytes,
        Func<Uri, bool> finalUriPolicy,
        int maximumRedirects,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithControlledRedirectsAsync(
            client, request, finalUriPolicy, maximumRedirects, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and long length && length > maximumBytes)
            throw new InvalidDataException("Update metadata exceeds its size limit.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > maximumBytes)
                throw new InvalidDataException("Update metadata exceeds its size limit.");
            destination.Write(buffer, 0, read);
        }
    }

    private static async Task<HttpResponseMessage> SendWithControlledRedirectsAsync(
        HttpClient client,
        HttpRequestMessage initialRequest,
        Func<Uri, bool> uriPolicy,
        int maximumRedirects,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage request = initialRequest;
        bool ownsRequest = false;
        try
        {
            for (int redirectCount = 0; ; redirectCount++)
            {
                HttpResponseMessage response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is not Uri actualUri ||
                    request.RequestUri is not Uri requestedUri || actualUri != requestedUri)
                {
                    response.Dispose();
                    throw new InvalidDataException(
                        "Update HTTP client performed an automatic redirect; automatic redirects must be disabled.");
                }
                if (!uriPolicy(actualUri))
                {
                    response.Dispose();
                    throw new InvalidDataException("Update request left its trusted host boundary.");
                }
                if (!IsRedirect(response.StatusCode))
                {
                    if (ownsRequest) request.Dispose();
                    return response;
                }
                if (redirectCount >= maximumRedirects || response.Headers.Location is not Uri location)
                {
                    response.Dispose();
                    throw new InvalidDataException("Update redirect policy was exceeded.");
                }

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(actualUri, location);
                if (!uriPolicy(nextUri))
                {
                    response.Dispose();
                    throw new InvalidDataException("Update redirect left its trusted host boundary.");
                }
                var nextRequest = new HttpRequestMessage(HttpMethod.Get, nextUri);
                bool sameOrigin = actualUri.Scheme == nextUri.Scheme &&
                    actualUri.Host == nextUri.Host && actualUri.Port == nextUri.Port;
                foreach (var header in request.Headers)
                {
                    bool credentialHeader = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase);
                    if (sameOrigin || !credentialHeader)
                        nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                response.Dispose();
                if (ownsRequest) request.Dispose();
                request = nextRequest;
                ownsRequest = true;
            }
        }
        catch
        {
            if (ownsRequest) request.Dispose();
            throw;
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is 301 or 302 or 303 or 307 or 308;

    private static void ValidateArtifact(UpdateArtifact artifact)
    {
        if (!IsTrustedGitHubDownloadUri(artifact.DownloadUri) ||
            artifact.DownloadUri.Host != "github.com" ||
            Path.GetFileName(artifact.FileName) != artifact.FileName ||
            artifact.ByteSize is < 1 or > MaximumArtifactBytes ||
            artifact.Sha256.Length != 64 || !artifact.Sha256.All(char.IsAsciiHexDigit) ||
            HasInvalidCodeSigningPolicy(
                artifact.FileName,
                artifact.CodeSigning,
                artifact.AuthenticodePublisher))
            throw new ArgumentException("Update artifact metadata is invalid.", nameof(artifact));
    }

    private static bool HasInvalidCodeSigningPolicy(
        string fileName,
        string codeSigning,
        string? publisher) =>
        IsExecutableInstaller(fileName)
            ? codeSigning switch
            {
                ArtifactCodeSigningPolicies.Unsigned => publisher is not null,
                ArtifactCodeSigningPolicies.Authenticode =>
                    string.IsNullOrWhiteSpace(publisher) || publisher.Length > 512,
                _ => true,
            }
            : codeSigning != ArtifactCodeSigningPolicies.NotApplicable ||
                publisher is not null;

    private static bool IsExecutableInstaller(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedGitHubDownloadUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host is
            "github.com" or "release-assets.githubusercontent.com" or
            "objects.githubusercontent.com";

    private static async Task CopyAndVerifyAsync(
        HttpContent content,
        string temporary,
        UpdateArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await VerifiedArtifactWriter.WriteAsync(
            input,
            temporary,
            artifact.ByteSize,
            artifact.Sha256,
            "Update artifact",
            reportBytes: null,
            cancellationToken).ConfigureAwait(false);
    }
}
