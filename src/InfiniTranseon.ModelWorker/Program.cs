using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.ModelWorker;

return await WorkerMain.RunAsync();

internal static partial class WorkerMain
{
    public static async Task<int> RunAsync()
    {
        byte[]? secret = null;
        try
        {
            await using Stream bootstrapStream = OpenBootstrapStream();
            LocalWorkerBootstrap bootstrap = await ReadBootstrapAsync(bootstrapStream, CancellationToken.None);
            if (bootstrap.ProtocolVersion != LocalWorkerProtocol.Version ||
                bootstrap.WorkerSessionEpoch == Guid.Empty || bootstrap.ExpectedParentProcessId <= 0 ||
                string.IsNullOrWhiteSpace(bootstrap.PipeName) ||
                string.IsNullOrWhiteSpace(bootstrap.ManagedModelDirectory) ||
                !Path.IsPathFullyQualified(bootstrap.ManagedModelDirectory)) return 10;
            secret = Convert.FromBase64String(bootstrap.BootstrapSecretBase64);
            if (secret.Length != 32) return 11;
            using var pipe = new NamedPipeClientStream(
                ".", bootstrap.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(10_000, CancellationToken.None);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverProcessId) ||
                serverProcessId != bootstrap.ExpectedParentProcessId) return 12;
            await WriteHandshakeAsync(pipe, bootstrap.WorkerSessionEpoch, secret, CancellationToken.None);
            CryptographicOperations.ZeroMemory(secret);
            secret = null;
            using ILocalModelRuntime runtime =
                LocalModelRuntimeFactory.Create(bootstrap.ManagedModelDirectory);
            while (pipe.IsConnected)
            {
                LocalTranslationRequest request;
                try { request = await ReadFrameAsync<LocalTranslationRequest>(pipe, CancellationToken.None); }
                catch (EndOfStreamException) { break; }
                LocalTranslationResponse response = Translate(
                    request, bootstrap.WorkerSessionEpoch, runtime);
                await WriteFrameAsync(pipe, response, CancellationToken.None);
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or FormatException or InvalidDataException or DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            return 20;
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static FileStream OpenBootstrapStream()
    {
        string? argument = Environment.GetCommandLineArgs().FirstOrDefault(
            item => item.StartsWith("--bootstrap-handle=", StringComparison.Ordinal));
        if (argument is null || !long.TryParse(argument[19..], out long rawHandle) || rawHandle <= 0)
            throw new InvalidDataException("Bootstrap handle is missing.");
        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(rawHandle), ownsHandle: true);
        return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
    }

    private static LocalTranslationResponse Translate(
        LocalTranslationRequest request,
        Guid epoch,
        ILocalModelRuntime runtime)
    {
        string? validationError = request.ProtocolVersion != LocalWorkerProtocol.Version ||
            request.WorkerSessionEpoch != epoch || request.RequestId == Guid.Empty
            ? "local.staleSession"
            : string.IsNullOrWhiteSpace(request.ModelId) || string.IsNullOrWhiteSpace(request.Text) ||
              request.Text.Length > LocalWorkerProtocol.MaximumTextCharacters ||
              request.MaximumOutputCharacters is < 1 or > LocalWorkerProtocol.MaximumTextCharacters
                ? "local.invalidRequest"
                : null;
        if (validationError is not null)
            return new LocalTranslationResponse(
                LocalWorkerProtocol.Version, epoch, request.RequestId, false, null, validationError);
        PhraseTableTranslationResult result = runtime.Translate(
            request.ModelId,
            request.SourceLanguage,
            request.TargetLanguage,
            request.Text,
            request.MaximumOutputCharacters);
        return new LocalTranslationResponse(
            LocalWorkerProtocol.Version,
            epoch,
            request.RequestId,
            result.Success,
            result.Text,
            result.ErrorCode);
    }

    private static async Task<LocalWorkerBootstrap> ReadBootstrapAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        await ReadFrameAsync<LocalWorkerBootstrap>(stream, cancellationToken);

    private static async Task WriteHandshakeAsync(
        Stream stream,
        Guid epoch,
        byte[] secret,
        CancellationToken cancellationToken)
    {
        byte[] identity = Encoding.UTF8.GetBytes($"{epoch:D}:{Environment.ProcessId}");
        byte[] proof = HMACSHA256.HashData(secret, identity);
        await WriteFrameAsync(stream, new
        {
            protocolVersion = LocalWorkerProtocol.Version,
            workerSessionEpoch = epoch,
            workerProcessId = Environment.ProcessId,
            proof = Convert.ToBase64String(proof),
        }, cancellationToken);
        CryptographicOperations.ZeroMemory(proof);
    }

    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > LocalWorkerProtocol.MaximumFrameBytes) throw new InvalidDataException();
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException();
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > LocalWorkerProtocol.MaximumFrameBytes) throw new InvalidDataException();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint serverProcessId);
}
