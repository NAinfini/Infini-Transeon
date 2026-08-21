using System.Buffers.Binary;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation.Local;

public interface ILocalTranslationClient : IAsyncDisposable
{
    ValueTask<LocalTranslationResponse> TranslateAsync(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int maximumOutputCharacters,
        CancellationToken cancellationToken);
}

public sealed class LocalWorkerClient : ILocalTranslationClient
{
    private readonly Stream _stream;
    private readonly Guid _sessionEpoch;
    private readonly SemaphoreSlim _singleRequest = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeStarted;
    private bool _faulted;

    public LocalWorkerClient(Stream authenticatedStream, Guid sessionEpoch)
    {
        ArgumentNullException.ThrowIfNull(authenticatedStream);
        if (!authenticatedStream.CanRead || !authenticatedStream.CanWrite)
            throw new ArgumentException("Worker stream must be duplex.", nameof(authenticatedStream));
        if (sessionEpoch == Guid.Empty) throw new ArgumentException("Worker session epoch cannot be empty.", nameof(sessionEpoch));
        _stream = authenticatedStream;
        _sessionEpoch = sessionEpoch;
    }

    public async ValueTask<LocalTranslationResponse> TranslateAsync(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int maximumOutputCharacters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > LocalWorkerProtocol.MaximumTextCharacters)
            throw new ArgumentOutOfRangeException(nameof(text));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumOutputCharacters, LocalWorkerProtocol.MaximumTextCharacters);
        var request = new LocalTranslationRequest(
            LocalWorkerProtocol.Version,
            _sessionEpoch,
            Guid.NewGuid(),
            modelId,
            sourceLanguage,
            targetLanguage,
            text,
            maximumOutputCharacters);
        await _singleRequest.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool frameStarted = false;
        bool requestSent = false;
        bool resynchronizing = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            if (_faulted) throw new IOException("Local worker protocol session is no longer usable.");
            frameStarted = true;
            await LocalWorkerFrameCodec.WriteAsync(_stream, request, cancellationToken).ConfigureAwait(false);
            requestSent = true;
            LocalTranslationResponse response = await LocalWorkerFrameCodec.ReadAsync<LocalTranslationResponse>(
                _stream, cancellationToken).ConfigureAwait(false);
            if (response.ProtocolVersion != LocalWorkerProtocol.Version ||
                response.WorkerSessionEpoch != _sessionEpoch || response.RequestId != request.RequestId)
                throw new InvalidDataException("Local worker returned stale or mismatched identity.");
            if (response.Text?.Length > maximumOutputCharacters)
                throw new InvalidDataException("Local worker exceeded the output limit.");
            return response;
        }
        catch (OperationCanceledException) when (requestSent)
        {
            // Superseding a caption is ordinary traffic, and the worker holds a multi-gigabyte model
            // that costs seconds to load. The request the caller abandoned is still being decoded and
            // its response frame will arrive, so the session is resynchronized rather than discarded:
            // the request slot stays held until that orphaned frame has been read and thrown away.
            resynchronizing = true;
            _ = ResynchronizeAsync();
            throw;
        }
        catch (Exception exception) when (frameStarted && exception is IOException or InvalidDataException)
        {
            _faulted = true;
            throw;
        }
        finally
        {
            if (!resynchronizing) _singleRequest.Release();
        }
    }

    private async Task ResynchronizeAsync()
    {
        try
        {
            _ = await LocalWorkerFrameCodec.ReadAsync<LocalTranslationResponse>(
                _stream, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The abandoned request has no caller left to observe this. Recording the fault is what
            // matters: the stream is no longer in a known position, so the next request fails fast
            // and the session manager retires the worker instead of reading a misaligned frame.
            _faulted = true;
        }
        finally
        {
            _singleRequest.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            // A resynchronizing read is waiting for a frame from a worker that disposal is about to
            // take away, and closing a pipe does not abort a read already in flight on it. Cancelling
            // that read is what lets the abandoned request release the slot disposal waits on, so
            // shutting the session down ends instead of outliving the worker it is retiring.
            await _closing.CancelAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            await _singleRequest.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _singleRequest.Release();
            _closing.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }
}

public static class LocalWorkerFrameCodec
{
    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length is < 2 or > LocalWorkerProtocol.MaximumFrameBytes)
            throw new InvalidDataException("Local worker frame length is invalid.");
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > LocalWorkerProtocol.MaximumFrameBytes)
            throw new InvalidDataException("Local worker frame length is invalid.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload) ??
                throw new InvalidDataException("Local worker frame is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Local worker frame is malformed.", exception);
        }
    }
}
