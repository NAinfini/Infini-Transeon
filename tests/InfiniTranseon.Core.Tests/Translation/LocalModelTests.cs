using System.Net;
using System.Security.Cryptography;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Translation.Local;
using InfiniTranseon.Core.Updates;
using InfiniTranseon.ModelWorker;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Tests.Translation;

public sealed class LocalModelTests
{
    [Fact]
    public void PhraseTableRuntimeLoadsRequestedManagedModelAndTranslatesWithoutNetwork()
    {
        string root = Path.Combine(Path.GetTempPath(), "infini-phrase-" + Guid.NewGuid().ToString("N"));
        string tables = Path.Combine(root, "phrase-tables");
        Directory.CreateDirectory(tables);
        try
        {
            File.WriteAllText(Path.Combine(tables, "ja-en-basic.json"), """
                {
                  "schemaVersion": 1,
                  "modelId": "ja-en-basic",
                  "sourceLanguage": "ja",
                  "targetLanguage": "en",
                  "entries": [
                    { "source": "攻撃", "target": "Attack" },
                    { "source": "防御", "target": "Defense" }
                  ]
                }
                """);
            var runtime = new PhraseTableRuntime(root);

            PhraseTableTranslationResult result = runtime.Translate(
                "ja-en-basic", "ja", "en", "攻撃:100\n防御:80", 100);

            Assert.True(result.Success);
            Assert.Equal("Attack:100\nDefense:80", result.Text);
            Assert.Null(result.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PhraseTableUsesLongestSourceMatchWithoutRetranslatingGeneratedText()
    {
        using var temp = new TempDirectory();
        string tables = Path.Combine(temp.Path, "phrase-tables");
        Directory.CreateDirectory(tables);
        File.WriteAllText(Path.Combine(tables, "test.json"), """
            {
              "schemaVersion": 1,
              "modelId": "test",
              "sourceLanguage": "en",
              "targetLanguage": "x-test",
              "entries": [
                { "source": "attack", "target": "A" },
                { "source": "attack power", "target": "Power" },
                { "source": "a", "target": "b" },
                { "source": "b", "target": "c" }
              ]
            }
            """);
        var runtime = new PhraseTableRuntime(temp.Path);

        PhraseTableTranslationResult result = runtime.Translate(
            "test", "en", "x-test", "attack power: a x", 100);

        Assert.True(result.Success);
        Assert.Equal("Power: b x", result.Text);
    }

    [Fact]
    public void SandboxLaunchOptionsRequireManagedModelRootAndBoundedMemory()
    {
        string root = Path.Combine(Path.GetTempPath(), "infini-models-" + Guid.NewGuid().ToString("N"));
        var options = new LocalWorkerSandboxOptions(
            Path.Combine(root, "worker.exe"),
            null,
            root,
            root + "-scratch",
            4L * 1024 * 1024 * 1024,
            TimeSpan.FromSeconds(10));

        options.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with
        {
            MaximumCommittedBytes = LocalWorkerProtocol.DefaultMaximumCommittedBytes + 1,
        }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { ManagedModelDirectory = "." }).Validate());
    }
    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("C:/escape.bin")]
    [InlineData("nested\\escape.bin")]
    public void ManagedModelPathsRejectTraversalAndNonCanonicalSeparators(string relativePath)
    {
        using var temp = new TempDirectory();
        Assert.Throws<InvalidDataException>(() => ModelPathPolicy.ResolveManagedPath(temp.Path, relativePath));
    }

    [Fact]
    public void PhraseTableCatalogEntryMustInstallAtWorkerOwnedCanonicalPath()
    {
        var invalid = new ModelCatalogEntry(
            "ja-en-basic", "1", "Apache-2.0", "phrase-table-v1", 0,
            ["win-x64"], [new Uri("https://models.example.test/")],
            [new ModelCatalogFile("arbitrary.json", 10, new string('A', 64))]);
        var document = new ModelCatalogDocument(
            1, 1, DateTimeOffset.UtcNow, [invalid], []);

        Assert.Throws<InvalidDataException>(() => ModelCatalogService.Validate(document));
    }

