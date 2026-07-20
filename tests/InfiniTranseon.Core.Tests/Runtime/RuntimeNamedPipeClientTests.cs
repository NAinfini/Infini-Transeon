using System.IO.Pipes;
using System.Security.Cryptography;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeNamedPipeClientTests
{
    [Fact]
    public void SessionPipeNamesAreRandomAndContainNoRemotePathSyntax()
    {
        string first = RuntimePipeName.Create();
        string second = RuntimePipeName.Create();

        Assert.NotEqual(first, second);
        Assert.StartsWith("infini-transeon.", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", first, StringComparison.Ordinal);
        Assert.DoesNotContain("/", first, StringComparison.Ordinal);
        RuntimePipeName.Validate(first);
    }

    [Fact]
    public async Task LocalCurrentUserPipeCompletesAuthenticatedHandshake()
    {
        string pipeName = RuntimePipeName.Create();
        Guid epoch = Guid.NewGuid();
        byte[] nonce = RandomNumberGenerator.GetBytes(RuntimeProtocol.BootstrapNonceBytes);
        using var server = CreateServer(pipeName);
        Task serverTask = RunServerHandshakeAsync(server, epoch, nonce);

        await using RuntimeNamedPipeConnection connection = await RuntimeNamedPipeClient.ConnectAsync(
            pipeName,
            Environment.ProcessId,
            epoch,
            nonce,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(Environment.ProcessId, connection.AuthenticatedServerProcessId);
        Assert.Equal(epoch, connection.RuntimeEpoch);
        Assert.Equal(RuntimeCapabilities.VersionOne, connection.Capabilities);
        Assert.True(connection.Stream.IsConnected);
    }

    [Fact]
    public async Task ServerPidMismatchDisconnectsBeforeHandshakePayloadIsSent()
    {
        string pipeName = RuntimePipeName.Create();
        byte[] nonce = RandomNumberGenerator.GetBytes(RuntimeProtocol.BootstrapNonceBytes);
        using var server = CreateServer(pipeName);
        Task waitForConnection = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        RuntimeProtocolException error = await Assert.ThrowsAsync<RuntimeProtocolException>(
            () => RuntimeNamedPipeClient.ConnectAsync(
                pipeName,
                int.MaxValue,
                Guid.NewGuid(),
                nonce,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken).AsTask());
        await waitForConnection;
        byte[] probe = new byte[1];
        int bytesRead = await server.ReadAsync(probe, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeProtocolError.AuthenticationFailed, error.Error);
        Assert.Equal(0, bytesRead);
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task RunServerHandshakeAsync(
        NamedPipeServerStream server,
        Guid epoch,
        byte[] nonce)
    {
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using RuntimeFrame request = await RuntimeFrameCodec.ReadAsync(
            server,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        using var authenticator = new RuntimeHandshakeAuthenticator(
            Environment.ProcessId,
            Environment.ProcessId,
            epoch,
            nonce);
        authenticator.Authenticate(request, Environment.ProcessId, DateTimeOffset.UtcNow);
        using RuntimeFrame response = RuntimeHandshakeFrames.CreateAcceptedResponse(
            request.Header,
            Environment.ProcessId,
            RuntimeCapabilities.VersionOne,
            DateTimeOffset.UtcNow.AddSeconds(5));
        await RuntimeFrameCodec.WriteAsync(server, response, TestContext.Current.CancellationToken);
    }
}
