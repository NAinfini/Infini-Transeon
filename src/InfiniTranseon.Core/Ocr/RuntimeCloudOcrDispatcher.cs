using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Ocr;

public interface IRuntimeOcrResultSink
{
    ValueTask SendAsync(OcrResultSnapshot result, CancellationToken cancellationToken);
}

public sealed class RuntimeEngineHostOcrResultSink : IRuntimeOcrResultSink
{
    private readonly IRuntimeEngineHostSession _session;
    private readonly TimeSpan _timeout;

    public RuntimeEngineHostOcrResultSink(
        IRuntimeEngineHostSession session,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _session = session;
        _timeout = timeout;
    }

    public async ValueTask SendAsync(
        OcrResultSnapshot result,
        CancellationToken cancellationToken)
    {
        RuntimeOcrResultAcknowledgement acknowledgement =
            await _session.SubmitOcrResultAsync(result, _timeout, cancellationToken)
                .ConfigureAwait(false);
        if (!acknowledgement.Accepted)
            throw new InvalidOperationException(
                acknowledgement.ErrorCode ?? "ocr.result.rejected");
    }
}

public sealed class RuntimeCloudOcrDispatcher
{
    private readonly CloudOcrRouter _router;
    private readonly IRuntimeOcrResultSink _sink;
    private readonly Func<TargetInstanceId, bool> _strictOffline;

    public RuntimeCloudOcrDispatcher(
        CloudOcrRouter router,
        IRuntimeOcrResultSink sink,
        Func<TargetInstanceId, bool> strictOffline)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(strictOffline);
        _router = router;
        _sink = sink;
        _strictOffline = strictOffline;
    }

    public async ValueTask DispatchAsync(
        RuntimeEngineEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (runtimeEvent.MessageKind != RuntimeMessageKind.CloudOcrCropRequest)
            throw new OcrRoutingException(
                "ocr.runtime.unexpectedMessage", "Runtime event is not a cloud OCR crop request.");
        if (runtimeEvent.DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new OcrRoutingException("ocr.deadline.expired", "Cloud OCR runtime event has expired.");

        CloudOcrCropRequest crop;
        try
        {
            crop = RuntimeCloudOcrCropRequestPayloadCodec.Decode(runtimeEvent.Payload.Span);
        }
        catch (InvalidDataException exception)
        {
            throw new OcrRoutingException(
                "ocr.runtime.payloadInvalid", $"Cloud OCR runtime payload is invalid: {exception.Message}");
        }
        using (crop)
        {
            if (runtimeEvent.RuntimeEpoch != crop.ExecutionToken.Source.RuntimeEpoch)
                throw new OcrRoutingException(
                    "ocr.runtime.epochMismatch", "Cloud OCR crop belongs to another runtime epoch.");
            if (crop.DeadlineUtc > runtimeEvent.DeadlineUtc)
                throw new OcrRoutingException(
                    "ocr.runtime.deadlineMismatch", "Cloud OCR crop exceeds its runtime envelope deadline.");
            var request = new CloudOcrRouteRequest(
                crop.ExecutionToken,
                crop.MimeType,
                crop.EncodedCrop.Span,
                crop.PixelWidth,
                crop.PixelHeight,
                crop.ExplicitCloudConsent,
                crop.ConsentPolicyRevision,
                crop.EncodedByteCeiling,
                crop.DeadlineUtc);
            OcrResultSnapshot result;
            try
            {
                result = await _router.RouteAsync(
                    crop.ProviderId,
                    request,
                    _strictOffline(crop.ExecutionToken.Source.TargetInstanceId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (OcrRoutingException exception)
            {
                result = _router.CompleteFailure(crop.ExecutionToken, exception.Code);
            }
            catch (Exception)
            {
                result = _router.CompleteFailure(
                    crop.ExecutionToken,
                    "ocr.provider.unhandledFailure");
            }
            await _sink.SendAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }
}
