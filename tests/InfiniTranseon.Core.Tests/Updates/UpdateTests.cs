using InfiniTranseon.Contracts.Security;
using InfiniTranseon.Core.Updates;
using System.Net;
using System.Security.Cryptography;

namespace InfiniTranseon.Core.Tests.Updates;

public sealed class UpdateTests
{
    [Fact]
    public void Ed25519VerifierAcceptsRfc8032VectorAndRejectsTampering()
    {
        var key = new Ed25519PublicKey("rfc8032", Convert.FromHexString(
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"));
        byte[] signature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var verifier = new SignatureVerifier(new Ed25519TrustRootSet(key, null, []));

        Assert.True(verifier.VerifyDetached([], signature, key));
        Assert.False(verifier.VerifyDetached([1], signature, key));
    }

    [Fact]
    public void Ed25519VerifierAcceptsRfc8032SingleByteVector()
    {
        var key = new Ed25519PublicKey("rfc8032-2", Convert.FromHexString(
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c"));
        byte[] signature = Convert.FromHexString(
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
            "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        var verifier = new SignatureVerifier(new Ed25519TrustRootSet(key, null, []));

        Assert.True(verifier.VerifyDetached([0x72], signature, key));
        signature[10] ^= 1;
        Assert.False(verifier.VerifyDetached([0x72], signature, key));
    }

    [Fact]
    public void CanonicalManifestRejectsTamperingUnknownAndRevokedKeys()
    {
        const string signature =
            "tvQTIjfi/SekXO0NN9bfW8vQf2QEJ6/c3lpNqhqh925/94JNpY3yy7ATshfjpVEEkcLk19TfIQoIMGSOb9z6Cw==";
        byte[] trustedDocument = System.Text.Encoding.UTF8.GetBytes($$"""
            {"signatures":[{"keyId":"rfc8032","algorithm":"Ed25519","signature":"{{signature}}"}]}
            """);
        var verifier = new SignatureVerifier(new Ed25519TrustRootSet(TestKey(), null, []));

        Assert.Equal("rfc8032", verifier.VerifyCanonicalJson(trustedDocument));

        SignatureVerificationException tampered = Assert.Throws<SignatureVerificationException>(() =>
            verifier.VerifyCanonicalJson(System.Text.Encoding.UTF8.GetBytes($$"""
                {"sequence":1,"signatures":[{"keyId":"rfc8032","algorithm":"Ed25519","signature":"{{signature}}"}]}
                """)));
        SignatureVerificationException unknown = Assert.Throws<SignatureVerificationException>(() =>
            verifier.VerifyCanonicalJson(System.Text.Encoding.UTF8.GetBytes($$"""
                {"signatures":[{"keyId":"unknown","algorithm":"Ed25519","signature":"{{signature}}"}]}
                """)));
        var revokedVerifier = new SignatureVerifier(new Ed25519TrustRootSet(
            new Ed25519PublicKey("replacement", Enumerable.Repeat((byte)0x42, 32).ToArray()),
            null,
            ["rfc8032"]));
        SignatureVerificationException revoked = Assert.Throws<SignatureVerificationException>(() =>
            revokedVerifier.VerifyCanonicalJson(trustedDocument));

        Assert.Equal("signature.untrusted", tampered.Code);
        Assert.Equal("signature.untrusted", unknown.Code);
        Assert.Equal("signature.untrusted", revoked.Code);
    }

    [Fact]
    public void CanonicalManifestRejectsDuplicateJsonProperties()
    {
        const string signature =
            "tvQTIjfi/SekXO0NN9bfW8vQf2QEJ6/c3lpNqhqh925/94JNpY3yy7ATshfjpVEEkcLk19TfIQoIMGSOb9z6Cw==";
        byte[] document = System.Text.Encoding.UTF8.GetBytes($$"""
            {"signatures":[{"keyId":"unknown","algorithm":"Ed25519","signature":"{{signature}}"}],"signatures":[{"keyId":"rfc8032","algorithm":"Ed25519","signature":"{{signature}}"}]}
            """);
        var verifier = new SignatureVerifier(new Ed25519TrustRootSet(TestKey(), null, []));

        SignatureVerificationException error = Assert.Throws<SignatureVerificationException>(() =>
            verifier.VerifyCanonicalJson(document));

        Assert.Equal("signature.duplicateProperty", error.Code);
    }

    [Fact]
    public async Task StrictOfflineAndMissingApprovalRejectBeforeHttpClientConstruction()
    {
        int constructions = 0;
        Ed25519PublicKey key = TestKey();
        var service = new GitHubReleaseUpdateService(
            () =>
            {
                constructions++;
                return new HttpClient();
            },
            new Uri("https://api.github.com/repos/owner/repo/releases/latest"),
            new SignatureVerifier(new Ed25519TrustRootSet(key, null, [])),
            new SignedSequenceState());

        UpdatePolicyException offline = await Assert.ThrowsAsync<UpdatePolicyException>(() => service.CheckAsync(
            new UpdateCheckContext(true, false, false, true, new Version(1, 0, 0)),
            TestContext.Current.CancellationToken).AsTask());
        UpdatePolicyException approval = await Assert.ThrowsAsync<UpdatePolicyException>(() =>
            service.DownloadApprovedAsync(
                new UpdateArtifact(
                    new Uri("https://github.com/owner/repo/releases/download/v2/app.zip"),
                    "app.zip", 1, new string('0', 64),
                    ArtifactCodeSigningPolicies.NotApplicable, null),
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                false,
                TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.DownloadApprovedAsync(
            new UpdateArtifact(
                new Uri("https://example.com/app.zip"),
                "app.zip", 1, new string('0', 64),
                ArtifactCodeSigningPolicies.NotApplicable, null),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            true,
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("update.strictOffline", offline.Code);
        Assert.Equal("update.approvalRequired", approval.Code);
        Assert.Equal(0, constructions);
    }

    [Fact]
    public async Task SignedReleaseCheckBindsMetadataAndArtifacts()
    {
        using HttpClient client = SignedReleaseClient("v2.0.0");
        GitHubReleaseUpdateService service = Service(() => client);

        UpdateMetadata? update = await service.CheckAsync(
            new UpdateCheckContext(false, true, false, true, new Version(1, 0, 0)),
            TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal(new Version(2, 0, 0), update.Version);
        Assert.Equal(2, update.ReleaseSequence);
        Assert.Equal("rfc8032", update.VerifiedKeyId);
        UpdateArtifact artifact = Assert.Single(update.Artifacts);
        Assert.Equal("app.zip", artifact.FileName);
        Assert.Equal(
            new Uri("https://github.com/owner/repo/releases/download/v2.0.0/app.zip"),
            artifact.DownloadUri);
    }

    [Fact]
    public void SignedReleaseFixtureUsesTheRuntimeCanonicalBytes()
    {
        const string expected =
            "{\"architecture\":\"win-x64\",\"artifacts\":[{\"byteSize\":7,\"codeSigning\":\"not-applicable\",\"fileName\":\"app.zip\",\"sha256\":\"a4d451ec23463726f72c43d64c710968f6b602cd653b4de8adee1b556240a829\"}],\"channel\":\"stable\",\"minimumWindowsBuild\":22621,\"publishedAtUtc\":\"2026-07-21T00:00:00\\u002B00:00\",\"releaseSequence\":2,\"releaseVersion\":\"2.0.0\",\"schemaVersion\":1}";
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(SignedReleaseManifest());
        byte[] canonical = SignatureVerifier.CanonicalizeWithoutSignatures(document.RootElement);

        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(canonical));
        Assert.True(new SignatureVerifier(new Ed25519TrustRootSet(TestKey(), null, []))
            .VerifyDetached(
                canonical,
                Convert.FromBase64String(
                    "jP4LQY1rJi2wSAgjJ9/CIA5+/lX5YpLU4hb2EOxo+H247/0ZeGaWm8IPHmroPlM3KRUGCjgtJNuTL9FJ11zJAw=="),
                TestKey()));
    }

    [Fact]
    public async Task SignedReleaseCheckRejectsTagMismatchAndDowngrade()
    {
        using HttpClient mismatchedClient = SignedReleaseClient("v2.0.1");
        GitHubReleaseUpdateService mismatched = Service(() => mismatchedClient);
        using HttpClient downgradeClient = SignedReleaseClient("v2.0.0");
        var sequence = new SignedSequenceState();
        Assert.True(sequence.TryAccept(3));
        var downgrade = new GitHubReleaseUpdateService(
            () => downgradeClient,
            new Uri("https://api.github.com/repos/owner/repo/releases/latest"),
            new SignatureVerifier(new Ed25519TrustRootSet(TestKey(), null, [])),
            sequence);

        await Assert.ThrowsAsync<InvalidDataException>(() => mismatched.CheckAsync(
            new UpdateCheckContext(false, true, false, true, new Version(1, 0, 0)),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => downgrade.CheckAsync(
            new UpdateCheckContext(false, true, false, true, new Version(1, 0, 0)),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void SequenceStateSurvivesRestartAndCorruptionFailsClosed()
    {
        string directory = Path.Combine(Path.GetTempPath(), "infini-sequence-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "stable.json");
        try
        {
            var first = new FileSignedSequenceState(path, "release:stable");
            Assert.True(first.TryAccept(12));
            var restarted = new FileSignedSequenceState(path, "release:stable");
            Assert.Equal(12, restarted.HighestAccepted);
            Assert.False(restarted.TryAccept(11));
            File.WriteAllText(path, "{}");
            Assert.Throws<InvalidDataException>(() => new FileSignedSequenceState(path, "release:stable"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConcurrentSequenceStateInstancesCannotOverwriteANewerAcceptedSequence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "infini-sequence-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "stable.json");
        try
        {
            var first = new FileSignedSequenceState(path, "release:stable");
            var staleSecondInstance = new FileSignedSequenceState(path, "release:stable");

            Assert.True(first.TryAccept(10));
            Assert.False(staleSecondInstance.TryAccept(5));
            Assert.Equal(10, staleSecondInstance.HighestAccepted);
            Assert.Equal(10, new FileSignedSequenceState(path, "release:stable").HighestAccepted);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadRejectsAutomaticRedirectHiddenByHttpClientHandler()
    {
        byte[] payload = "release"u8.ToArray();
        UpdateArtifact artifact = Artifact(payload);
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            var redirectedRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "https://release-assets.githubusercontent.com/owner/release/app.zip");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = redirectedRequest,
                Content = new ByteArrayContent(payload),
            };
        }));
        var service = Service(() => client);
        string destination = Path.Combine(Path.GetTempPath(), "infini-update-" + Guid.NewGuid().ToString("N"), "app.zip");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadApprovedAsync(
                artifact, destination, true, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("automatic redirect", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadRejectsTamperingAndDeletesPartialFile()
    {
        byte[] expected = "release"u8.ToArray();
        byte[] tampered = "replace"u8.ToArray();
        UpdateArtifact artifact = Artifact(expected);
        using var client = new HttpClient(new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(tampered),
        }));
        var service = Service(() => client);
        string directory = Path.Combine(Path.GetTempPath(), "infini-update-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "app.zip");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadApprovedAsync(
                artifact, destination, true, TestContext.Current.CancellationToken).AsTask());

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.partial-*"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecutableUpdateArtifactRequiresAnExplicitCodeSigningPolicy()
    {
        byte[] payload = "release"u8.ToArray();
        int clientConstructions = 0;
        var artifact = new UpdateArtifact(
            new Uri("https://github.com/owner/repo/releases/download/v2/app.msi"),
            "app.msi",
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)),
            ArtifactCodeSigningPolicies.NotApplicable,
            null);
        var service = Service(() =>
        {
            clientConstructions++;
            return new HttpClient(new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload),
            }));
        });

        await Assert.ThrowsAsync<ArgumentException>(() => service.DownloadApprovedAsync(
            artifact,
            Path.Combine(Path.GetTempPath(), "infini-update-" + Guid.NewGuid().ToString("N"), "app.msi"),
            true,
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, clientConstructions);
    }

    [Fact]
    public async Task ExplicitlyUnsignedInstallerUsesSignedManifestHashAndPublishesAtomically()
    {
        byte[] payload = "release"u8.ToArray();
        var artifact = new UpdateArtifact(
            new Uri("https://github.com/owner/repo/releases/download/v2/app.msi"),
            "app.msi",
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)),
            ArtifactCodeSigningPolicies.Unsigned,
            null);
        using var client = new HttpClient(new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(payload),
        }));
        var service = Service(() => client);
        string directory = Path.Combine(Path.GetTempPath(), "infini-update-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "app.msi");
        try
        {
            string result = await service.DownloadApprovedAsync(
                artifact, destination, true, TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(destination), result);
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFollowsExplicitTrustedRedirectAndPublishesAtomically()
    {
        byte[] payload = "release"u8.ToArray();
        UpdateArtifact artifact = Artifact(payload);
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            if (requests == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
                redirect.Headers.Location = new Uri(
                    "https://release-assets.githubusercontent.com/owner/release/app.zip");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload),
            };
        }));
        var service = Service(() => client);
        string directory = Path.Combine(Path.GetTempPath(), "infini-update-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "app.zip");
        try
        {
            string result = await service.DownloadApprovedAsync(
                artifact, destination, true, TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(destination), result);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal(2, requests);
            Assert.Empty(Directory.GetFiles(directory, "*.partial-*"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static GitHubReleaseUpdateService Service(Func<HttpClient> factory) => new(
        factory,
        new Uri("https://api.github.com/repos/owner/repo/releases/latest"),
        new SignatureVerifier(new Ed25519TrustRootSet(TestKey(), null, [])),
        new SignedSequenceState());

    private static HttpClient SignedReleaseClient(string tag)
    {
        string manifest = SignedReleaseManifest();
        string metadata = $$"""
            {"tag_name":"{{tag}}","assets":[{"name":"release-manifest.json","browser_download_url":"https://github.com/owner/repo/releases/download/v2.0.0/release-manifest.json"},{"name":"app.zip","browser_download_url":"https://github.com/owner/repo/releases/download/v2.0.0/app.zip"}]}
            """;
        return new HttpClient(new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(
                request.RequestUri!.AbsolutePath.EndsWith("/release-manifest.json", StringComparison.Ordinal)
                    ? manifest
                    : metadata)),
        }));
    }

    private static string SignedReleaseManifest() => """
        {"schemaVersion":1,"releaseSequence":2,"releaseVersion":"2.0.0","channel":"stable","architecture":"win-x64","minimumWindowsBuild":22621,"publishedAtUtc":"2026-07-21T00:00:00+00:00","artifacts":[{"fileName":"app.zip","byteSize":7,"sha256":"a4d451ec23463726f72c43d64c710968f6b602cd653b4de8adee1b556240a829","codeSigning":"not-applicable"}],"signatures":[{"keyId":"rfc8032","algorithm":"Ed25519","signature":"jP4LQY1rJi2wSAgjJ9/CIA5+/lX5YpLU4hb2EOxo+H247/0ZeGaWm8IPHmroPlM3KRUGCjgtJNuTL9FJ11zJAw=="}]}
        """;

    private static UpdateArtifact Artifact(byte[] payload) => new(
        new Uri("https://github.com/owner/repo/releases/download/v2/app.zip"),
        "app.zip",
        payload.Length,
        Convert.ToHexString(SHA256.HashData(payload)),
        ArtifactCodeSigningPolicies.NotApplicable,
        null);

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private static Ed25519PublicKey TestKey() => new("rfc8032", Convert.FromHexString(
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"));
}
