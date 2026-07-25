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
    public void MessageKindWireNumbersAreContiguousAndStable()
    {
        Assert.Equal(
            Enumerable.Range(0, 28),
            Enum.GetValues<RuntimeMessageKind>().Select(value => (int)value));
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
            ["CloudOcrCropRequest", "OcrResult", "Thumbnail"],
            backpressure.GetProperty("dataLaneKinds").EnumerateArray()
                .Select(item => item.GetString()).OfType<string>().ToArray());
        Assert.Equal("caller-configured-finite-window",
            protocol.RootElement.GetProperty("restartPolicy").GetString());
    }

    [Fact]
    public void JsonProtocolDefinesCapabilitiesOwnershipCancellationAndReconnectSemantics()
    {
        using JsonDocument protocol = LoadProtocol();
        JsonElement handshake = protocol.RootElement.GetProperty("handshakeResponse");
        JsonElement contracts = protocol.RootElement.GetProperty("flowContracts");

        Assert.Equal(180, handshake.GetProperty("payloadBytes").GetInt32());
        Assert.Equal("RuntimeCapabilitiesV1", handshake.GetProperty("capabilities").GetString());
        Assert.Equal("sender-owns-until-write-completes; receiver-copies-or-takes-explicit-lease",
            contracts.GetProperty("ownership").GetString());
        Assert.Equal("execution-token-and-request-id",
            contracts.GetProperty("cancellation").GetString());
        Assert.Equal("complete-cumulative-snapshot",
            contracts.GetProperty("streaming").GetString());
        Assert.Equal("full-state-no-text-replay",
            contracts.GetProperty("reconnect").GetString());
        Assert.Equal("dedicated-serialized-owner-per-lane",
            contracts.GetProperty("callbackThreads").GetString());
    }

    [Theory]
    [InlineData(RuntimeMessageKind.OcrResult)]
    [InlineData(RuntimeMessageKind.CloudOcrCropRequest)]
    [InlineData(RuntimeMessageKind.TranslationOutput)]
    [InlineData(RuntimeMessageKind.TranslationStreamSnapshot)]
    [InlineData(RuntimeMessageKind.OverlayDesiredState)]
    [InlineData(RuntimeMessageKind.Thumbnail)]
    public void TextAndImagePayloadsAreClearedWhenFramesAreDisposed(RuntimeMessageKind kind)
    {
        byte[] secret = "private translated pixels"u8.ToArray();
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            kind,
            Guid.NewGuid(),
            Guid.NewGuid(),
            secret.Length,
            DateTimeOffset.UtcNow.AddMinutes(1));
        using var frame = new RuntimeFrame(header, secret);
        ReadOnlyMemory<byte> payload = frame.Payload;

        frame.Dispose();

        Assert.All(payload.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void CaptureTargetCommandBinaryPayloadRoundTripsWithoutNativePointerGuessing()
    {
        var window = new RuntimeCaptureTargetCommand(
            RuntimeCaptureTargetOperation.Upsert,
            7,
            new CaptureTargetId(Guid.NewGuid()),
            new TargetInstanceId(Guid.NewGuid()),
            RuntimeCaptureTargetKind.Window,
            0x1234,
            null);
        var desktop = new RuntimeCaptureTargetCommand(
            RuntimeCaptureTargetOperation.Upsert,
            8,
            new CaptureTargetId(Guid.NewGuid()),
            new TargetInstanceId(Guid.NewGuid()),
            RuntimeCaptureTargetKind.DesktopRegion,
            0,
            new OverlayPixelRect(-100, 50, 800, 600));

        Assert.Equal(window, RuntimeCaptureTargetPayloadCodec.Decode(
            RuntimeCaptureTargetPayloadCodec.Encode(window)));
        Assert.Equal(desktop, RuntimeCaptureTargetPayloadCodec.Decode(
            RuntimeCaptureTargetPayloadCodec.Encode(desktop)));
        Assert.Throws<InvalidDataException>(() => RuntimeCaptureTargetPayloadCodec.Decode(new byte[71]));
        Assert.Throws<ArgumentException>(() => new RuntimeCaptureTargetCommand(
            RuntimeCaptureTargetOperation.Upsert,
            1,
            new CaptureTargetId(Guid.NewGuid()),
            new TargetInstanceId(Guid.NewGuid()),
            RuntimeCaptureTargetKind.Window,
            0,
            null));
        Assert.Throws<ArgumentException>(() => new RuntimeCaptureTargetCommand(
            RuntimeCaptureTargetOperation.Upsert,
            1,
            new CaptureTargetId(Guid.NewGuid()),
            new TargetInstanceId(Guid.NewGuid()),
            RuntimeCaptureTargetKind.DesktopRegion,
            0,
            new OverlayPixelRect(0, 0, 16_385, 1080)));
    }

    [Fact]
    public void ProcessingConfigurationRoundTripsBoundedRegionsAndOcrSettings()
    {
        TargetInstanceId target = new(Guid.NewGuid());
        var value = new RuntimeProcessingConfiguration(
            target,
            configurationRevision: 9,
            profileId: Guid.NewGuid(),
            profileRevision: 4,
            detectionLongEdge: 1920,
            scanRemainingArea: true,
            remainingAreaIntervalMilliseconds: 1000,
            [new RuntimeProcessingRegion(
                new RegionId(Guid.NewGuid()),
                new NormalizedRect(0.1, 0.6, 0.8, 0.3),
                RuntimeRegionPriority.P0,
                CaptureAreaKind.UserRegion,
                recognitionIntervalMilliseconds: 125,
                lockDegradation: true,
                detectOrientation: true,
                useCloudOcr: false,
                cloudConsentPolicyRevision: 0,
                detectionScale: 0.75,
                lineBreakMode: RuntimeLineBreakMode.KeyValueRows,
                ocrProviderId: "ocr.windows.media",
                recognitionLanguage: "ja-JP",
                preprocessingPipeline: "grayscale|threshold")]);

        RuntimeProcessingConfiguration decoded = RuntimeProcessingConfigurationPayloadCodec.Decode(
            RuntimeProcessingConfigurationPayloadCodec.Encode(value));

        Assert.Equal(value.TargetInstanceId, decoded.TargetInstanceId);
        Assert.Equal(value.ConfigurationRevision, decoded.ConfigurationRevision);
        Assert.Equal(value.ProfileId, decoded.ProfileId);
        Assert.Equal(value.ProfileRevision, decoded.ProfileRevision);
        Assert.Equal(value.DetectionLongEdge, decoded.DetectionLongEdge);
        Assert.Equal(value.ScanRemainingArea, decoded.ScanRemainingArea);
        Assert.Equal(value.RemainingAreaIntervalMilliseconds, decoded.RemainingAreaIntervalMilliseconds);
        Assert.Equal(value.Regions, decoded.Regions);
        Assert.Throws<InvalidDataException>(() =>
            RuntimeProcessingConfigurationPayloadCodec.Decode(new byte[55]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeProcessingConfiguration(
            target, 10, Guid.NewGuid(), 4, 1921, false, 1000, []));
    }

    [Fact]
    public void ProcessingConfigurationAcknowledgementRejectsUnstableErrors()
    {
        TargetInstanceId target = new(Guid.NewGuid());
        var accepted = new RuntimeProcessingConfigurationAcknowledgement(
            target, 7, true, RuntimeProcessingConfigurationStatus.Applied, null);
        var rejected = new RuntimeProcessingConfigurationAcknowledgement(
            target, 8, false, RuntimeProcessingConfigurationStatus.TargetMissing,
            "runtime.processing.targetMissing");

        Assert.Equal(accepted, RuntimeProcessingConfigurationAcknowledgementPayloadCodec.Decode(
            RuntimeProcessingConfigurationAcknowledgementPayloadCodec.Encode(accepted)));
        Assert.Equal(rejected, RuntimeProcessingConfigurationAcknowledgementPayloadCodec.Decode(
            RuntimeProcessingConfigurationAcknowledgementPayloadCodec.Encode(rejected)));
        Assert.Throws<ArgumentException>(() => new RuntimeProcessingConfigurationAcknowledgement(
            target, 9, false, RuntimeProcessingConfigurationStatus.InvalidConfiguration,
            "contains spaces"));
    }

    [Fact]
    public void CloudOcrCropPayloadRoundTripsCompleteConsentAndExecutionIdentity()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        OcrExecutionToken token = OcrToken(resultSequence: 2, isManual: true);
        using var value = new CloudOcrCropRequest(
            token, "image/png", [1, 2, 3, 4], 640, 160, true,
            consentPolicyRevision: 7,
            encodedByteCeiling: 1024,
            deadlineUtc: deadline,
            providerId: "ocr.tencent");

        using CloudOcrCropRequest decoded = RuntimeCloudOcrCropRequestPayloadCodec.Decode(
            RuntimeCloudOcrCropRequestPayloadCodec.Encode(value));

        Assert.Equal(token, decoded.ExecutionToken);
        Assert.Equal("image/png", decoded.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, decoded.EncodedCrop.ToArray());
        Assert.Equal(7, decoded.ConsentPolicyRevision);
        Assert.Equal(1024, decoded.EncodedByteCeiling);
        Assert.Equal(deadline, decoded.DeadlineUtc);
        Assert.Equal("ocr.tencent", decoded.ProviderId);
    }

    [Fact]
    public void OcrResultPayloadRoundTripsUnicodeLinesAndStableMetadata()
    {
        var value = new OcrResultSnapshot(
            OcrToken(resultSequence: 1, isManual: true),
            [new TextLine("攻撃：100", new NormalizedRect(0.1, 0.2, 0.3, 0.1), 0.98)
            {
                OrientationDegrees = 90,
                IsVertical = true,
            }],
            "pp-ocr-v5",
            "2026.07",
            IsStable: true,
            TerminalErrorCode: null);

        OcrResultSnapshot decoded = RuntimeOcrResultPayloadCodec.Decode(
            RuntimeOcrResultPayloadCodec.Encode(value));

        Assert.Equal(value.ExecutionToken, decoded.ExecutionToken);
        Assert.True(decoded.ExecutionToken.IsManual);
        Assert.Equal(value.Lines, decoded.Lines);
        Assert.Equal(90, decoded.Lines[0].OrientationDegrees);
        Assert.True(decoded.Lines[0].IsVertical);
        Assert.Equal(value.ModelId, decoded.ModelId);
        Assert.Equal(value.ModelVersion, decoded.ModelVersion);
        Assert.True(decoded.IsStable);
    }

    [Fact]
    public void OcrResultAcknowledgementRoundTripsIdentityAndExplicitRejection()
    {
        var value = new RuntimeOcrResultAcknowledgement(
            new TargetInstanceId(Guid.NewGuid()),
            17,
            9,
            RuntimeOcrResultApplyStatus.StaleGeneration,
            "ocr.result.staleGeneration");

        RuntimeOcrResultAcknowledgement decoded =
            RuntimeOcrResultAcknowledgementPayloadCodec.Decode(
                RuntimeOcrResultAcknowledgementPayloadCodec.Encode(value));

        Assert.Equal(value, decoded);
        Assert.False(decoded.Accepted);
    }

    [Fact]
    public void CaptureTargetAcknowledgementBinaryPayloadRoundTripsAndRejectsInvalidErrorCodes()
    {
        var targetId = new CaptureTargetId(Guid.NewGuid());
        var instanceId = new TargetInstanceId(Guid.NewGuid());
        var accepted = new RuntimeCaptureTargetAcknowledgement(
            7,
            targetId,
            instanceId,
            true,
            TargetLifecycleState.Running,
            null);
        var rejected = new RuntimeCaptureTargetAcknowledgement(
            8,
            targetId,
            instanceId,
            false,
            TargetLifecycleState.OccludedOrUnsupported,
            "capture.targetUnsupported",
            unchecked((int)0x80004005));

        Assert.Equal(accepted, RuntimeCaptureTargetAcknowledgementPayloadCodec.Decode(
            RuntimeCaptureTargetAcknowledgementPayloadCodec.Encode(accepted)));
        Assert.Equal(rejected, RuntimeCaptureTargetAcknowledgementPayloadCodec.Decode(
            RuntimeCaptureTargetAcknowledgementPayloadCodec.Encode(rejected)));
        Assert.Equal(unchecked((int)0x80004005), rejected.NativeErrorCode);
        Assert.Throws<ArgumentException>(() => new RuntimeCaptureTargetAcknowledgement(
            9,
            targetId,
            instanceId,
            true,
            TargetLifecycleState.Running,
            "capture.unexpected"));
        Assert.Throws<ArgumentException>(() => new RuntimeCaptureTargetAcknowledgement(
            9,
            targetId,
            instanceId,
            false,
            TargetLifecycleState.Closed,
            "contains spaces"));
        Assert.Throws<InvalidDataException>(() =>
            RuntimeCaptureTargetAcknowledgementPayloadCodec.Decode(new byte[55]));
    }

    [Fact]
    public void TargetLifecycleBinaryPayloadRoundTripsWithDimensionsAndNativeFailure()
    {
        var value = new TargetLifecycleEvent(
            new TargetSnapshot(
                new TargetInstanceId(Guid.NewGuid()),
                new CaptureTargetId(Guid.NewGuid()),
                TargetLifecycleState.DeviceLost,
                3840,
                2160,
                144),
            19,
            DateTimeOffset.UtcNow,
            "capture.deviceLost")
        {
            NativeErrorCode = unchecked((int)0x887A0005),
        };

        TargetLifecycleEvent decoded = RuntimeTargetLifecyclePayloadCodec.Decode(
            RuntimeTargetLifecyclePayloadCodec.Encode(value));

        Assert.Equal(value, decoded);
        Assert.Equal(value.NativeErrorCode, decoded.NativeErrorCode);
        Assert.Throws<InvalidDataException>(() =>
            RuntimeTargetLifecyclePayloadCodec.Decode(new byte[75]));
    }

    [Fact]
    public void OverlayBinaryPayloadRoundTripsUnicodeFixedSlotsAndStyle()
    {
        Guid epoch = Guid.NewGuid();
        TargetInstanceId target = new(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        Guid firstSlot = Guid.NewGuid();
        var value = new OverlayDesiredState(
            epoch,
            target,
            27,
            [new OverlayRegionSnapshot(
                regionId,
                new OverlayPixelRect(12, 34, 900, 240),
                new OverlayRegionStyleSnapshot(
                    OverlayBackgroundTreatment.AutomaticContrast,
                    OverlayTextAlignment.Center,
                    "#CC112233",
                    "#FFEEDDCC",
                    0.75,
                    14,
                    11,
                    30,
                    15,
                    5)
                {
                    OutlineColor = "#FF010203",
                    OutlineWidth = 1.5,
                    MinimumDwellMilliseconds = 650,
                    CrossfadeMilliseconds = 140,
                    ReducedMotion = true,
                },
                [
                    new OverlaySlotSnapshot(firstSlot, 0, OverlaySlotState.Streaming, "勇者：こんにちは", "主译")
                    {
                        StageIndex = 2,
                    },
                    new OverlaySlotSnapshot(Guid.NewGuid(), 1, OverlaySlotState.Waiting, "", "候选 2"),
                ])]);

        OverlayDesiredState decoded = RuntimeOverlayDesiredStatePayloadCodec.Decode(
            RuntimeOverlayDesiredStatePayloadCodec.Encode(value));

        Assert.Equal(epoch, decoded.RuntimeEpoch);
        Assert.Equal(target, decoded.TargetInstanceId);
        Assert.Equal(27, decoded.OverlayRevision);
        OverlayRegionSnapshot region = Assert.Single(decoded.Regions);
        Assert.Equal(regionId, region.RegionId);
        Assert.Equal(value.Regions[0].Bounds, region.Bounds);
        Assert.Equal(value.Regions[0].Style, region.Style);
        Assert.Equal(firstSlot, region.OrderedSlots[0].SlotId);
        Assert.Equal("勇者：こんにちは", region.OrderedSlots[0].Text);
        Assert.Equal("主译", region.OrderedSlots[0].Label);
        Assert.Equal(2, region.OrderedSlots[0].StageIndex);
    }

    [Fact]
    public void OffsetOverlayCarriesAnIndependentDestinationRectangle()
    {
        var style = new OverlayRegionStyleSnapshot(
            OverlayBackgroundTreatment.Offset,
            OverlayTextAlignment.Left,
            "#CC000000",
            "#FFFFFFFF",
            0.8,
            0,
            8,
            24,
            12,
            4)
        {
            DestinationBounds = new OverlayPixelRect(1000, 100, 600, 300),
        };
        var value = new OverlayDesiredState(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            1,
            [new OverlayRegionSnapshot(
                Guid.NewGuid(),
                new OverlayPixelRect(100, 700, 800, 200),
                style,
                [])]);

        OverlayDesiredState decoded = RuntimeOverlayDesiredStatePayloadCodec.Decode(
            RuntimeOverlayDesiredStatePayloadCodec.Encode(value));

        Assert.Equal(style.DestinationBounds, Assert.Single(decoded.Regions).Style.DestinationBounds);
        Assert.Throws<ArgumentException>(() => new OverlayRegionSnapshot(
            Guid.NewGuid(),
            new OverlayPixelRect(100, 700, 800, 200),
            style with { DestinationBounds = null },
            []));
    }

    [Fact]
    public void OverlayAcknowledgementAndPolicyPayloadsRejectMalformedState()
    {
        TargetInstanceId target = new(Guid.NewGuid());
        var applied = new RuntimeOverlayAcknowledgement(
            target,
            8,
            true,
            RuntimeOverlayApplyStatus.Applied,
            null);
        var rejected = new RuntimeOverlayAcknowledgement(
            target,
            9,
            false,
            RuntimeOverlayApplyStatus.InvalidState,
            "overlay.invalidState",
            unchecked((int)0x80070057));

        Assert.Equal(applied, RuntimeOverlayAcknowledgementPayloadCodec.Decode(
            RuntimeOverlayAcknowledgementPayloadCodec.Encode(applied)));
        Assert.Equal(rejected, RuntimeOverlayAcknowledgementPayloadCodec.Decode(
            RuntimeOverlayAcknowledgementPayloadCodec.Encode(rejected)));
        var missingBackground = new RuntimeOverlayAcknowledgement(
            target,
            10,
            false,
            RuntimeOverlayApplyStatus.BackgroundUnavailable,
            "overlay.temporalBackgroundUnavailable");
        Assert.Equal(missingBackground, RuntimeOverlayAcknowledgementPayloadCodec.Decode(
            RuntimeOverlayAcknowledgementPayloadCodec.Encode(missingBackground)));

        RegionId region = new(Guid.NewGuid());
        Guid profileId = Guid.NewGuid();
        var policy = new PolicyRevision(12, profileId, 4, new Dictionary<RegionId, string>
        {
            [region] = "3:paused",
        });
        PolicyRevision decodedPolicy = RuntimePolicyRevisionPayloadCodec.Decode(
            RuntimePolicyRevisionPayloadCodec.Encode(policy));
        Assert.Equal(12, decodedPolicy.Revision);
        Assert.Equal(profileId, decodedPolicy.ProfileId);
        Assert.Equal(4, decodedPolicy.ProfileRevision);
        Assert.Equal("3:paused", decodedPolicy.RegionPolicies[region]);

        var acknowledgement = new PolicyAcknowledgement(12, false, "runtime.policy.capacity");
        Assert.Equal(acknowledgement, RuntimePolicyAcknowledgementPayloadCodec.Decode(
            RuntimePolicyAcknowledgementPayloadCodec.Encode(acknowledgement)));
        Assert.Throws<InvalidDataException>(() =>
            RuntimeOverlayDesiredStatePayloadCodec.Decode(new byte[39]));
        Assert.Throws<ArgumentException>(() => new RuntimeOverlayAcknowledgement(
            target, 11, true, RuntimeOverlayApplyStatus.Applied, "overlay.error"));
    }

    private static RuntimeEnvelopeHeader ValidHeader() => new(
        RuntimeProtocol.CurrentVersion,
        RuntimeMessageKind.HandshakeRequest,
        Guid.NewGuid(),
        Guid.NewGuid(),
        0,
        DateTimeOffset.UtcNow.AddMinutes(1));

    private static OcrExecutionToken OcrToken(
        long resultSequence,
        bool isManual = false) => new(
        new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.UserRegion(new RegionId(Guid.NewGuid())),
            new TextTrackId(Guid.NewGuid()),
            3,
            4),
        Guid.NewGuid(),
        1,
        resultSequence,
        isManual);

    private static JsonDocument LoadProtocol() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "InfiniTranseon.EngineHost",
        "ipc",
        "runtime-protocol.json")));

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
