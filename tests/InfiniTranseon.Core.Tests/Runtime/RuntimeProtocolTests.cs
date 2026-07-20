using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeProtocolTests
{
    [Fact]
    public void CurrentProtocolUsesTheCapabilitiesMessageCeiling()
    {
        Assert.Equal(1, RuntimeProtocol.CurrentVersion);
        Assert.Equal(RuntimeProtocol.MaxMessageBytes, RuntimeCapabilities.VersionOne.MaxIpcMessageBytes);
    }

    [Fact]
    public void ValidEnvelopeHeaderIsAccepted()
    {
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            RuntimeMessageKind.TargetSnapshot,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1024,
            DateTimeOffset.UtcNow.AddSeconds(5));

        RuntimeProtocolValidator.Validate(header, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void MismatchedProtocolVersionIsRejected()
    {
        RuntimeEnvelopeHeader header = ValidHeader() with { ProtocolVersion = 2 };

        RuntimeProtocolException error = Assert.Throws<RuntimeProtocolException>(
            () => RuntimeProtocolValidator.Validate(header, DateTimeOffset.UtcNow));

        Assert.Equal(RuntimeProtocolError.VersionMismatch, error.Error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8_388_609)]
    public void InvalidPayloadLengthIsRejected(int payloadLength)
    {
        RuntimeEnvelopeHeader header = ValidHeader() with { PayloadLength = payloadLength };

        RuntimeProtocolException error = Assert.Throws<RuntimeProtocolException>(
            () => RuntimeProtocolValidator.Validate(header, DateTimeOffset.UtcNow));

        Assert.Equal(RuntimeProtocolError.InvalidPayloadLength, error.Error);
    }

    [Fact]
    public void EmptyRequestOrRuntimeIdentityIsRejected()
    {
        RuntimeEnvelopeHeader emptyRequest = ValidHeader() with { RequestId = Guid.Empty };
        RuntimeEnvelopeHeader emptyEpoch = ValidHeader() with { RuntimeEpoch = Guid.Empty };

        Assert.Equal(
            RuntimeProtocolError.InvalidRequestId,
            Assert.Throws<RuntimeProtocolException>(
                () => RuntimeProtocolValidator.Validate(emptyRequest, DateTimeOffset.UtcNow)).Error);
        Assert.Equal(
            RuntimeProtocolError.InvalidRuntimeEpoch,
            Assert.Throws<RuntimeProtocolException>(
                () => RuntimeProtocolValidator.Validate(emptyEpoch, DateTimeOffset.UtcNow)).Error);
    }

    [Fact]
    public void ExpiredMessageIsRejected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RuntimeEnvelopeHeader header = ValidHeader() with { DeadlineUtc = now.AddMilliseconds(-1) };

        RuntimeProtocolException error = Assert.Throws<RuntimeProtocolException>(
            () => RuntimeProtocolValidator.Validate(header, now));

        Assert.Equal(RuntimeProtocolError.DeadlineExpired, error.Error);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(42, 31)]
    [InlineData(42, 33)]
    public void HandshakeRequiresPidAndExactlyThirtyTwoNonceBytes(int processId, int nonceLength)
    {
        Assert.Throws<RuntimeProtocolException>(() => RuntimeHandshakeFrames.CreateRequest(
            localProcessId: 42,
            expectedPeerProcessId: processId,
            Guid.NewGuid(),
            new byte[nonceLength],
            DateTimeOffset.UtcNow.AddSeconds(5)));
    }

    [Fact]
    public void JsonProtocolListsEveryRequiredBidirectionalFlow()
    {
        string root = FindRepositoryRoot();
        string protocolPath = Path.Combine(
            root,
            "src",
            "InfiniTranseon.EngineHost",
            "ipc",
            "runtime-protocol.json");
        using JsonDocument protocol = JsonDocument.Parse(File.ReadAllText(protocolPath));
        string[] messageKinds = protocol.RootElement.GetProperty("messageKinds")
            .EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .ToArray();

        string[] requiredKinds = Enum.GetNames<RuntimeMessageKind>();
        Assert.Empty(requiredKinds.Except(messageKinds, StringComparer.Ordinal));
        Assert.Empty(messageKinds.Except(requiredKinds, StringComparer.Ordinal));
    }

    [Fact]
    public void JsonProtocolMatchesTheBinaryWireEnvelope()
    {
        string root = FindRepositoryRoot();
        string protocolPath = Path.Combine(
            root,
            "src",
            "InfiniTranseon.EngineHost",
            "ipc",
            "runtime-protocol.json");
        using JsonDocument protocol = JsonDocument.Parse(File.ReadAllText(protocolPath));
        JsonElement wire = protocol.RootElement.GetProperty("wireEnvelope");

        Assert.Equal(RuntimeProtocol.FramePrefixBytes, wire.GetProperty("lengthPrefixBytes").GetInt32());
        Assert.Equal(RuntimeProtocol.WireHeaderBytes, wire.GetProperty("headerBytes").GetInt32());
        Assert.Equal("little-endian", wire.GetProperty("byteOrder").GetString());
        Assert.Equal("utc-ticks", wire.GetProperty("deadlineEncoding").GetString());
    }

    [Fact]
    public void JsonProtocolPinsSecureBootstrapAndPipeCreation()
    {
        string root = FindRepositoryRoot();
        string protocolPath = Path.Combine(
            root,
            "src",
            "InfiniTranseon.EngineHost",
            "ipc",
            "runtime-protocol.json");
        using JsonDocument protocol = JsonDocument.Parse(File.ReadAllText(protocolPath));
        JsonElement bootstrap = protocol.RootElement.GetProperty("bootstrapTransport");
        JsonElement pipe = protocol.RootElement.GetProperty("namedPipeSecurity");

        Assert.Equal("inherited-anonymous-pipe-read-handle",
            bootstrap.GetProperty("secretDelivery").GetString());
        Assert.True(bootstrap.GetProperty("explicitHandleListOnly").GetBoolean());
        Assert.Equal(
            ["command-line", "environment", "logs", "persistent-files"],
            bootstrap.GetProperty("secretExcludedFrom").EnumerateArray()
                .Select(item => item.GetString()).OfType<string>().ToArray());
        Assert.True(pipe.GetProperty("firstInstance").GetBoolean());
        Assert.True(pipe.GetProperty("rejectRemoteClients").GetBoolean());
        Assert.Equal("current-logon-user", pipe.GetProperty("acl").GetString());
    }

    [Fact]
    public void JsonProtocolPinsPostHandshakeFlowControlAndShutdown()
    {
        string root = FindRepositoryRoot();
        string protocolPath = Path.Combine(
            root,
            "src",
            "InfiniTranseon.EngineHost",
            "ipc",
            "runtime-protocol.json");
        using JsonDocument protocol = JsonDocument.Parse(File.ReadAllText(protocolPath));
        JsonElement postHandshake = protocol.RootElement.GetProperty("postHandshake");
        JsonElement backpressure = protocol.RootElement.GetProperty("backpressure");

        Assert.Equal("ControlRequest", postHandshake.GetProperty("heartbeatRequest").GetString());
        Assert.Equal("ControlResponse", postHandshake.GetProperty("heartbeatResponse").GetString());
        Assert.Equal("ShutdownRequest", postHandshake.GetProperty("shutdownRequest").GetString());
        Assert.Equal("ShutdownAcknowledgement",
            postHandshake.GetProperty("shutdownResponse").GetString());
        Assert.Equal(RuntimeProtocol.MaxInFlightBytes,
            backpressure.GetProperty("sharedMaxBytes").GetInt64());
        Assert.Equal(
            ["CloudOcrCropRequest", "Thumbnail"],
            backpressure.GetProperty("dataLaneKinds").EnumerateArray()
                .Select(item => item.GetString()).OfType<string>().ToArray());
        Assert.Equal("caller-configured-finite-window",
            protocol.RootElement.GetProperty("restartPolicy").GetString());
    }

    private static RuntimeEnvelopeHeader ValidHeader() => new(
        RuntimeProtocol.CurrentVersion,
        RuntimeMessageKind.HandshakeRequest,
        Guid.NewGuid(),
        Guid.NewGuid(),
        0,
        DateTimeOffset.UtcNow.AddMinutes(1));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InfiniTranseon.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Infini-Transeon repository root.");
    }
}