    [Fact]
    public void ModelCatalogRejectsNegativeOrUnreasonablyLargeOpsets()
    {
        static ModelCatalogDocument Catalog(int opset) => new(
            1,
            1,
            DateTimeOffset.UtcNow,
            [new ModelCatalogEntry(
                "model", "1", "Apache-2.0", "future-runtime", opset,
                ["win-x64"], [new Uri("https://models.example.test/")],
                [new ModelCatalogFile("model.bin", 10, new string('A', 64))])],
            []);

        Assert.Throws<InvalidDataException>(() => ModelCatalogService.Validate(Catalog(-1)));
        Assert.Throws<InvalidDataException>(() => ModelCatalogService.Validate(Catalog(101)));
    }

    [Theory]
    [InlineData("https://models.example.test/releases")]
    [InlineData("https://models.example.test/releases/?token=secret")]
    [InlineData("https://models.example.test/releases/#fragment")]
    public void ModelCatalogRejectsAmbiguousOrCredentialBearingDownloadOrigins(string origin)
    {
        var document = new ModelCatalogDocument(
            1,
            1,
            DateTimeOffset.UtcNow,
            [new ModelCatalogEntry(
                "model", "1", "Apache-2.0", "runtime", 1,
                ["win-x64"], [new Uri(origin)],
                [new ModelCatalogFile("model.bin", 10, new string('A', 64))])],
            []);

        Assert.Throws<InvalidDataException>(() => ModelCatalogService.Validate(document));
    }

    [Fact]
    public void ModelCatalogRejectsDuplicateFilePaths()
    {
        var file = new ModelCatalogFile("model.bin", 10, new string('A', 64));
        var document = new ModelCatalogDocument(
            1,
            1,
            DateTimeOffset.UtcNow,
            [new ModelCatalogEntry(
                "model", "1", "Apache-2.0", "runtime", 1,
                ["win-x64"], [new Uri("https://models.example.test/")], [file, file])],
            []);

        Assert.Throws<InvalidDataException>(() => ModelCatalogService.Validate(document));
    }

