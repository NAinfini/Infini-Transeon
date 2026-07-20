using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public static class RuntimePipeName
{
    private const string Prefix = "infini-transeon.";

    public static string Create() => $"{Prefix}{Guid.NewGuid():N}";

    public static void Validate(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        bool valid = pipeName.Length == Prefix.Length + 32
            && pipeName.StartsWith(Prefix, StringComparison.Ordinal)
            && pipeName.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
        if (!valid)
        {
            throw new ArgumentException("Runtime pipe name is not a valid local session name.", nameof(pipeName));
        }
    }
}

public sealed class RuntimeNamedPipeConnection : IAsyncDisposable
{
    internal RuntimeNamedPipeConnection(
        NamedPipeClientStream stream,
        int authenticatedServerProcessId,
        Guid runtimeEpoch)
    {
        Stream = stream;
        AuthenticatedServerProcessId = authenticatedServerProcessId;
        RuntimeEpoch = runtimeEpoch;
    }

    public NamedPipeClientStream Stream { get; }

    public int AuthenticatedServerProcessId { get; }

    public Guid RuntimeEpoch { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public static class RuntimeNamedPipeClient
{
    public static async ValueTask<RuntimeNamedPipeConnection> ConnectAsync(
        string pipeName,
        int expectedServerProcessId,
        Guid runtimeEpoch,
        ReadOnlyMemory<byte> bootstrapNonce,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        RuntimePipeName.Validate(pipeName);
        if (expectedServerProcessId <= 0)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPeerProcessId);
        }

        if (runtimeEpoch == Guid.Empty)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
        }

        if (bootstrapNonce.Length != RuntimeProtocol.BootstrapNonceBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidBootstrapNonce);
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

            int actualServerProcessId = GetServerProcessId(pipe.SafePipeHandle);
            if (actualServerProcessId != expectedServerProcessId)
            {
                throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            using RuntimeFrame request = RuntimeHandshakeFrames.CreateRequest(
                Environment.ProcessId,
                expectedServerProcessId,
                runtimeEpoch,
                bootstrapNonce.Span,
                now.Add(timeout));
            await RuntimeFrameCodec.WriteAsync(pipe, request, timeoutSource.Token).ConfigureAwait(false);
            using RuntimeFrame response = await RuntimeFrameCodec.ReadAsync(
                pipe,
                DateTimeOffset.UtcNow,
                timeoutSource.Token).ConfigureAwait(false);
            RuntimeHandshakeFrames.ValidateAcceptedResponse(
                response,
                request.Header,
                expectedServerProcessId,
                DateTimeOffset.UtcNow);

            return new RuntimeNamedPipeConnection(pipe, actualServerProcessId, runtimeEpoch);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static int GetServerProcessId(SafePipeHandle pipeHandle)
    {
        if (!RuntimePipeNative.GetNamedPipeServerProcessId(pipeHandle, out uint processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return checked((int)processId);
    }
}

internal static partial class RuntimePipeNative
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}
