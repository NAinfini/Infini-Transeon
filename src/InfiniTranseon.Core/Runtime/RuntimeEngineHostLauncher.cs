using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using InfiniTranseon.Contracts.Runtime;
using Microsoft.Win32.SafeHandles;

namespace InfiniTranseon.Core.Runtime;

public sealed class RuntimeEngineEvent : IDisposable
{
    private byte[]? _payload;
    private RuntimeIpcLease? _ipcLease;

    public RuntimeEngineEvent(
        RuntimeMessageKind messageKind,
        Guid eventId,
        Guid runtimeEpoch,
        DateTimeOffset deadlineUtc,
        ReadOnlySpan<byte> payload)
        : this(messageKind, eventId, runtimeEpoch, deadlineUtc, payload.ToArray())
    {
    }

    private RuntimeEngineEvent(
        RuntimeMessageKind messageKind,
        Guid eventId,
        Guid runtimeEpoch,
        DateTimeOffset deadlineUtc,
        byte[] ownedPayload,
        RuntimeIpcLease? ipcLease = null)
    {
        MessageKind = messageKind;
        EventId = eventId;
        RuntimeEpoch = runtimeEpoch;
        DeadlineUtc = deadlineUtc;
        _payload = ownedPayload;
        _ipcLease = ipcLease;
    }

    public RuntimeMessageKind MessageKind { get; }
    public Guid EventId { get; }
    public Guid RuntimeEpoch { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public ReadOnlyMemory<byte> Payload => _payload ?? ReadOnlyMemory<byte>.Empty;
    public bool IsDisposed => _payload is null;

    internal static RuntimeEngineEvent TakeOwnership(
        RuntimeMessageKind messageKind,
        Guid eventId,
        Guid runtimeEpoch,
        DateTimeOffset deadlineUtc,
        byte[] ownedPayload,
        RuntimeIpcLease? ipcLease = null) =>
        new(messageKind, eventId, runtimeEpoch, deadlineUtc, ownedPayload, ipcLease);

    public void Dispose()
    {
        byte[]? payload = Interlocked.Exchange(ref _payload, null);
        RuntimeIpcLease? lease = Interlocked.Exchange(ref _ipcLease, null);
        try
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
        finally
        {
            lease?.Dispose();
        }
    }
}

public interface IRuntimeEngineHostSession : IAsyncDisposable
{
    Guid RuntimeEpoch { get; }
    int MonitoringProcessId => 0;
    IAsyncEnumerable<RuntimeEngineEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);
    ValueTask<RuntimeCaptureTargetAcknowledgement> ApplyCaptureTargetAsync(
        RuntimeCaptureTargetCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken);
    ValueTask<RuntimeOverlayAcknowledgement> ApplyOverlayAsync(
        OverlayDesiredState state,
        TimeSpan timeout,
        CancellationToken cancellationToken);
    ValueTask<PolicyAcknowledgement> ApplyPolicyAsync(
        PolicyRevision revision,
        TimeSpan timeout,
        CancellationToken cancellationToken);
    ValueTask<RuntimeProcessingConfigurationAcknowledgement>
        ApplyProcessingConfigurationAsync(
            RuntimeProcessingConfiguration configuration,
            TimeSpan timeout,
            CancellationToken cancellationToken);
    ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);
    ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
        RuntimeManualOcrRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<RuntimeManualOcrAcknowledgement>(
            new EngineRuntimeUnsupportedOperationException("targetScopedManualOcr"));
    ValueTask<RuntimeOcrResultAcknowledgement> SubmitOcrResultAsync(
        OcrResultSnapshot result,
        TimeSpan timeout,
        CancellationToken cancellationToken);
    ValueTask<RuntimeThumbnailAcknowledgement> RequestThumbnailAsync(
        RuntimeThumbnailRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<RuntimeThumbnailAcknowledgement>(
            new EngineRuntimeUnsupportedOperationException("thumbnail"));
}

