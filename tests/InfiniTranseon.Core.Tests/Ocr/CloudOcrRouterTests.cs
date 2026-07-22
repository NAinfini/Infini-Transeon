using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Runtime;
using System.Net;
using System.Text;
using System.Text.Json;

namespace InfiniTranseon.Core.Tests.Ocr;

public sealed class CloudOcrRouterTests
{
    [Fact]
    public async Task TencentCloudOcrSignsOnlyTheAuthorizedCropAndNormalizesCoordinates()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new RecordingHandler(request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"攻击 100\",\"Confidence\":98,\"ItemPolygon\":{\"X\":20,\"Y\":10,\"Width\":100,\"Height\":20}}],\"RequestId\":\"id\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var options = new TencentCloudOcrOptions(
            new Uri("https://ocr.tencentcloudapi.com/"),
            "ap-guangzhou",
            "secret-id-ref",
            "secret-key-ref",
            null,
            ProxyPolicy.System,
            Clock: () => DateTimeOffset.FromUnixTimeSeconds(1_551_113_065));
        var provider = new TencentCloudOcrProvider(options, new HttpClient(handler), credentials);
        await credentials.WriteAsync("secret-id-ref", "AKIDEXAMPLE",
            provider.CreateCredentialBinding("secret-id"), CancellationToken.None);
        await credentials.WriteAsync("secret-key-ref", "example-secret",
            provider.CreateCredentialBinding("secret-key"), CancellationToken.None);
        OcrExecutionToken token = Token(1);

        OcrResultSnapshot result = await provider.RecognizeAsync(
            new CloudOcrProviderRequest(token, "image/png", new byte[] { 1, 2, 3 }, 200, 100),
            CancellationToken.None);

