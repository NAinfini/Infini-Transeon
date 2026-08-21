using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Ocr;

public sealed class RuntimeLocalOcrDispatcherTests
{
    [Fact]
    public async Task RecognizesLocalCropAndReturnsItThroughTheEngineHostSink()
    {
        OcrExecutionToken token = Token();
        var probe = new StubProbe(_ => new OcrProbeResult(
            "攻撃",
            [new TextLine("攻撃", new NormalizedRect(0.1, 0.2, 0.3, 0.2), 0.9)],
            TimeSpan.FromMilliseconds(12),
            "ja"));
        var sink = new RecordingSink();
        var dispatcher = new RuntimeLocalOcrDispatcher(probe, sink);
        using RuntimeEngineEvent runtimeEvent = Event(token);

        await dispatcher.DispatchAsync(
            runtimeEvent,
            TestContext.Current.CancellationToken);

        OcrResultSnapshot result = Assert.IsType<OcrResultSnapshot>(sink.Result);
        Assert.Equal(token, result.ExecutionToken);
        Assert.Equal("攻撃", Assert.Single(result.Lines).Text);
        Assert.Equal("paddleocr.onnx", result.ModelId);
        Assert.Equal("ja", result.ModelVersion);
        Assert.Null(result.TerminalErrorCode);
    }

    [Fact]
    public async Task MissingInstalledModelReturnsStableFailureWithoutAnyInstallPath()
    {
        OcrExecutionToken token = Token();
        var probe = new StubProbe(_ => throw new PaddleOcrUnavailableException(
            PaddleOcrUnavailableException.LanguageNotInstalledCode,
            "missing"));
        var sink = new RecordingSink();
        var dispatcher = new RuntimeLocalOcrDispatcher(probe, sink);
        using RuntimeEngineEvent runtimeEvent = Event(token);

        await dispatcher.DispatchAsync(
            runtimeEvent,
            TestContext.Current.CancellationToken);

        OcrResultSnapshot result = Assert.IsType<OcrResultSnapshot>(sink.Result);
        Assert.Empty(result.Lines);
        Assert.False(result.IsStable);
        Assert.Equal(
            PaddleOcrUnavailableException.LanguageNotInstalledCode,
            result.TerminalErrorCode);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task InvalidInstalledPackageReturnsStableFailure()
    {
        OcrExecutionToken token = Token();
        using var probe = new PaddleOcrProbe(new ThrowingCatalog(
            new InvalidDataException("duplicate models")));
        var sink = new RecordingSink();
        var dispatcher = new RuntimeLocalOcrDispatcher(probe, sink);
        using RuntimeEngineEvent runtimeEvent = Event(token);

        await dispatcher.DispatchAsync(
            runtimeEvent,
            TestContext.Current.CancellationToken);

        OcrResultSnapshot result = Assert.IsType<OcrResultSnapshot>(sink.Result);
        Assert.Empty(result.Lines);
        Assert.False(result.IsStable);
        Assert.Equal("ocr.paddle.packageInvalid", result.TerminalErrorCode);
    }

    [Theory]
    [InlineData("ocr.paddle.imageInvalid")]
    [InlineData("ocr.paddle.runtimeUnavailable")]
    public async Task ExpectedLocalRuntimeFailureReturnsStableTerminalResult(string errorCode)
    {
        OcrExecutionToken token = Token();
        var probe = new StubProbe(_ => throw new PaddleOcrUnavailableException(errorCode, "failed"));
        var sink = new RecordingSink();
        var dispatcher = new RuntimeLocalOcrDispatcher(probe, sink);
        using RuntimeEngineEvent runtimeEvent = Event(token);

        await dispatcher.DispatchAsync(
            runtimeEvent,
            TestContext.Current.CancellationToken);

        OcrResultSnapshot result = Assert.IsType<OcrResultSnapshot>(sink.Result);
        Assert.Equal(errorCode, result.TerminalErrorCode);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RemovalReservationReturnsStableBusyResultInsteadOfStoppingTheEventPump()
    {
        OcrExecutionToken token = Token();
        using var probe = new PaddleOcrProbe(
            new FixedCatalog(),
            _ => throw new PaddleOcrUnavailableException(
                PaddleOcrUnavailableException.ModelBusyCode,
                "The model is being removed."));
        var sink = new RecordingSink();
        var dispatcher = new RuntimeLocalOcrDispatcher(probe, sink);
        using RuntimeEngineEvent runtimeEvent = Event(token);

        await dispatcher.DispatchAsync(
            runtimeEvent,
            TestContext.Current.CancellationToken);

        OcrResultSnapshot result = Assert.IsType<OcrResultSnapshot>(sink.Result);
        Assert.Empty(result.Lines);
        Assert.False(result.IsStable);
        Assert.Equal(PaddleOcrUnavailableException.ModelBusyCode, result.TerminalErrorCode);
    }

    [Fact]
    public async Task UnexpectedProbeFailureIsNotHidden()
    {
        var probe = new StubProbe(_ => throw new InvalidOperationException("broken-model"));
        var dispatcher = new RuntimeLocalOcrDispatcher(probe, new RecordingSink());
        using RuntimeEngineEvent runtimeEvent = Event(Token());

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(
                runtimeEvent,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("broken-model", failure.Message);
    }

    private static RuntimeEngineEvent Event(OcrExecutionToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        using var request = new LocalOcrCropRequest(
            token,
            "image/png",
            [1, 2, 3],
            320,
            120,
            "ja-JP",
            1024,
            deadline);
        return new RuntimeEngineEvent(
            RuntimeMessageKind.LocalOcrCropRequest,
            Guid.NewGuid(),
            token.Source.RuntimeEpoch,
            deadline,
            RuntimeLocalOcrCropRequestPayloadCodec.Encode(request));
    }

    private static OcrExecutionToken Token()
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.UserRegion(new RegionId(Guid.NewGuid())),
            new TextTrackId(Guid.NewGuid()),
            1,
            1);
        return new OcrExecutionToken(source, Guid.NewGuid(), 1, 1);
    }

    private sealed class StubProbe(Func<OcrProbeRequest, OcrProbeResult> recognize) : IOcrProbe
    {
        public ValueTask<OcrProbeResult> RecognizeAsync(
            OcrProbeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(recognize(request));
        }
    }

    private sealed class ThrowingCatalog(Exception failure) : IPaddleOcrModelCatalog
    {
        public IReadOnlyList<string> InstalledLanguageTags => [];

        public bool TryResolve(
            string? languageTag,
            [NotNullWhen(true)] out PaddleOcrModelSet? modelSet)
        {
            modelSet = null;
            throw failure;
        }
    }

    private sealed class FixedCatalog : IPaddleOcrModelCatalog
    {
        public IReadOnlyList<string> InstalledLanguageTags => ["ja"];

        public bool TryResolve(
            string? languageTag,
            [NotNullWhen(true)] out PaddleOcrModelSet? modelSet)
        {
            modelSet = new PaddleOcrModelSet(
                "ja",
                "unused-det.onnx",
                "unused-rec.onnx",
                null);
            return true;
        }
    }

    private sealed class RecordingSink : IRuntimeOcrResultSink
    {
        public OcrResultSnapshot? Result { get; private set; }

        public ValueTask SendAsync(
            OcrResultSnapshot result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result = result;
            return ValueTask.CompletedTask;
        }
    }
}