public sealed class RuntimeEngineHostSession : IRuntimeEngineHostSession
{
    private readonly SafeProcessHandle _processHandle;
    private readonly SafeFileHandle _jobHandle;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly RuntimeBudgetLedger _budgetLedger;
    private readonly RuntimeIpcAdmission _outgoingAdmission;
    private readonly RuntimeIpcAdmission _incomingAdmission;
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte> _expiredRequests = new();
    private readonly Channel<RuntimeEngineEvent> _events = Channel.CreateBounded<RuntimeEngineEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly CancellationTokenSource _readerStop = new();
    private readonly Task _readerTask;
    private Exception? _readerFailure;
    private long _droppedEvents;
    private int _shutdownRequested;
    private int _disposeStarted;
    private int _disposed;

    internal RuntimeEngineHostSession(
        int processId,
        Guid runtimeEpoch,
        RuntimeNamedPipeConnection connection,
        SafeProcessHandle processHandle,
        SafeFileHandle jobHandle)
    {
        ProcessId = processId;
        RuntimeEpoch = runtimeEpoch;
        Connection = connection;
        _processHandle = processHandle;
        _jobHandle = jobHandle;
        _budgetLedger = new RuntimeBudgetLedger(
            runtimeEpoch,
            [new RuntimeBudgetPoolDefinition(
                "app.ipc.inflight.bytes",
                connection.Capabilities.MaxIpcInFlightBytes)]);
        _outgoingAdmission = new RuntimeIpcAdmission(
            RuntimeIpcBackpressureOptions.Default,
            _budgetLedger,
            "app.ipc.inflight.bytes");
        _incomingAdmission = new RuntimeIpcAdmission(
            RuntimeIpcBackpressureOptions.Default,
            _budgetLedger,
            "app.ipc.inflight.bytes");
        _readerTask = ReceiveLoopAsync();
    }

    public int ProcessId { get; }
    public int MonitoringProcessId => ProcessId;

    public Guid RuntimeEpoch { get; }

    public RuntimeNamedPipeConnection Connection { get; }

    public long DroppedEventCount => Interlocked.Read(ref _droppedEvents);

    public RuntimeBudgetSnapshot AppBudgetSnapshot => _budgetLedger.Snapshot();

    public IAsyncEnumerable<RuntimeEngineEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public bool HasExited
    {
        get => ExitCode.HasValue;
    }

