using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeHandshakeAuthenticatorTests
{
    [Fact]
    public void MatchingPidEpochPeerAndNonceAuthenticateOnce()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid epoch = Guid.NewGuid();
        byte[] nonce = Enumerable.Range(0, RuntimeProtocol.BootstrapNonceBytes)
            .Select(value => (byte)value)
            .ToArray();
        using var authenticator = new RuntimeHandshakeAuthenticator(
            localProcessId: 200,
            expectedClientProcessId: 100,
            epoch,
            nonce);
        using RuntimeFrame request = RuntimeHandshakeFrames.CreateRequest(
            localProcessId: 100,
            expectedPeerProcessId: 200,
            epoch,
            nonce,
            now.AddSeconds(5));

        RuntimeHandshakeResult result = authenticator.Authenticate(request, actualClientProcessId: 100, now);

        Assert.Equal(100, result.AuthenticatedClientProcessId);
        Assert.Equal(epoch, result.RuntimeEpoch);
        Assert.Throws<RuntimeProtocolException>(
            () => authenticator.Authenticate(request, actualClientProcessId: 100, now));
    }

    [Fact]
    public void WrongNonceConsumesTheOnlyAuthenticationAttempt()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid epoch = Guid.NewGuid();
        byte[] expectedNonce = new byte[RuntimeProtocol.BootstrapNonceBytes];
        byte[] wrongNonce = Enumerable.Repeat((byte)1, RuntimeProtocol.BootstrapNonceBytes).ToArray();
        using var authenticator = new RuntimeHandshakeAuthenticator(200, 100, epoch, expectedNonce);
        using RuntimeFrame wrongRequest = RuntimeHandshakeFrames.CreateRequest(
            100,
            200,
            epoch,
            wrongNonce,
            now.AddSeconds(5));
        using RuntimeFrame laterCorrectRequest = RuntimeHandshakeFrames.CreateRequest(
            100,
            200,
            epoch,
            expectedNonce,
            now.AddSeconds(5));

        RuntimeProtocolException first = Assert.Throws<RuntimeProtocolException>(
            () => authenticator.Authenticate(wrongRequest, 100, now));
        RuntimeProtocolException second = Assert.Throws<RuntimeProtocolException>(
            () => authenticator.Authenticate(laterCorrectRequest, 100, now));

        Assert.Equal(RuntimeProtocolError.AuthenticationFailed, first.Error);
        Assert.Equal(RuntimeProtocolError.HandshakeAlreadyAttempted, second.Error);
    }

    [Theory]
    [InlineData(101, 200)]
    [InlineData(100, 201)]
    public void WrongActualClientOrExpectedPeerPidIsRejected(int actualClientProcessId, int expectedPeerProcessId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid epoch = Guid.NewGuid();
        byte[] nonce = new byte[RuntimeProtocol.BootstrapNonceBytes];
        using var authenticator = new RuntimeHandshakeAuthenticator(200, 100, epoch, nonce);
        using RuntimeFrame request = RuntimeHandshakeFrames.CreateRequest(
            100,
            expectedPeerProcessId,
            epoch,
            nonce,
            now.AddSeconds(5));

        RuntimeProtocolException error = Assert.Throws<RuntimeProtocolException>(
            () => authenticator.Authenticate(request, actualClientProcessId, now));

        Assert.Equal(RuntimeProtocolError.AuthenticationFailed, error.Error);
    }

    [Fact]
    public void HandshakeFrameClearsSensitivePayloadWhenDisposed()
    {
        byte[] nonce = Enumerable.Repeat((byte)0xA5, RuntimeProtocol.BootstrapNonceBytes).ToArray();
        RuntimeFrame frame = RuntimeHandshakeFrames.CreateRequest(
            100,
            200,
            Guid.NewGuid(),
            nonce,
            DateTimeOffset.UtcNow.AddSeconds(5));

        frame.Dispose();

        Assert.All(frame.Payload.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void AcceptedResponseRoundTripsAllCapabilities()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid epoch = Guid.NewGuid();
        using RuntimeFrame request = RuntimeHandshakeFrames.CreateRequest(
            100,
            200,
            epoch,
            new byte[RuntimeProtocol.BootstrapNonceBytes],
            now.AddSeconds(5));
        using RuntimeFrame response = RuntimeHandshakeFrames.CreateAcceptedResponse(
            request.Header,
            200,
            RuntimeCapabilities.VersionOne,
            now.AddSeconds(5));

        RuntimeHandshakeAcceptance accepted = RuntimeHandshakeFrames.ValidateAcceptedResponse(
            response,
            request.Header,
            200,
            now);

        Assert.Equal(200, accepted.AuthenticatedPeerProcessId);
        Assert.Equal(RuntimeCapabilities.VersionOne, accepted.Capabilities);
    }
}