    [Fact]
    public async Task ModelDownloadRequiresApprovalAndAtomicallyVerifiesSizeAndChecksum()
    {
        byte[] payload = "verified model"u8.ToArray();
        int requests = 0;
        int clientConstructions = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        });
        using var temp = new TempDirectory();
        Uri origin = new("https://models.example.test/releases/");
        var file = new ModelCatalogFile(
            "madlad/model.bin", payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var model = new ModelCatalogEntry(
            "madlad-3b-int8", "1", "Apache-2.0", "undecided", 1,
            ["win-x64"], [origin], [file]);
        var service = new ModelDownloadService(() =>
        {
            clientConstructions++;
            return new HttpClient(handler, disposeHandler: false);
        }, temp.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(
            Request(model, file, origin, approved: false),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(
            Request(model, file, origin, approved: true, strictOffline: true),
            TestContext.Current.CancellationToken).AsTask());
        string path = await service.DownloadAsync(
            Request(model, file, origin, approved: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, requests);
        Assert.Equal(1, clientConstructions);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ChecksumFailureDeletesPartialModel()
    {
        byte[] payload = "tampered"u8.ToArray();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        using var temp = new TempDirectory();
        Uri origin = new("https://models.example.test/");
        var file = new ModelCatalogFile("model.bin", payload.Length, new string('0', 64));
        var model = new ModelCatalogEntry(
            "model", "1", "Apache-2.0", "runtime", 1, ["win-x64"], [origin], [file]);
        var service = new ModelDownloadService(
            () => new HttpClient(handler, disposeHandler: false), temp.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(
            Request(model, file, origin, approved: true),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ModelDownloadRejectsAResponseRedirectedOutsideSignedOrigin()
    {
        byte[] payload = "model"u8.ToArray();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/model.bin"),
            Content = new ByteArrayContent(payload),
        });
        using var temp = new TempDirectory();
        Uri origin = new("https://models.example.test/");
        var file = new ModelCatalogFile(
            "model.bin", payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var model = new ModelCatalogEntry(
            "model", "1", "Apache-2.0", "runtime", 1, ["win-x64"], [origin], [file]);
        var service = new ModelDownloadService(
            () => new HttpClient(handler, disposeHandler: false), temp.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(
            Request(model, file, origin, approved: true),
            TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ModelDownloadRejectsAutomaticRedirectEvenInsideSignedOrigin()
    {
        byte[] payload = "model"u8.ToArray();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get, "https://models.example.test/other/model.bin"),
            Content = new ByteArrayContent(payload),
        });
        using var temp = new TempDirectory();
        Uri origin = new("https://models.example.test/");
        var file = new ModelCatalogFile(
            "model.bin", payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var model = new ModelCatalogEntry(
            "model", "1", "Apache-2.0", "runtime", 1, ["win-x64"], [origin], [file]);
        var service = new ModelDownloadService(
            () => new HttpClient(handler, disposeHandler: false), temp.Path);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAsync(
                Request(model, file, origin, approved: true),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("automatic redirect", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LocalProviderMapsWorkerResultWithoutRejectingStrictOffline()
    {
        var client = new StubLocalClient(request => new LocalTranslationResponse(
            LocalWorkerProtocol.Version,
            request.WorkerSessionEpoch,
            request.RequestId,
            true,
            "你好",
            null));
        var session = new StubLocalSession(client);
        await using var manager = new LocalWorkerSessionManager(
            _ => ValueTask.FromResult<ILocalWorkerSession>(session),
            new LocalWorkerSessionManagerOptions(TimeSpan.FromMinutes(1), PinWarm: false));
        var provider = new LocalTranslationProvider("local.madlad", "madlad-3b-int8", manager);

        IReadOnlyList<ProviderWireEvent> events = await CollectAsync(provider.StreamAsync(
            Request(strictOffline: true), CancellationToken.None));

        Assert.Equal("你好", Assert.IsType<ProviderDelta>(events[0]).Text);
        Assert.IsType<ProviderDone>(events[1]);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task LocalWorkerManagerStartsOnDemandAndStopsAfterIdleUnlessPinned()
    {
        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        int launches = 0;
        var session = new StubLocalSession(new StubLocalClient(request => new LocalTranslationResponse(
            LocalWorkerProtocol.Version,
            request.WorkerSessionEpoch,
            request.RequestId,
            false,
            null,
            "local.modelMissing")));
        await using var manager = new LocalWorkerSessionManager(
            _ =>
            {
                launches++;
                return ValueTask.FromResult<ILocalWorkerSession>(session);
            },
            new LocalWorkerSessionManagerOptions(TimeSpan.FromSeconds(30), PinWarm: false),
            () => now);

        await manager.TranslateAsync("model", "en", "zh-Hans", "hello", 100, CancellationToken.None);
        Assert.False(await manager.StopIfIdleAsync(now.AddSeconds(29), CancellationToken.None));
        Assert.True(await manager.StopIfIdleAsync(now.AddSeconds(30), CancellationToken.None));
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task LocalWorkerManagerAutomaticallyStopsAfterConfiguredIdleTimeout()
    {
        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var delayStarted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubLocalSession(new StubLocalClient(request => new LocalTranslationResponse(
            LocalWorkerProtocol.Version,
            request.WorkerSessionEpoch,
            request.RequestId,
            true,
            "done",
            null)));
        await using var manager = new LocalWorkerSessionManager(
            _ => ValueTask.FromResult<ILocalWorkerSession>(session),
            new LocalWorkerSessionManagerOptions(TimeSpan.FromSeconds(30), PinWarm: false),
            () => now,
            async (delay, cancellationToken) =>
            {
                delayStarted.TrySetResult(delay);
                await releaseDelay.Task.WaitAsync(cancellationToken);
            });

        await manager.TranslateAsync(
            "model", "en", "zh-Hans", "hello", 100, TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(30), await delayStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        now = now.AddSeconds(30);
        releaseDelay.SetResult();

        await session.Disposed.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task LocalWorkerManagerWaitsForAnActiveRequestBeforeDisposingItsSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BlockingLocalClient(started, release);
        var session = new StubLocalSession(client);
        var manager = new LocalWorkerSessionManager(
            _ => ValueTask.FromResult<ILocalWorkerSession>(session),
            new LocalWorkerSessionManagerOptions(TimeSpan.FromMinutes(1), PinWarm: true));

        Task<LocalTranslationResponse> translation = manager.TranslateAsync(
            "model", "en", "zh-Hans", "hello", 100,
            TestContext.Current.CancellationToken).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Task dispose = manager.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, session.DisposeCount);
        release.SetResult();
        Assert.True((await translation).Success);
        await dispose.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task LocalWorkerManagerRetiresACrashedSessionAndRelaunchesOnTheNextRequest()
    {
        int launches = 0;
        var crashed = new StubLocalSession(new ThrowingLocalClient(new IOException("worker exited")));
        var healthy = new StubLocalSession(new StubLocalClient(request => new LocalTranslationResponse(
            LocalWorkerProtocol.Version,
            request.WorkerSessionEpoch,
            request.RequestId,
            true,
            "done",
            null)));
        await using var manager = new LocalWorkerSessionManager(
            _ => ValueTask.FromResult<ILocalWorkerSession>(++launches == 1 ? crashed : healthy),
            new LocalWorkerSessionManagerOptions(TimeSpan.FromMinutes(1), PinWarm: true));

        await Assert.ThrowsAsync<IOException>(() => manager.TranslateAsync(
            "model", "en", "zh-Hans", "hello", 100,
            TestContext.Current.CancellationToken).AsTask());
        LocalTranslationResponse response = await manager.TranslateAsync(
            "model", "en", "zh-Hans", "hello", 100,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(2, launches);
        Assert.Equal(1, crashed.DisposeCount);
    }

    [Fact]
    public async Task CancellationAfterRequestWriteRetiresProtocolSessionBeforeNextRequest()
    {
        int launches = 0;
        var stream = new CancelDuringReadStream();
        var canceledSession = new ClientOwningSession(new LocalWorkerClient(stream, Guid.NewGuid()));
        var healthy = new StubLocalSession(new StubLocalClient(request => new LocalTranslationResponse(
            LocalWorkerProtocol.Version,
            request.WorkerSessionEpoch,
            request.RequestId,
            true,
            "done",
            null)));
        await using var manager = new LocalWorkerSessionManager(
            _ => ValueTask.FromResult<ILocalWorkerSession>(++launches == 1 ? canceledSession : healthy),
            new LocalWorkerSessionManagerOptions(TimeSpan.FromMinutes(1), PinWarm: true));
        using var cancellation = new CancellationTokenSource();
        Task<LocalTranslationResponse> first = manager.TranslateAsync(
            "model", "en", "zh-Hans", "hello", 100, cancellation.Token).AsTask();
        await stream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, canceledSession.DisposeCount);
        LocalTranslationResponse second = await manager.TranslateAsync(
            "model", "en", "zh-Hans", "hello", 100, TestContext.Current.CancellationToken);
        Assert.True(second.Success);
        Assert.Equal(2, launches);
        Assert.Equal(1, canceledSession.DisposeCount);
    }

    [Fact]
    public async Task LocalWorkerClientDisposalWaitsForAnActiveRequestToExit()
    {
        var stream = new CancelDuringReadStream();
        var client = new LocalWorkerClient(stream, Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        Task<LocalTranslationResponse> request = client.TranslateAsync(
            "model", "en", "zh-Hans", "hello", 100, cancellation.Token).AsTask();
        await stream.ReadStarted.WaitAsync(
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Task dispose = client.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.False(dispose.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await dispose.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.TranslateAsync(
            "model", "en", "zh-Hans", "later", 100,
            TestContext.Current.CancellationToken).AsTask());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = factory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static TranslationRequest Request(bool strictOffline)
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.UserRegion(new RegionId(Guid.NewGuid())),
            new TextTrackId(Guid.NewGuid()),
            1,
            1);
        var channel = new ChannelExecutionToken(
            source, new TranslationChannelId(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid());
        return new TranslationRequest(
            "hello",
            "en",
            "zh-Hans",
            new TranslationContext(null, null, null, null, [], []),
            [],
            new StageExecutionToken(channel, Guid.NewGuid(), 1, 1, 1),
            TimeSpan.FromSeconds(2),
            "idempotency",
            100,
            100,
            new ProviderCostReservation("characters", 5, null, null),
            strictOffline);
    }

    private static ModelDownloadRequest Request(
        ModelCatalogEntry model,
        ModelCatalogFile file,
        Uri origin,
        bool approved,
        bool strictOffline = false) => new(
            new VerifiedModelCatalog(
                new ModelCatalogDocument(
                    1, 1, DateTimeOffset.UtcNow, [model],
                    [new SignatureEntry("test-key", "Ed25519", Convert.ToBase64String(new byte[64]))]),
                "test-key"),
            model.ModelId,
            model.Version,
            file.RelativePath,
            origin,
            approved,
            strictOffline);

    private static async Task<IReadOnlyList<ProviderWireEvent>> CollectAsync(
        IAsyncEnumerable<ProviderWireEvent> source)
    {
        var result = new List<ProviderWireEvent>();
        await foreach (ProviderWireEvent item in source) result.Add(item);
        return result;
    }

    private sealed class StubLocalClient(
        Func<LocalTranslationRequest, LocalTranslationResponse> callback) : ILocalTranslationClient
    {
        public int CallCount { get; private set; }

        public ValueTask<LocalTranslationResponse> TranslateAsync(
            string modelId,
            string sourceLanguage,
            string targetLanguage,
            string text,
            int maximumOutputCharacters,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Guid epoch = Guid.NewGuid();
            var request = new LocalTranslationRequest(
                LocalWorkerProtocol.Version,
                epoch,
                Guid.NewGuid(),
                modelId,
                sourceLanguage,
                targetLanguage,
                text,
                maximumOutputCharacters);
            return ValueTask.FromResult(callback(request));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingLocalClient(
        TaskCompletionSource started,
        TaskCompletionSource release) : ILocalTranslationClient
    {
        public async ValueTask<LocalTranslationResponse> TranslateAsync(
            string modelId,
            string sourceLanguage,
            string targetLanguage,
            string text,
            int maximumOutputCharacters,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new LocalTranslationResponse(
                LocalWorkerProtocol.Version, Guid.NewGuid(), Guid.NewGuid(), true, "done", null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingLocalClient(Exception exception) : ILocalTranslationClient
    {
        public ValueTask<LocalTranslationResponse> TranslateAsync(
            string modelId,
            string sourceLanguage,
            string targetLanguage,
            string text,
            int maximumOutputCharacters,
            CancellationToken cancellationToken) => ValueTask.FromException<LocalTranslationResponse>(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubLocalSession(ILocalTranslationClient client) : ILocalWorkerSession
    {
        public ILocalTranslationClient Client { get; } = client;
        public int DisposeCount { get; private set; }
        public Task Disposed => _disposed.Task;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ClientOwningSession(ILocalTranslationClient client) : ILocalWorkerSession
    {
        public ILocalTranslationClient Client { get; } = client;
        public int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await Client.DisposeAsync();
        }
    }

    private sealed class CancelDuringReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InfiniTranseon.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