    public int? ExitCode
    {
        get
        {
            if (!RuntimeProcessNative.GetExitCodeProcess(_processHandle, out uint exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return exitCode == RuntimeProcessNative.StillActive
                ? null
                : unchecked((int)exitCode);
        }
    }

    public bool IsSupervised
    {
        get
        {
            if (!RuntimeProcessNative.IsProcessInJob(_processHandle, _jobHandle, out bool result))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return result;
        }
    }

    public async ValueTask PingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        byte[] payload = await RequestAsync(
            RuntimeMessageKind.ControlRequest,
            RuntimeMessageKind.ControlResponse,
            [],
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (payload.Length != 0)
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength);
    }

    public async ValueTask<RuntimeCaptureTargetAcknowledgement> ApplyCaptureTargetAsync(
        RuntimeCaptureTargetCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        byte[] responsePayload = await RequestAsync(
            RuntimeMessageKind.CaptureTargetCommand,
            RuntimeMessageKind.CaptureTargetAcknowledgement,
            RuntimeCaptureTargetPayloadCodec.Encode(command),
            timeout,
            cancellationToken).ConfigureAwait(false);
        RuntimeCaptureTargetAcknowledgement acknowledgement;
        try
        {
            acknowledgement = RuntimeCaptureTargetAcknowledgementPayloadCodec.Decode(
                responsePayload);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(
                RuntimeProtocolError.InvalidPayloadLength, exception);
        }
        if (acknowledgement.CommandRevision != command.CommandRevision ||
            acknowledgement.TargetId != command.TargetId ||
            acknowledgement.TargetInstanceId != command.TargetInstanceId)
            throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
        return acknowledgement;
    }

    public async ValueTask<RuntimeOverlayAcknowledgement> ApplyOverlayAsync(
        OverlayDesiredState state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.RuntimeEpoch != RuntimeEpoch)
            throw new ArgumentException("Overlay state belongs to a different runtime epoch.", nameof(state));
        byte[] responsePayload = await RequestAsync(
            RuntimeMessageKind.OverlayDesiredState,
            RuntimeMessageKind.OverlayAcknowledgement,
            RuntimeOverlayDesiredStatePayloadCodec.Encode(state),
            timeout,
            cancellationToken).ConfigureAwait(false);
        RuntimeOverlayAcknowledgement acknowledgement;
        try
        {
            acknowledgement = RuntimeOverlayAcknowledgementPayloadCodec.Decode(responsePayload);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength, exception);
        }
        if (acknowledgement.TargetInstanceId != state.TargetInstanceId ||
            acknowledgement.OverlayRevision != state.OverlayRevision)
            throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
        return acknowledgement;
    }

    public async ValueTask<PolicyAcknowledgement> ApplyPolicyAsync(
        PolicyRevision revision,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        byte[] responsePayload = await RequestAsync(
            RuntimeMessageKind.PolicyRevision,
            RuntimeMessageKind.PolicyAcknowledgement,
            RuntimePolicyRevisionPayloadCodec.Encode(revision),
            timeout,
            cancellationToken).ConfigureAwait(false);
        PolicyAcknowledgement acknowledgement;
        try
        {
            acknowledgement = RuntimePolicyAcknowledgementPayloadCodec.Decode(responsePayload);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength, exception);
        }
        if (acknowledgement.Revision != revision.Revision)
            throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
        return acknowledgement;
    }

    public async ValueTask<RuntimeProcessingConfigurationAcknowledgement>
        ApplyProcessingConfigurationAsync(
            RuntimeProcessingConfiguration configuration,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        byte[] responsePayload = await RequestAsync(
            RuntimeMessageKind.ProcessingConfiguration,
            RuntimeMessageKind.ProcessingConfigurationAcknowledgement,
            RuntimeProcessingConfigurationPayloadCodec.Encode(configuration),
            timeout,
            cancellationToken).ConfigureAwait(false);
        RuntimeProcessingConfigurationAcknowledgement acknowledgement;
        try
        {
            acknowledgement = RuntimeProcessingConfigurationAcknowledgementPayloadCodec.Decode(
                responsePayload);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(
                RuntimeProtocolError.InvalidPayloadLength, exception);
        }
        if (acknowledgement.TargetInstanceId != configuration.TargetInstanceId ||
            acknowledgement.ConfigurationRevision != configuration.ConfigurationRevision)
            throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
        return acknowledgement;
    }

    public async ValueTask<RuntimeOcrResultAcknowledgement> SubmitOcrResultAsync(
        OcrResultSnapshot result,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExecutionToken.Source.RuntimeEpoch != RuntimeEpoch)
            throw new ArgumentException(
                "OCR result belongs to a different runtime epoch.", nameof(result));
        byte[] responsePayload = await RequestAsync(
            RuntimeMessageKind.OcrResult,
            RuntimeMessageKind.OcrResultAcknowledgement,
            RuntimeOcrResultPayloadCodec.Encode(result),
            timeout,
            cancellationToken).ConfigureAwait(false);
        RuntimeOcrResultAcknowledgement acknowledgement;
        try
        {
            acknowledgement = RuntimeOcrResultAcknowledgementPayloadCodec.Decode(
                responsePayload);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(
                RuntimeProtocolError.InvalidPayloadLength, exception);
        }
        if (acknowledgement.TargetInstanceId != result.ExecutionToken.Source.TargetInstanceId ||
            acknowledgement.SourceGeneration != result.ExecutionToken.Source.SourceGeneration ||
            acknowledgement.ResultSequence != result.ExecutionToken.ResultSequence)
            throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
        return acknowledgement;
    }

    public async ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await RequestManualOcrAsync(
            RuntimeManualOcrRequest.AllTargets,
            timeout,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
        RuntimeManualOcrRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] responsePayload = await RequestAsync(
            RuntimeMessageKind.ControlRequest,
            RuntimeMessageKind.ControlResponse,
            RuntimeManualOcrPayloadCodec.EncodeRequest(request),
            timeout,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return RuntimeManualOcrPayloadCodec.DecodeAcknowledgement(responsePayload);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(
                RuntimeProtocolError.InvalidPayloadLength, exception);
        }
    }

    public async ValueTask<RuntimeThumbnailAcknowledgement> RequestThumbnailAsync(
        RuntimeThumbnailRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] responsePayload = await RequestAsync(
            RuntimeMessageKind.ThumbnailRequest,
            RuntimeMessageKind.ThumbnailAcknowledgement,
            RuntimeThumbnailPayloadCodec.EncodeRequest(request),
            timeout,
            cancellationToken).ConfigureAwait(false);
        RuntimeThumbnailAcknowledgement acknowledgement;
        try
        {
            acknowledgement = RuntimeThumbnailPayloadCodec.DecodeAcknowledgement(
                responsePayload);
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(
                RuntimeProtocolError.InvalidPayloadLength,
                exception);
        }
        if (acknowledgement.TargetInstanceId != request.TargetInstanceId)
            throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
        return acknowledgement;
    }

    public async ValueTask ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        byte[] payload = await RequestAsync(
            RuntimeMessageKind.ShutdownRequest,
            RuntimeMessageKind.ShutdownAcknowledgement,
            [],
            timeout,
            cancellationToken,
            allowAfterShutdownRequested: true).ConfigureAwait(false);
        if (payload.Length != 0)
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            if (!HasExited && Volatile.Read(ref _shutdownRequested) == 0)
            {
                try
                {
                    await ShutdownAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or OperationCanceledException)
                {
                    // Disposal is still responsible for closing the pipe and job when the
                    // runtime disconnects or stops responding during best-effort shutdown.
                }
            }
        }
        finally
        {
            try
            {
                _readerStop.Cancel();
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _readerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_readerStop.IsCancellationRequested)
                {
                }
                while (_events.Reader.TryRead(out RuntimeEngineEvent? runtimeEvent))
                    runtimeEvent.Dispose();
                _jobHandle.Dispose();
                _ = RuntimeProcessNative.WaitForSingleObject(_processHandle, 5_000U);
                _processHandle.Dispose();
                _writeGate.Dispose();
                _readerStop.Dispose();
                Volatile.Write(ref _disposed, 1);
            }
        }
    }

    private async ValueTask<byte[]> RequestAsync(
        RuntimeMessageKind requestKind,
        RuntimeMessageKind responseKind,
        byte[] requestPayload,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowAfterShutdownRequested = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ObjectDisposedException.ThrowIf(
            !allowAfterShutdownRequested && Volatile.Read(ref _disposeStarted) != 0,
            this);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (!allowAfterShutdownRequested && Volatile.Read(ref _shutdownRequested) != 0)
        {
            throw new InvalidOperationException("EngineHost shutdown has already been requested.");
        }
        if (Volatile.Read(ref _readerFailure) is Exception readerFailure)
            throw new IOException("EngineHost receive loop is unavailable.", readerFailure);

        if (requestPayload.Length > RuntimeProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(requestPayload));
        int frameBytes = checked(RuntimeProtocol.WireHeaderBytes + requestPayload.Length);
        RuntimeMessageLane lane = RuntimeMessageLaneClassifier.Classify(requestKind);
        if (!_outgoingAdmission.TryAcquire(
                lane, frameBytes, out RuntimeIpcLease? admissionLease))
        {
            throw new RuntimeIpcBackpressureException(lane);
        }

        using (admissionLease)
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout);
            Guid requestId = Guid.NewGuid();
            var pending = new PendingRequest(responseKind);
            if (!_pending.TryAdd(requestId, pending))
                throw new InvalidOperationException("Runtime request identifier collision.");
            try
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
                using RuntimeFrame request = RuntimeFrame.TakeOwnership(
                    new RuntimeEnvelopeHeader(
                        RuntimeProtocol.CurrentVersion,
                        requestKind,
                        requestId,
                        RuntimeEpoch,
                        requestPayload.Length,
                        deadline),
                    requestPayload);
                await _writeGate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                try
                {
                    await RuntimeFrameCodec.WriteAsync(
                        Connection.Stream,
                        request,
                        timeoutSource.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
                return await pending.Completion.Task.WaitAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_pending.TryRemove(requestId, out _))
                {
                    if (_expiredRequests.Count >= 64) _expiredRequests.Clear();
                    _expiredRequests.TryAdd(requestId, 0);
                }
                throw;
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_readerStop.IsCancellationRequested)
            {
                using RuntimeFrame frame = await RuntimeFrameCodec.ReadAsync(
                    Connection.Stream,
                    DateTimeOffset.UtcNow,
                    _readerStop.Token).ConfigureAwait(false);
                if (frame.Header.RuntimeEpoch != RuntimeEpoch)
                    throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);

                if (_pending.TryRemove(frame.Header.RequestId, out PendingRequest? pending))
                {
                    if (frame.Header.MessageKind != pending.ExpectedKind)
                    {
                        pending.Completion.TrySetException(new RuntimeProtocolException(
                            RuntimeProtocolError.UnexpectedMessageKind));
                    }
                    else
                    {
                        pending.Completion.TrySetResult(frame.Payload.ToArray());
                    }
                    continue;
                }
                if (_expiredRequests.TryRemove(frame.Header.RequestId, out _)) continue;
                if (IsResponseKind(frame.Header.MessageKind))
                    throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);

                int eventBytes = checked(
                    RuntimeProtocol.WireHeaderBytes + frame.Header.PayloadLength);
                RuntimeMessageLane lane = RuntimeMessageLaneClassifier.Classify(
                    frame.Header.MessageKind);
                if (!_incomingAdmission.TryAcquire(
                        lane, eventBytes, out RuntimeIpcLease? eventLease))
                {
                    Interlocked.Increment(ref _droppedEvents);
                    throw new RuntimeProtocolException(
                        RuntimeProtocolError.EventQueueCapacityExceeded);
                }
                RuntimeEngineEvent runtimeEvent;
                try
                {
                    runtimeEvent = RuntimeEngineEvent.TakeOwnership(
                        frame.Header.MessageKind,
                        frame.Header.RequestId,
                        frame.Header.RuntimeEpoch,
                        frame.Header.DeadlineUtc,
                        frame.Payload.ToArray(),
                        eventLease);
                }
                catch
                {
                    eventLease!.Dispose();
                    throw;
                }
                try
                {
                    EnqueueEventOrThrow(_events.Writer, runtimeEvent);
                }
                catch
                {
                    Interlocked.Increment(ref _droppedEvents);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (_readerStop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                _events.Writer.TryComplete();
                return;
            }
            Volatile.Write(ref _readerFailure, exception);
            foreach ((_, PendingRequest pending) in _pending)
                pending.Completion.TrySetException(exception);
            _events.Writer.TryComplete(exception);
            return;
        }
        _events.Writer.TryComplete();
    }

    private static bool IsResponseKind(RuntimeMessageKind kind) => kind is
        RuntimeMessageKind.HandshakeResponse or
        RuntimeMessageKind.ControlResponse or
        RuntimeMessageKind.PolicyAcknowledgement or
        RuntimeMessageKind.ShutdownAcknowledgement or
        RuntimeMessageKind.CaptureTargetAcknowledgement or
        RuntimeMessageKind.OverlayAcknowledgement or
        RuntimeMessageKind.ProcessingConfigurationAcknowledgement or
        RuntimeMessageKind.OcrResultAcknowledgement or
        RuntimeMessageKind.ThumbnailAcknowledgement;

    internal static void EnqueueEventOrThrow(
        ChannelWriter<RuntimeEngineEvent> writer,
        RuntimeEngineEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (writer.TryWrite(runtimeEvent)) return;
        runtimeEvent.Dispose();
        throw new RuntimeProtocolException(
            RuntimeProtocolError.EventQueueCapacityExceeded);
    }

    private sealed class PendingRequest
    {
        public PendingRequest(RuntimeMessageKind expectedKind)
        {
            ExpectedKind = expectedKind;
            Completion = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public RuntimeMessageKind ExpectedKind { get; }
        public TaskCompletionSource<byte[]> Completion { get; }
    }
}

