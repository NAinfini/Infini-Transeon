using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Ocr;

public sealed class RuntimeLocalOcrDispatcher
{
    private readonly IOcrProbe _probe;
    private readonly IRuntimeOcrResultSink _sink;

    public RuntimeLocalOcrDispatcher(IOcrProbe probe, IRuntimeOcrResultSink sink)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(sink);
        _probe = probe;
        _sink = sink;
    }

    public async ValueTask DispatchAsync(
        RuntimeEngineEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (runtimeEvent.MessageKind != RuntimeMessageKind.LocalOcrCropRequest)
            throw new RuntimeProtocolException(RuntimeProtocolError.UnexpectedMessageKind);
        if (runtimeEvent.DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new RuntimeProtocolException(RuntimeProtocolError.DeadlineExpired);

        LocalOcrCropRequest crop;
        try
        {
            crop = RuntimeLocalOcrCropRequestPayloadCodec.Decode(runtimeEvent.Payload.Span);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(
                RuntimeProtocolError.InvalidPayloadLength,
                exception);
        }

        using (crop)
        {
            if (runtimeEvent.RuntimeEpoch != crop.ExecutionToken.Source.RuntimeEpoch)
                throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
            if (crop.DeadlineUtc > runtimeEvent.DeadlineUtc)
                throw new RuntimeProtocolException(RuntimeProtocolError.InvalidDeadline);

            OcrResultSnapshot result;
            try
            {
                OcrProbeResult reading = await _probe.RecognizeAsync(
                    new OcrProbeRequest(
                        crop.ExecutionToken.Source.Area.UserRegionId ??
                            new RegionId(crop.ExecutionToken.Source.TextTrackId.Value),
                        crop.PixelWidth,
                        crop.PixelHeight,
                        crop.EncodedCrop,
                        crop.RecognitionLanguage),
                    cancellationToken).ConfigureAwait(false);
                result = new OcrResultSnapshot(
                    crop.ExecutionToken,
                    reading.Lines,
                    "paddleocr.onnx",
                    reading.LanguageTag ?? crop.RecognitionLanguage,
                    IsStable: true,
                    TerminalErrorCode: null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PaddleOcrUnavailableException exception)
            {
                result = new OcrResultSnapshot(
                    crop.ExecutionToken,
                    [],
                    "paddleocr.onnx",
                    crop.RecognitionLanguage,
                    IsStable: false,
                    exception.ErrorCode);
            }

            await _sink.SendAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }
}