        Assert.Equal("GeneralBasicOCR", captured!.Headers.GetValues("X-TC-Action").Single());
        Assert.Equal("2018-11-19", captured.Headers.GetValues("X-TC-Version").Single());
        using JsonDocument requestBody = JsonDocument.Parse(body!);
        Assert.Equal("AQID", requestBody.RootElement.GetProperty("ImageBase64").GetString());
        TextLine line = Assert.Single(result.Lines);
        Assert.Equal("攻击 100", line.Text);
        Assert.Equal(0.1, line.Bounds.X, 6);
        Assert.Equal(0.1, line.Bounds.Y, 6);
        Assert.Equal(0.5, line.Bounds.Width, 6);
        Assert.Equal(0.2, line.Bounds.Height, 6);
        Assert.Equal(0.98, line.Confidence, 6);
    }

    [Fact]
    public async Task AzureVisionSendsBinaryCropAndParsesReadLines()
    {
        HttpRequestMessage? captured = null;
        byte[]? body = null;
        var handler = new RecordingHandler(request =>
        {
            captured = request;
            body = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return JsonResponse("{\"modelVersion\":\"2024-02-01\",\"readResult\":{\"blocks\":[{\"lines\":[{\"text\":\"Attack 100\",\"boundingPolygon\":[{\"x\":20,\"y\":10},{\"x\":120,\"y\":10},{\"x\":120,\"y\":30},{\"x\":20,\"y\":30}],\"words\":[{\"text\":\"Attack\",\"confidence\":0.9},{\"text\":\"100\",\"confidence\":0.8}]}]}]}}");
        });
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var options = new AzureVisionOcrOptions(
            new Uri("https://example.cognitiveservices.azure.com/"),
            "key-ref",
            ProxyPolicy.System);
        var provider = new AzureVisionOcrProvider(options, new HttpClient(handler), credentials);
        await credentials.WriteAsync(
            "key-ref", "secret", provider.CreateCredentialBinding(), CancellationToken.None);

        OcrResultSnapshot result = await provider.RecognizeAsync(
            new CloudOcrProviderRequest(Token(1), "image/png", new byte[] { 1, 2, 3 }, 200, 100),
            CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, body);
        Assert.Equal("secret", captured!.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        Assert.Contains("features=read", captured.RequestUri!.Query, StringComparison.Ordinal);
        TextLine line = Assert.Single(result.Lines);
        Assert.Equal("Attack 100", line.Text);
        AssertRect(line.Bounds, 0.1, 0.1, 0.5, 0.2);
        Assert.Equal(0.85, line.Confidence, 6);
    }

    [Fact]
    public async Task CloudOcrRejectsAnAutomaticRedirectHiddenByTheHttpHandler()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Post, "https://other.example/steal-crop"),
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var provider = new AzureVisionOcrProvider(
            new AzureVisionOcrOptions(
                new Uri("https://example.cognitiveservices.azure.com/"),
                "key-ref",
                ProxyPolicy.System),
            new HttpClient(handler),
            credentials);
        await credentials.WriteAsync(
            "key-ref", "secret", provider.CreateCredentialBinding(), CancellationToken.None);

        OcrRoutingException error = await Assert.ThrowsAsync<OcrRoutingException>(() =>
            provider.RecognizeAsync(
                new CloudOcrProviderRequest(
                    Token(1), "image/png", new byte[] { 1, 2, 3 }, 200, 100),
                CancellationToken.None).AsTask());

        Assert.Equal("ocr.redirectRejected", error.Code);
    }

    [Fact]
    public async Task GoogleVisionUsesHeaderKeyAndParsesParagraphGeometry()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new RecordingHandler(request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("{\"responses\":[{\"fullTextAnnotation\":{\"pages\":[{\"blocks\":[{\"paragraphs\":[{\"confidence\":0.96,\"boundingBox\":{\"vertices\":[{\"x\":10,\"y\":20},{\"x\":110,\"y\":20},{\"x\":110,\"y\":40},{\"x\":10,\"y\":40}]},\"words\":[{\"symbols\":[{\"text\":\"H\"},{\"text\":\"i\",\"property\":{\"detectedBreak\":{\"type\":\"SPACE\"}}}]},{\"symbols\":[{\"text\":\"!\"}]}]}]}]}]}}]}");
        });
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var options = new GoogleVisionOcrOptions(
            new Uri("https://vision.googleapis.com/v1/images:annotate"),
            "key-ref",
            ProxyPolicy.System,
            ["en"]);
        var provider = new GoogleVisionOcrProvider(options, new HttpClient(handler), credentials);
        await credentials.WriteAsync(
            "key-ref", "secret", provider.CreateCredentialBinding(), CancellationToken.None);

        OcrResultSnapshot result = await provider.RecognizeAsync(
            new CloudOcrProviderRequest(Token(1), "image/png", new byte[] { 1, 2, 3 }, 200, 100),
            CancellationToken.None);

        Assert.Equal("secret", captured!.Headers.GetValues("x-goog-api-key").Single());
        using JsonDocument requestJson = JsonDocument.Parse(body!);
        Assert.Equal("AQID", requestJson.RootElement.GetProperty("requests")[0]
            .GetProperty("image").GetProperty("content").GetString());
        TextLine line = Assert.Single(result.Lines);
        Assert.Equal("Hi !", line.Text);
        AssertRect(line.Bounds, 0.05, 0.2, 0.5, 0.2);
        Assert.Equal(0.96, line.Confidence, 6);
    }

    [Fact]
    public async Task BaiduOcrObtainsTokenThenSendsOnlyEncodedCrop()
    {
        var requests = new List<(Uri Uri, string Body)>();
        var handler = new RecordingHandler(request =>
        {
            string requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requests.Add((request.RequestUri!, requestBody));
            if (request.RequestUri!.AbsolutePath.Contains("oauth", StringComparison.Ordinal))
                return JsonResponse("{\"access_token\":\"token\",\"expires_in\":2592000}");
            return JsonResponse("{\"log_id\":1,\"words_result_num\":1,\"words_result\":[{\"words\":\"生命 200\",\"probability\":{\"average\":0.93},\"location\":{\"left\":20,\"top\":10,\"width\":100,\"height\":20}}]}");
        });
        var credentials = new BoundCredentialStore(new MemoryCredentialStore());
        var options = new BaiduOcrOptions(
            new Uri("https://aip.baidubce.com/oauth/2.0/token"),
            new Uri("https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic"),
            "id-ref",
            "secret-ref",
            ProxyPolicy.System,
            IncludeLocation: true);
        var provider = new BaiduOcrProvider(options, new HttpClient(handler), credentials);
        await credentials.WriteAsync(
            "id-ref", "client-id", provider.CreateCredentialBinding("client-id"), CancellationToken.None);
        await credentials.WriteAsync(
            "secret-ref", "client-secret", provider.CreateCredentialBinding("client-secret"), CancellationToken.None);

        OcrResultSnapshot result = await provider.RecognizeAsync(
            new CloudOcrProviderRequest(Token(1), "image/png", new byte[] { 1, 2, 3 }, 200, 100),
            CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain("client_secret", requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("access_token=token", requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("image=AQID", requests[1].Body, StringComparison.Ordinal);
        TextLine line = Assert.Single(result.Lines);
        Assert.Equal("生命 200", line.Text);
        AssertRect(line.Bounds, 0.1, 0.1, 0.5, 0.2);
        Assert.Equal(0.93, line.Confidence, 6);
    }

    [Fact]
    public async Task StrictOfflineRejectsBeforeConstructingNetworkProvider()
    {
        int factoryCalls = 0;
        var router = CreateRouter(() =>
        {
            factoryCalls++;
            return new StubProvider(CreateResult);
        });
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);

        OcrRoutingException error = await Assert.ThrowsAsync<OcrRoutingException>(() => router.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(token, "image/png", [1, 2, 3], 10, 10, true),
            strictOffline: true,
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("ocr.policy.strictOffline", error.Code);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task ConsentIsRequiredBeforeConstructingProvider()
    {
        int factoryCalls = 0;
        var router = CreateRouter(() =>
        {
            factoryCalls++;
            return new StubProvider(CreateResult);
        });
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);

        OcrRoutingException error = await Assert.ThrowsAsync<OcrRoutingException>(() => router.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(token, "image/png", [1], 10, 10, false),
            strictOffline: false,
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("ocr.policy.cloudConsentRequired", error.Code);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task RouterReusesProviderAndOwnsItsDisposal()
    {
        int factoryCalls = 0;
        int disposals = 0;
        var router = new CloudOcrRouter([
            new OcrProviderRegistration("cloud", true, () =>
            {
                factoryCalls++;
                return new DisposableStubProvider(
                    request => CreateResult(request),
                    () => disposals++);
            }),
        ]);
        OcrExecutionToken first = router.BeginAttempt(CreateSource(), 1);
        OcrExecutionToken second = router.BeginAttempt(CreateSource(), 1);

        await router.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(first, "image/png", [1], 10, 10, true),
            false,
            TestContext.Current.CancellationToken);
        await router.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(second, "image/png", [2], 10, 10, true),
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, disposals);
        Assert.IsAssignableFrom<IDisposable>(router).Dispose();
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task DisposeStopsAdmissionButLetsAnActiveRouteFinishBeforeProviderDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposals = 0;
        var router = new CloudOcrRouter([
            new OcrProviderRegistration("cloud", true, () =>
                new DisposableAsyncStubProvider(
                    async (request, cancellationToken) =>
                    {
                        entered.SetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return CreateResult(request);
                    },
                    () => disposals++)),
        ]);
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);

        Task<OcrResultSnapshot> activeRoute = router.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(token, "image/png", [1], 10, 10, true),
            false,
            TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        router.Dispose();
        Assert.Equal(0, disposals);
        Assert.Throws<ObjectDisposedException>(() => router.BeginAttempt(CreateSource(), 1));

        release.SetResult();
        OcrResultSnapshot result = await activeRoute;

        Assert.Equal(token, result.ExecutionToken);
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task AuthorizedCropProducesUnifiedResultAndAdvancesSequenceOnlyAfterSuccess()
    {
        var router = CreateRouter(() => new StubProvider(CreateResult));
        OcrExecutionToken first = router.BeginAttempt(CreateSource(), 1);

        OcrResultSnapshot result = await router.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(first, "image/png", [1, 2, 3], 100, 40, true),
            strictOffline: false,
            TestContext.Current.CancellationToken);
        OcrExecutionToken second = router.NextToken(first);

        Assert.Equal(first, result.ExecutionToken);
        Assert.Equal(2, second.ResultSequence);
        Assert.Equal("cloud-model", result.ModelId);
    }

    [Fact]
    public async Task MalformedProviderTokenIsRejectedAndSameSequenceCanRetry()
    {
        bool malformed = true;
        var router = CreateRouter(() => new StubProvider(request => malformed
            ? CreateResult(request with
            {
                ExecutionToken = new OcrExecutionToken(
                    request.ExecutionToken.Source,
                    Guid.NewGuid(),
                    request.ExecutionToken.Attempt,
                    request.ExecutionToken.ResultSequence),
            })
            : CreateResult(request)));
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);
        var request = new CloudOcrRouteRequest(token, "image/png", [1], 10, 10, true);

        await Assert.ThrowsAsync<OcrRoutingException>(() => router.RouteAsync(
            "cloud", request, false, TestContext.Current.CancellationToken).AsTask());
        malformed = false;
        OcrResultSnapshot retried = await router.RouteAsync(
            "cloud", request, false, TestContext.Current.CancellationToken);

        Assert.Equal(token, retried.ExecutionToken);
    }

    [Fact]
    public async Task OversizedMalformedResultAndCancellationAreExplicit()
    {
        var oversized = CreateRouter(() => new StubProvider(request => new OcrResultSnapshot(
            request.ExecutionToken,
            Enumerable.Repeat(new TextLine("x", new NormalizedRect(0, 0, 1, 1), 1), 2049).ToArray(),
            "model",
            "1",
            true,
            null)));
        OcrExecutionToken token = oversized.BeginAttempt(CreateSource(), 1);
        await Assert.ThrowsAsync<OcrRoutingException>(() => oversized.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(token, "image/png", [1], 10, 10, true),
            false,
            TestContext.Current.CancellationToken).AsTask());

        var cancelled = CreateRouter(() => new StubProvider(async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return CreateResult(request);
        }));
        using var cancellation = new CancellationTokenSource();
        OcrExecutionToken cancelledToken = cancelled.BeginAttempt(CreateSource(), 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(cancelledToken, "image/png", [1], 10, 10, true),
            false,
            cancellation.Token).AsTask());
    }

    [Fact]
    public async Task CloudCropDeadlineCancelsAProviderThatDoesNotFinishInTime()
    {
        var router = CreateRouter(() => new StubProvider(async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            return CreateResult(request);
        }));
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);

        OcrRoutingException error = await Assert.ThrowsAsync<OcrRoutingException>(() => router.RouteAsync(
            "cloud",
            new CloudOcrRouteRequest(
                token,
                "image/png",
                [1],
                10,
                10,
                true,
                deadlineUtc: DateTimeOffset.UtcNow.AddMilliseconds(50)),
            false,
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("ocr.deadline.expired", error.Code);
    }

    [Fact]
    public async Task RuntimeDispatcherRoutesAuthorizedCropThroughEngineHostOnly()
    {
        var router = CreateRouter(() => new StubProvider(CreateResult));
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        using var crop = new CloudOcrCropRequest(
            token, "image/png", [1, 2, 3], 100, 40, true,
            consentPolicyRevision: 5,
            encodedByteCeiling: 1024,
            deadlineUtc: deadline,
            providerId: "cloud");
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            token.Source.RuntimeEpoch,
            deadline,
            RuntimeCloudOcrCropRequestPayloadCodec.Encode(crop));
        OcrResultSnapshot? received = null;
        var dispatcher = new RuntimeCloudOcrDispatcher(
            router,
            new StubResultSink(result =>
            {
                received = result;
            }),
            _ => false);

        await dispatcher.DispatchAsync(
            runtimeEvent, TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal(token, received.ExecutionToken);
        Assert.Null(received.TerminalErrorCode);
    }

    [Fact]
    public async Task RuntimeDispatcherReturnsProviderFailureWithoutTerminatingEventPump()
    {
        var router = CreateRouter(() => new StubProvider((_, _) =>
            ValueTask.FromException<OcrResultSnapshot>(
                new OcrRoutingException("ocr.network", "provider unavailable"))));
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        using var crop = new CloudOcrCropRequest(
            token, "image/png", [1], 10, 10, true,
            deadlineUtc: deadline,
            providerId: "cloud");
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            token.Source.RuntimeEpoch,
            deadline,
            RuntimeCloudOcrCropRequestPayloadCodec.Encode(crop));
        OcrResultSnapshot? received = null;
        var dispatcher = new RuntimeCloudOcrDispatcher(
            router,
            new StubResultSink(result => received = result),
            _ => false);

        await dispatcher.DispatchAsync(
            runtimeEvent, TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal(token, received.ExecutionToken);
        Assert.Empty(received.Lines);
        Assert.False(received.IsStable);
        Assert.Equal("ocr.network", received.TerminalErrorCode);
        OcrRoutingException replay = await Assert.ThrowsAsync<OcrRoutingException>(() =>
            router.RouteAsync(
                "cloud",
                new CloudOcrRouteRequest(token, "image/png", [1], 10, 10, true),
                false,
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("ocr.sequence.outOfOrder", replay.Code);
    }

    [Fact]
    public async Task RuntimeDispatcherMapsUnexpectedProviderExceptionToStableFailure()
    {
        var router = CreateRouter(() => new StubProvider((_, _) =>
            ValueTask.FromException<OcrResultSnapshot>(
                new InvalidOperationException("secret provider detail"))));
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        using var crop = new CloudOcrCropRequest(
            token, "image/png", [1], 10, 10, true,
            deadlineUtc: deadline,
            providerId: "cloud");
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            token.Source.RuntimeEpoch,
            deadline,
            RuntimeCloudOcrCropRequestPayloadCodec.Encode(crop));
        OcrResultSnapshot? received = null;
        var dispatcher = new RuntimeCloudOcrDispatcher(
            router,
            new StubResultSink(result => received = result),
            _ => false);

        await dispatcher.DispatchAsync(
            runtimeEvent, TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal("ocr.provider.unhandledFailure", received.TerminalErrorCode);
    }

    [Fact]
    public async Task RuntimeDispatcherRejectsEpochMismatchBeforeProviderConstruction()
    {
        int factoryCalls = 0;
        var router = CreateRouter(() =>
        {
            factoryCalls++;
            return new StubProvider(CreateResult);
        });
        OcrExecutionToken token = router.BeginAttempt(CreateSource(), 1);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        using var crop = new CloudOcrCropRequest(
            token, "image/png", [1], 10, 10, true,
            deadlineUtc: deadline,
            providerId: "cloud");
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            RuntimeCloudOcrCropRequestPayloadCodec.Encode(crop));
        var dispatcher = new RuntimeCloudOcrDispatcher(
            router,
            new StubResultSink(_ => { }),
            _ => false);

        OcrRoutingException error = await Assert.ThrowsAsync<OcrRoutingException>(() =>
            dispatcher.DispatchAsync(
                runtimeEvent, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("ocr.runtime.epochMismatch", error.Code);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task RuntimeDispatcherAppliesStrictOfflinePolicyForTheCropTarget()
    {
        int factoryCalls = 0;
        var router = CreateRouter(() =>
        {
            factoryCalls++;
            return new StubProvider(CreateResult);
        });
        SourceGenerationToken source = CreateSource();
        OcrExecutionToken token = router.BeginAttempt(source, 1);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        using var crop = new CloudOcrCropRequest(
            token, "image/png", [1], 10, 10, true,
            deadlineUtc: deadline,
            providerId: "cloud");
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            source.RuntimeEpoch,
            deadline,
            RuntimeCloudOcrCropRequestPayloadCodec.Encode(crop));
        OcrResultSnapshot? received = null;
        var dispatcher = new RuntimeCloudOcrDispatcher(
            router,
            new StubResultSink(result => received = result),
            targetInstanceId =>
            {
                Assert.Equal(source.TargetInstanceId, targetInstanceId);
                return true;
            });

        await dispatcher.DispatchAsync(
            runtimeEvent, TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal("ocr.policy.strictOffline", received.TerminalErrorCode);
        Assert.Equal(0, factoryCalls);
    }

    private static CloudOcrRouter CreateRouter(Func<IOcrProvider> factory) => new(
    [
        new OcrProviderRegistration("cloud", requiresNetwork: true, factory),
    ]);

    private static SourceGenerationToken CreateSource() => new(
        Guid.NewGuid(),
        new TargetInstanceId(Guid.NewGuid()),
        CaptureAreaKey.UserRegion(new RegionId(Guid.NewGuid())),
        new TextTrackId(Guid.NewGuid()),
        1,
        1);

    private static OcrExecutionToken Token(long sequence) => new(
        CreateSource(), Guid.NewGuid(), 1, sequence);

    private static OcrResultSnapshot CreateResult(CloudOcrProviderRequest request) => new(
        request.ExecutionToken,
        [new TextLine("text", new NormalizedRect(0, 0, 1, 1), 0.99)],
        "cloud-model",
        "1",
        true,
        null);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static void AssertRect(
        NormalizedRect actual,
        double x,
        double y,
        double width,
        double height)
    {
        Assert.Equal(x, actual.X, 6);
        Assert.Equal(y, actual.Y, 6);
        Assert.Equal(width, actual.Width, 6);
        Assert.Equal(height, actual.Height, 6);
    }

    private sealed class StubProvider : IOcrProvider
    {
        private readonly Func<CloudOcrProviderRequest, CancellationToken, ValueTask<OcrResultSnapshot>> _handler;

        public StubProvider(Func<CloudOcrProviderRequest, OcrResultSnapshot> handler)
            : this((request, _) => ValueTask.FromResult(handler(request))) { }

        public StubProvider(Func<CloudOcrProviderRequest, CancellationToken, ValueTask<OcrResultSnapshot>> handler)
        {
            _handler = handler;
        }

        public ValueTask<OcrResultSnapshot> RecognizeAsync(
            CloudOcrProviderRequest request,
            CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }

    private sealed class DisposableStubProvider(
        Func<CloudOcrProviderRequest, OcrResultSnapshot> handler,
        Action dispose) : IOcrProvider, IDisposable
    {
        public ValueTask<OcrResultSnapshot> RecognizeAsync(
            CloudOcrProviderRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(handler(request));
        }

        public void Dispose() => dispose();
    }

    private sealed class DisposableAsyncStubProvider(
        Func<CloudOcrProviderRequest, CancellationToken, ValueTask<OcrResultSnapshot>> handler,
        Action dispose) : IOcrProvider, IDisposable
    {
        public ValueTask<OcrResultSnapshot> RecognizeAsync(
            CloudOcrProviderRequest request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);

        public void Dispose() => dispose();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }

    private sealed class StubResultSink(Action<OcrResultSnapshot> callback) : IRuntimeOcrResultSink
    {
        public ValueTask SendAsync(OcrResultSnapshot result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            callback(result);
            return ValueTask.CompletedTask;
        }
    }
}