public sealed class RuntimeIpcBackpressureException : Exception
{
    public RuntimeIpcBackpressureException(RuntimeMessageLane lane)
        : base($"Runtime IPC {lane} lane has reached its configured limit.")
    {
        Lane = lane;
    }

    public RuntimeMessageLane Lane { get; }
}

public static class RuntimeEngineHostLauncher
{
    private const uint BootstrapMagic = 0x42525449U;
    private const int BootstrapFixedBytes = 64;

    public static async ValueTask<RuntimeEngineHostSession> LaunchAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        WindowsPlatformGuard.EnsureCurrentSystemSupported();
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath) ||
            !string.Equals(Path.GetExtension(fullExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("EngineHost executable was not found.", fullExecutablePath);
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        string pipeName = RuntimePipeName.Create();
        Guid runtimeEpoch = Guid.NewGuid();
        byte[] nonce = RandomNumberGenerator.GetBytes(Contracts.Runtime.RuntimeProtocol.BootstrapNonceBytes);
        SafeFileHandle? bootstrapRead = null;
        SafeFileHandle? bootstrapWrite = null;
        SafeProcessHandle? processHandle = null;
        SafeFileHandle? jobHandle = null;
        RuntimeNamedPipeConnection? connection = null;
        try
        {
            var securityAttributes = new RuntimeProcessNative.SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<RuntimeProcessNative.SecurityAttributes>(),
                InheritHandle = true,
            };
            if (!RuntimeProcessNative.CreatePipe(
                out bootstrapRead,
                out bootstrapWrite,
                ref securityAttributes,
                0U))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!RuntimeProcessNative.SetHandleInformation(
                bootstrapWrite,
                RuntimeProcessNative.HandleFlagInherit,
                0U))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var attributeList = new RuntimeProcessAttributeList(bootstrapRead);
            RuntimeProcessNative.StartupInfoEx startupInfo = attributeList.CreateStartupInfo();
            jobHandle = CreateKillOnCloseJob();
            string commandLine = $"\"{fullExecutablePath}\" --bootstrap-handle={bootstrapRead.DangerousGetHandle().ToInt64()}";
            var mutableCommandLine = new StringBuilder(commandLine);
            if (!RuntimeProcessNative.CreateProcess(
                fullExecutablePath,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                RuntimeProcessNative.ExtendedStartupInfoPresent |
                    RuntimeProcessNative.CreateNoWindow |
                    RuntimeProcessNative.CreateSuspended,
                IntPtr.Zero,
                Path.GetDirectoryName(fullExecutablePath),
                ref startupInfo,
                out RuntimeProcessNative.ProcessInformation processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            processHandle = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            bootstrapRead.Dispose();
            bootstrapRead = null;

            if (!RuntimeProcessNative.AssignProcessToJobObject(jobHandle, processHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            byte[] bootstrapPayload = CreateBootstrapPayload(
                Environment.ProcessId,
                runtimeEpoch,
                nonce,
                pipeName);
            try
            {
                await using var bootstrapStream = new FileStream(
                    bootstrapWrite,
                    FileAccess.Write,
                    bufferSize: 4_096,
                    isAsync: false);
                bootstrapWrite = null;
                await bootstrapStream.WriteAsync(bootstrapPayload, cancellationToken).ConfigureAwait(false);
                await bootstrapStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bootstrapPayload);
            }

            if (RuntimeProcessNative.ResumeThread(threadHandle) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            connection = await RuntimeNamedPipeClient.ConnectAsync(
                pipeName,
                checked((int)processInformation.ProcessId),
                runtimeEpoch,
                nonce,
                timeout,
                cancellationToken).ConfigureAwait(false);

            var session = new RuntimeEngineHostSession(
                checked((int)processInformation.ProcessId),
                runtimeEpoch,
                connection,
                processHandle,
                jobHandle);
            connection = null;
            processHandle = null;
            jobHandle = null;
            return session;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            jobHandle?.Dispose();
            if (processHandle is not null && !processHandle.IsInvalid)
            {
                _ = RuntimeProcessNative.TerminateProcess(processHandle, 1U);
                _ = RuntimeProcessNative.WaitForSingleObject(processHandle, 5_000U);
                processHandle.Dispose();
            }
            throw;
        }
        finally
        {
            bootstrapRead?.Dispose();
            bootstrapWrite?.Dispose();
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static byte[] CreateBootstrapPayload(
        int expectedClientProcessId,
        Guid runtimeEpoch,
        ReadOnlySpan<byte> nonce,
        string pipeName)
    {
        RuntimePipeName.Validate(pipeName);
        int pipeNameBytes = Encoding.ASCII.GetByteCount(pipeName);
        byte[] payload = new byte[sizeof(int) + BootstrapFixedBytes + pipeNameBytes];
        Span<byte> body = payload.AsSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(payload, body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body, BootstrapMagic);
        BinaryPrimitives.WriteInt32LittleEndian(body[4..], Contracts.Runtime.RuntimeProtocol.CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(body[8..], expectedClientProcessId);
        runtimeEpoch.TryWriteBytes(body[12..28]);
        nonce.CopyTo(body[28..60]);
        BinaryPrimitives.WriteInt32LittleEndian(body[60..], pipeNameBytes);
        Encoding.ASCII.GetBytes(pipeName, body[BootstrapFixedBytes..]);
        return payload;
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        SafeFileHandle job = RuntimeProcessNative.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        RuntimeProcessNative.JobObjectExtendedLimitInformation limits = default;
        limits.BasicLimitInformation.LimitFlags = RuntimeProcessNative.JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<RuntimeProcessNative.JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!RuntimeProcessNative.SetInformationJobObject(
                job,
                RuntimeProcessNative.JobObjectExtendedLimitInformationClass,
                buffer,
                (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal sealed class RuntimeProcessAttributeList : IDisposable
{
    private IntPtr _list;
    private IntPtr _handleValue;

    public RuntimeProcessAttributeList(SafeFileHandle inheritedHandle)
    {
        try
        {
            nuint bytes = 0;
            _ = RuntimeProcessNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0U, ref bytes);
            _list = Marshal.AllocHGlobal(checked((int)bytes));
            if (!RuntimeProcessNative.InitializeProcThreadAttributeList(_list, 1, 0U, ref bytes))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _handleValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_handleValue, inheritedHandle.DangerousGetHandle());
            if (!RuntimeProcessNative.UpdateProcThreadAttribute(
                _list,
                0U,
                RuntimeProcessNative.ProcThreadAttributeHandleList,
                _handleValue,
                (nuint)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public RuntimeProcessNative.StartupInfoEx CreateStartupInfo() => new()
    {
        StartupInfo = new RuntimeProcessNative.StartupInfo
        {
            Size = (uint)Marshal.SizeOf<RuntimeProcessNative.StartupInfoEx>(),
        },
        AttributeList = _list,
    };

    public void Dispose()
    {
        if (_list != IntPtr.Zero)
        {
            RuntimeProcessNative.DeleteProcThreadAttributeList(_list);
            Marshal.FreeHGlobal(_list);
            _list = IntPtr.Zero;
        }
        if (_handleValue != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_handleValue);
            _handleValue = IntPtr.Zero;
        }
    }
}

internal static class RuntimeProcessNative
{
    internal const uint HandleFlagInherit = 0x00000001U;
    internal const uint ExtendedStartupInfoPresent = 0x00080000U;
    internal const uint CreateNoWindow = 0x08000000U;
    internal const uint CreateSuspended = 0x00000004U;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000U;
    internal const int JobObjectExtendedLimitInformationClass = 9;
    internal const uint StillActive = 259U;
    internal static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal uint Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2Bytes;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(
        SafeProcessHandle process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
