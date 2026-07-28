using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace InfiniTranseon.Contracts.Runtime;

public enum RuntimeCaptureTargetOperation : byte
{
    Upsert = 1,
    Remove = 2,
}

public enum RuntimeCaptureTargetKind : byte
{
    Window = 1,
    Monitor = 2,
    DesktopRegion = 3,
}

public sealed record RuntimeCaptureTargetCommand
{
    public RuntimeCaptureTargetCommand(
        RuntimeCaptureTargetOperation operation,
        long commandRevision,
        CaptureTargetId targetId,
        TargetInstanceId targetInstanceId,
        RuntimeCaptureTargetKind targetKind,
        ulong nativeHandle,
        OverlayPixelRect? desktopRegion)
    {
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(targetKind))
            throw new ArgumentOutOfRangeException(nameof(operation));
        ArgumentOutOfRangeException.ThrowIfLessThan(commandRevision, 1);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        bool validDesktopRegion = desktopRegion is { Width: > 0 and <= 16_384,
            Height: > 0 and <= 16_384 } region &&
            (long)region.X + region.Width <= int.MaxValue &&
            (long)region.Y + region.Height <= int.MaxValue;
        bool validUpsert = operation != RuntimeCaptureTargetOperation.Upsert || targetKind switch
        {
            RuntimeCaptureTargetKind.Window or RuntimeCaptureTargetKind.Monitor =>
                nativeHandle != 0 && desktopRegion is null,
            RuntimeCaptureTargetKind.DesktopRegion =>
                nativeHandle == 0 && validDesktopRegion,
            _ => false,
        };
        bool validRemove = operation != RuntimeCaptureTargetOperation.Remove ||
            nativeHandle == 0 && desktopRegion is null;
        if (!validUpsert || !validRemove)
            throw new ArgumentException("Capture target command fields do not match the operation and target kind.");
        Operation = operation;
        CommandRevision = commandRevision;
        TargetId = targetId;
        TargetInstanceId = targetInstanceId;
        TargetKind = targetKind;
        NativeHandle = nativeHandle;
        DesktopRegion = desktopRegion;
    }

    public RuntimeCaptureTargetOperation Operation { get; }
    public long CommandRevision { get; }
    public CaptureTargetId TargetId { get; }
    public TargetInstanceId TargetInstanceId { get; }
    public RuntimeCaptureTargetKind TargetKind { get; }
    public ulong NativeHandle { get; }
    public OverlayPixelRect? DesktopRegion { get; }
}

public sealed record RuntimeCaptureTargetAcknowledgement
{
    public RuntimeCaptureTargetAcknowledgement(
        long commandRevision,
        CaptureTargetId targetId,
        TargetInstanceId targetInstanceId,
        bool accepted,
        TargetLifecycleState state,
        string? errorCode,
        int? nativeErrorCode = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandRevision, 1);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        bool validError = accepted
            ? errorCode is null
            : IsStableErrorCode(errorCode);
        if (!validError)
            throw new ArgumentException(
                "Accepted acknowledgements cannot have an error code; rejected acknowledgements require a stable error code.",
                nameof(errorCode));
        if (accepted && nativeErrorCode is not null)
            throw new ArgumentException(
                "Accepted acknowledgements cannot have a native error code.",
                nameof(nativeErrorCode));

        CommandRevision = commandRevision;
        TargetId = targetId;
        TargetInstanceId = targetInstanceId;
        Accepted = accepted;
        State = state;
        ErrorCode = errorCode;
        NativeErrorCode = nativeErrorCode;
    }

    public long CommandRevision { get; }
    public CaptureTargetId TargetId { get; }
    public TargetInstanceId TargetInstanceId { get; }
    public bool Accepted { get; }
    public TargetLifecycleState State { get; }
    public string? ErrorCode { get; }
    public int? NativeErrorCode { get; }

    private static bool IsStableErrorCode(string? value) =>
        value is { Length: > 0 and <= RuntimeCaptureTargetAcknowledgementPayloadCodec.MaximumErrorCodeBytes } &&
        value.All(character => character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');
}

public static class RuntimeCaptureTargetPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int PayloadBytes = 72;

    public static byte[] Encode(RuntimeCaptureTargetCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        byte[] payload = new byte[PayloadBytes];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        bytes[4] = (byte)command.Operation;
        bytes[5] = (byte)command.TargetKind;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], command.CommandRevision);
        command.TargetId.Value.TryWriteBytes(bytes[16..32]);
        command.TargetInstanceId.Value.TryWriteBytes(bytes[32..48]);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], command.NativeHandle);
        OverlayPixelRect? region = command.DesktopRegion;
        BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], region?.X ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[60..], region?.Y ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[64..], region?.Width ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[68..], region?.Height ?? 0);
        return payload;
    }

    public static RuntimeCaptureTargetCommand Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[6] != 0 || payload[7] != 0)
            throw new InvalidDataException("Capture target payload header is invalid.");
        var operation = (RuntimeCaptureTargetOperation)payload[4];
        var kind = (RuntimeCaptureTargetKind)payload[5];
        int width = BinaryPrimitives.ReadInt32LittleEndian(payload[64..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(payload[68..]);
        var region = kind == RuntimeCaptureTargetKind.DesktopRegion &&
            operation == RuntimeCaptureTargetOperation.Upsert
            ? new OverlayPixelRect(
                BinaryPrimitives.ReadInt32LittleEndian(payload[56..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[60..]),
                width,
                height)
            : null;
        try
        {
            return new RuntimeCaptureTargetCommand(
                operation,
                BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
                new CaptureTargetId(new Guid(payload[16..32])),
                new TargetInstanceId(new Guid(payload[32..48])),
                kind,
                BinaryPrimitives.ReadUInt64LittleEndian(payload[48..]),
                region);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Capture target payload fields are invalid.", exception);
        }
    }

    public static RuntimeFrameDescriptor CreateFrameDescriptor(
        RuntimeCaptureTargetCommand command,
        Guid runtimeEpoch,
        DateTimeOffset deadlineUtc) => new(
            RuntimeMessageKind.CaptureTargetCommand,
            Guid.NewGuid(),
            runtimeEpoch,
            deadlineUtc,
            Encode(command));
}

public static class RuntimeCaptureTargetAcknowledgementPayloadCodec
{
    public const int SchemaVersion = 2;
    public const int FixedPayloadBytes = 60;
    public const int MaximumErrorCodeBytes = 128;

    public static byte[] Encode(RuntimeCaptureTargetAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        int errorBytes = acknowledgement.ErrorCode?.Length ?? 0;
        byte[] payload = new byte[checked(FixedPayloadBytes + errorBytes)];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        bytes[4] = acknowledgement.Accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], acknowledgement.CommandRevision);
        acknowledgement.TargetId.Value.TryWriteBytes(bytes[16..32]);
        acknowledgement.TargetInstanceId.Value.TryWriteBytes(bytes[32..48]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], (int)acknowledgement.State);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[52..], errorBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes[56..], acknowledgement.NativeErrorCode ?? 0);
        if (errorBytes > 0)
            System.Text.Encoding.ASCII.GetBytes(acknowledgement.ErrorCode!, bytes[FixedPayloadBytes..]);
        return payload;
    }

    public static RuntimeCaptureTargetAcknowledgement Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] > 1 || payload[5] != 0 || payload[6] != 0 || payload[7] != 0)
            throw new InvalidDataException("Capture target acknowledgement header is invalid.");

        int errorBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[52..]);
        if (errorBytes is < 0 or > MaximumErrorCodeBytes ||
            payload.Length != FixedPayloadBytes + errorBytes)
            throw new InvalidDataException("Capture target acknowledgement error code length is invalid.");

        try
        {
            string? errorCode = errorBytes == 0
                ? null
                : System.Text.Encoding.ASCII.GetString(payload[FixedPayloadBytes..]);
            return new RuntimeCaptureTargetAcknowledgement(
                BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
                new CaptureTargetId(new Guid(payload[16..32])),
                new TargetInstanceId(new Guid(payload[32..48])),
                payload[4] == 1,
                (TargetLifecycleState)BinaryPrimitives.ReadInt32LittleEndian(payload[48..]),
                errorCode,
                BinaryPrimitives.ReadInt32LittleEndian(payload[56..]) is var nativeError &&
                    nativeError != 0 ? nativeError : null);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Capture target acknowledgement fields are invalid.", exception);
        }
    }
}

public static class RuntimeTargetLifecyclePayloadCodec
{
    public const int SchemaVersion = 1;
    public const int FixedPayloadBytes = 76;
    public const int MaximumErrorCodeBytes = 128;

    public static byte[] Encode(TargetLifecycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        int errorBytes = value.ErrorCode?.Length ?? 0;
        byte[] payload = new byte[checked(FixedPayloadBytes + errorBytes)];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], (int)value.Target.State);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.LifecycleSequence);
        value.Target.TargetId.Value.TryWriteBytes(bytes[16..32]);
        value.Target.TargetInstanceId.Value.TryWriteBytes(bytes[32..48]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], value.OccurredAtUtc.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], value.Target.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[60..], value.Target.PixelHeight);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[64..], value.Target.Dpi);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[68..], value.NativeErrorCode ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[72..], errorBytes);
        if (errorBytes > 0)
            System.Text.Encoding.ASCII.GetBytes(value.ErrorCode!, bytes[FixedPayloadBytes..]);
        return payload;
    }

    public static TargetLifecycleEvent Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion)
            throw new InvalidDataException("Target lifecycle payload header is invalid.");
        int errorBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[72..]);
        if (errorBytes is < 0 or > MaximumErrorCodeBytes ||
            payload.Length != FixedPayloadBytes + errorBytes)
            throw new InvalidDataException("Target lifecycle error code length is invalid.");

        try
        {
            int nativeError = BinaryPrimitives.ReadInt32LittleEndian(payload[68..]);
            var result = new TargetLifecycleEvent(
                new TargetSnapshot(
                    new TargetInstanceId(new Guid(payload[32..48])),
                    new CaptureTargetId(new Guid(payload[16..32])),
                    (TargetLifecycleState)BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[56..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[60..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[64..])),
                BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
                new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(payload[48..]), TimeSpan.Zero),
                errorBytes == 0
                    ? null
                    : System.Text.Encoding.ASCII.GetString(payload[FixedPayloadBytes..]))
            {
                NativeErrorCode = nativeError == 0 ? null : nativeError,
            };
            Validate(result);
            return result;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Target lifecycle payload fields are invalid.", exception);
        }
    }

    private static void Validate(TargetLifecycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value.Target);
        ArgumentOutOfRangeException.ThrowIfLessThan(value.LifecycleSequence, 1);
        if (!Enum.IsDefined(value.Target.State) || value.OccurredAtUtc.Offset != TimeSpan.Zero ||
            value.ErrorCode is { } errorCode &&
            (errorCode.Length is 0 or > MaximumErrorCodeBytes ||
                errorCode.Any(character => character is not (>= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))))
            throw new ArgumentException("Target lifecycle fields are invalid.", nameof(value));
    }
}

public enum RuntimeOverlayApplyStatus : byte
{
    Applied = 1,
    Superseded = 2,
    InvalidState = 3,
    CaptureExclusionFailed = 4,
    DeviceFailed = 5,
    Stopped = 6,
    BackgroundUnavailable = 7,
}

public sealed record RuntimeOverlayAcknowledgement
{
    public RuntimeOverlayAcknowledgement(
        TargetInstanceId targetInstanceId,
        long overlayRevision,
        bool accepted,
        RuntimeOverlayApplyStatus status,
        string? errorCode,
        int? nativeErrorCode = null)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(overlayRevision, 1);
        if (!Enum.IsDefined(status) || accepted != (status == RuntimeOverlayApplyStatus.Applied) ||
            accepted != string.IsNullOrEmpty(errorCode) ||
            accepted && nativeErrorCode is not null ||
            errorCode is not null && !RuntimeBinaryPayloadPolicy.IsStableCode(errorCode))
            throw new ArgumentException("Overlay acknowledgement state is invalid.");
        TargetInstanceId = targetInstanceId;
        OverlayRevision = overlayRevision;
        Accepted = accepted;
        Status = status;
        ErrorCode = errorCode;
        NativeErrorCode = nativeErrorCode;
    }

    public TargetInstanceId TargetInstanceId { get; }
    public long OverlayRevision { get; }
    public bool Accepted { get; }
    public RuntimeOverlayApplyStatus Status { get; }
    public string? ErrorCode { get; }
    public int? NativeErrorCode { get; }
}

public static class RuntimeOverlayDesiredStatePayloadCodec
{
    public const int SchemaVersion = 4;
    public const int FixedPayloadBytes = 48;
    public const int FixedRegionBytes = 136;
    public const int FixedSlotBytes = 40;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(OverlayDesiredState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        int payloadBytes = FixedPayloadBytes;
        int totalCharacters = 0;
        foreach (OverlayRegionSnapshot region in state.Regions)
        {
            payloadBytes = checked(payloadBytes + FixedRegionBytes);
            foreach (OverlaySlotSnapshot slot in region.OrderedSlots)
            {
                if (slot.Text is null || slot.Label is null || !Enum.IsDefined(slot.State))
                    throw new ArgumentException("Overlay slot state is invalid.", nameof(state));
                totalCharacters = checked(totalCharacters + slot.Text.Length + slot.Label.Length);
                payloadBytes = checked(payloadBytes + FixedSlotBytes +
                    StrictUtf8.GetByteCount(slot.Label) + StrictUtf8.GetByteCount(slot.Text));
            }
        }
        if (totalCharacters > RuntimeCapabilities.VersionOne.MaxOverlayCharsPerTarget ||
            payloadBytes > RuntimeProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(state), "Overlay snapshot exceeds runtime capacity.");

        byte[] payload = new byte[payloadBytes];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], state.Regions.Count);
        state.RuntimeEpoch.TryWriteBytes(bytes[8..24]);
        state.TargetInstanceId.Value.TryWriteBytes(bytes[24..40]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[40..48], state.OverlayRevision);
        int offset = FixedPayloadBytes;
        foreach (OverlayRegionSnapshot region in state.Regions)
        {
            region.RegionId.TryWriteBytes(bytes[offset..(offset + 16)]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 16)..], region.Bounds.X);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 20)..], region.Bounds.Y);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 24)..], region.Bounds.Width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 28)..], region.Bounds.Height);
            bytes[offset + 32] = (byte)region.Style.Background;
            bytes[offset + 33] = (byte)region.Style.Alignment;
            WriteArgb(bytes[(offset + 36)..], region.Style.BackgroundColor);
            WriteArgb(bytes[(offset + 40)..], region.Style.TextColor);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 44)..], region.Style.Opacity);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 52)..], region.Style.BlurRadius);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 60)..], region.Style.Padding);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 68)..], region.Style.PreferredFontSize);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 76)..], region.Style.MinimumFontSize);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 84)..], region.Style.MaximumLines);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 88)..], region.OrderedSlots.Count);
            OverlayPixelRect? destination = region.Style.DestinationBounds;
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 92)..], destination?.X ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 96)..], destination?.Y ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 100)..], destination?.Width ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 104)..], destination?.Height ?? 0);
            WriteArgb(bytes[(offset + 108)..], region.Style.OutlineColor);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 112)..], region.Style.OutlineWidth);
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes[(offset + 120)..], region.Style.MinimumDwellMilliseconds);
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes[(offset + 124)..], region.Style.CrossfadeMilliseconds);
            bytes[offset + 128] = region.Style.ReducedMotion ? (byte)1 : (byte)0;
            bytes[offset + 129] = region.Style.AutomaticShrink ? (byte)1 : (byte)0;
            bytes[offset + 130] = region.Style.NoScrollOverflow ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 132)..], region.Style.MaximumHeight);
            offset += FixedRegionBytes;
            foreach (OverlaySlotSnapshot slot in region.OrderedSlots)
            {
                int labelBytes = StrictUtf8.GetByteCount(slot.Label);
                int textBytes = StrictUtf8.GetByteCount(slot.Text);
                slot.SlotId.TryWriteBytes(bytes[offset..(offset + 16)]);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 16)..], slot.Order);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 20)..], (int)slot.State);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 24)..], labelBytes);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 28)..], textBytes);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 32)..], slot.StageIndex);
                offset += FixedSlotBytes;
                offset += StrictUtf8.GetBytes(slot.Label, bytes[offset..]);
                offset += StrictUtf8.GetBytes(slot.Text, bytes[offset..]);
            }
        }
        return payload;
    }

    public static OverlayDesiredState Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]) is var regionCountValue &&
                (regionCountValue < 0 || regionCountValue > RuntimeCapabilities.VersionOne.MaxRegionsPerTarget))
            throw new InvalidDataException("Overlay payload header is invalid.");
        int regionCount = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        int offset = FixedPayloadBytes;
        int totalCharacters = 0;
        var regions = new List<OverlayRegionSnapshot>(regionCount);
        try
        {
            for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
            {
                Require(payload, offset, FixedRegionBytes);
                Guid regionId = new(payload.Slice(offset, 16));
                var bounds = new OverlayPixelRect(
                    BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 16)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 20)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 24)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 28)..]));
                int destinationX = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 92)..]);
                int destinationY = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 96)..]);
                int destinationWidth = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 100)..]);
                int destinationHeight = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 104)..]);
                bool destinationEmpty = destinationX == 0 && destinationY == 0 &&
                    destinationWidth == 0 && destinationHeight == 0;
                bool destinationValid = destinationWidth is >= 1 and <= 16_384 &&
                    destinationHeight is >= 1 and <= 16_384;
                if (!destinationEmpty && !destinationValid)
                    throw new InvalidDataException("Overlay destination rectangle is invalid.");
                var style = new OverlayRegionStyleSnapshot(
                    (OverlayBackgroundTreatment)payload[offset + 32],
                    (OverlayTextAlignment)payload[offset + 33],
                    ReadArgb(payload[(offset + 36)..]),
                    ReadArgb(payload[(offset + 40)..]),
                    BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 44)..]),
                    BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 52)..]),
                    BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 60)..]),
                    BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 68)..]),
                    BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 76)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 84)..]))
                {
                    DestinationBounds = destinationValid
                        ? new OverlayPixelRect(
                            destinationX,
                            destinationY,
                            destinationWidth,
                            destinationHeight)
                        : null,
                    OutlineColor = ReadArgb(payload[(offset + 108)..]),
                    OutlineWidth = BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 112)..]),
                    MinimumDwellMilliseconds = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 120)..]),
                    CrossfadeMilliseconds = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 124)..]),
                    ReducedMotion = payload[offset + 128] == 1,
                    AutomaticShrink = payload[offset + 129] == 1,
                    NoScrollOverflow = payload[offset + 130] == 1,
                    MaximumHeight = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 132)..]),
                };
                int slotCount = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 88)..]);
                if (payload[offset + 34] != 0 || payload[offset + 35] != 0 ||
                    payload[offset + 128] > 1 || payload[offset + 129] > 1 ||
                    payload[offset + 130] > 1 || payload[offset + 131] != 0 ||
                    slotCount is < 0 or > 4)
                    throw new InvalidDataException("Overlay region header is invalid.");
                offset += FixedRegionBytes;
                var slots = new List<OverlaySlotSnapshot>(slotCount);
                for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
                {
                    Require(payload, offset, FixedSlotBytes);
                    int labelBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 24)..]);
                    int textBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 28)..]);
                    if (labelBytes < 0 || textBytes < 0 ||
                        labelBytes > RuntimeProtocol.MaxPayloadBytes - textBytes)
                        throw new InvalidDataException("Overlay slot text lengths are invalid.");
                    int variableBytes = checked(labelBytes + textBytes);
                    Require(payload, offset + FixedSlotBytes, variableBytes);
                    string label = StrictUtf8.GetString(payload.Slice(offset + FixedSlotBytes, labelBytes));
                    string text = StrictUtf8.GetString(payload.Slice(offset + FixedSlotBytes + labelBytes, textBytes));
                    totalCharacters = checked(totalCharacters + label.Length + text.Length);
                    if (!payload.Slice(offset + 36, 4).SequenceEqual(new byte[4]))
                        throw new InvalidDataException("Overlay slot reserved bytes are invalid.");
                    slots.Add(new OverlaySlotSnapshot(
                        new Guid(payload.Slice(offset, 16)),
                        BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 16)..]),
                        (OverlaySlotState)BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 20)..]),
                        text,
                        label)
                    {
                        StageIndex = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 32)..]),
                    });
                    offset = checked(offset + FixedSlotBytes + variableBytes);
                }
                regions.Add(new OverlayRegionSnapshot(regionId, bounds, style, slots));
            }
            if (offset != payload.Length || totalCharacters > RuntimeCapabilities.VersionOne.MaxOverlayCharsPerTarget)
                throw new InvalidDataException("Overlay payload has trailing data or exceeds capacity.");
            return new OverlayDesiredState(
                new Guid(payload[8..24]),
                new TargetInstanceId(new Guid(payload[24..40])),
                BinaryPrimitives.ReadInt64LittleEndian(payload[40..48]),
                regions);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DecoderFallbackException)
        {
            throw new InvalidDataException("Overlay payload fields are invalid.", exception);
        }
    }

    private static void Require(ReadOnlySpan<byte> payload, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > payload.Length - count)
            throw new InvalidDataException("Overlay payload is truncated.");
    }

    private static void WriteArgb(Span<byte> destination, string value)
    {
        if (value is not { Length: 9 } || value[0] != '#')
            throw new ArgumentException("Overlay colors must use #AARRGGBB.", nameof(value));
        destination[0] = byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        destination[1] = byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        destination[2] = byte.Parse(value.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        destination[3] = byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string ReadArgb(ReadOnlySpan<byte> source) =>
        $"#{source[3]:X2}{source[0]:X2}{source[1]:X2}{source[2]:X2}";
}

public static class RuntimeOverlayAcknowledgementPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int FixedPayloadBytes = 48;

    public static byte[] Encode(RuntimeOverlayAcknowledgement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] error = value.ErrorCode is null ? [] : Encoding.ASCII.GetBytes(value.ErrorCode);
        byte[] payload = new byte[FixedPayloadBytes + error.Length];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        bytes[4] = value.Accepted ? (byte)1 : (byte)0;
        bytes[5] = (byte)value.Status;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.OverlayRevision);
        value.TargetInstanceId.Value.TryWriteBytes(bytes[16..32]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], error.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[36..], value.NativeErrorCode ?? 0);
        error.CopyTo(bytes[FixedPayloadBytes..]);
        return payload;
    }

    public static RuntimeOverlayAcknowledgement Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] > 1 || payload[6] != 0 || payload[7] != 0 ||
            payload[40..48].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Overlay acknowledgement header is invalid.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(payload[32..]);
        if (length is < 0 or > RuntimeBinaryPayloadPolicy.MaximumStableCodeBytes ||
            payload.Length != FixedPayloadBytes + length)
            throw new InvalidDataException("Overlay acknowledgement length is invalid.");
        try
        {
            int native = BinaryPrimitives.ReadInt32LittleEndian(payload[36..]);
            return new RuntimeOverlayAcknowledgement(
                new TargetInstanceId(new Guid(payload[16..32])),
                BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
                payload[4] == 1,
                (RuntimeOverlayApplyStatus)payload[5],
                length == 0 ? null : Encoding.ASCII.GetString(payload[FixedPayloadBytes..]),
                native == 0 ? null : native);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Overlay acknowledgement fields are invalid.", exception);
        }
    }
}

public static class RuntimePolicyRevisionPayloadCodec
{
    public const int SchemaVersion = 2;
    public const int FixedPayloadBytes = 48;
    public const int FixedEntryBytes = 24;
    public const int MaximumPolicyBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(PolicyRevision value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(value.Revision, 1);
        if (value.ProfileId == Guid.Empty)
            throw new ArgumentException("Profile identity cannot be empty.", nameof(value));
        ArgumentOutOfRangeException.ThrowIfLessThan(value.ProfileRevision, 1);
        if (value.RegionPolicies.Count > RuntimeCapabilities.VersionOne.MaxRegionsPerTarget)
            throw new ArgumentOutOfRangeException(nameof(value));
        var entries = value.RegionPolicies.OrderBy(item => item.Key.Value)
            .Select(item => (item.Key, Bytes: StrictUtf8.GetBytes(item.Value ?? string.Empty))).ToArray();
        if (entries.Any(item => item.Bytes.Length is < 1 or > MaximumPolicyBytes ||
            item.Bytes.Any(character => character is < 0x20 or > 0x7e)))
            throw new ArgumentException("Region policy payload is invalid.", nameof(value));
        int length = entries.Aggregate(FixedPayloadBytes, (total, item) =>
            checked(total + FixedEntryBytes + item.Bytes.Length));
        byte[] payload = new byte[length];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], entries.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.Revision);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], value.ProfileRevision);
        value.ProfileId.TryWriteBytes(bytes[24..40]);
        int offset = FixedPayloadBytes;
        foreach ((RegionId id, byte[] policy) in entries)
        {
            id.Value.TryWriteBytes(bytes[offset..(offset + 16)]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 16)..], policy.Length);
            offset += FixedEntryBytes;
            policy.CopyTo(bytes[offset..]);
            offset += policy.Length;
        }
        return payload;
    }

    public static PolicyRevision Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]) is var entryCountValue &&
                (entryCountValue < 0 || entryCountValue > RuntimeCapabilities.VersionOne.MaxRegionsPerTarget) ||
            payload[40..48].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Policy revision header is invalid.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        long revision = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        long profileRevision = BinaryPrimitives.ReadInt64LittleEndian(payload[16..]);
        Guid profileId = new(payload[24..40]);
        if (revision < 1 || profileRevision < 1 || profileId == Guid.Empty)
            throw new InvalidDataException("Policy revision identifiers are invalid.");
        int offset = FixedPayloadBytes;
        var policies = new Dictionary<RegionId, string>(count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                if (offset > payload.Length - FixedEntryBytes)
                    throw new InvalidDataException("Policy revision payload is truncated.");
                var id = new RegionId(new Guid(payload.Slice(offset, 16)));
                int byteLength = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 16)..]);
                if (payload[(offset + 20)..(offset + 24)].IndexOfAnyExcept((byte)0) >= 0 ||
                    byteLength is < 1 or > MaximumPolicyBytes ||
                    offset + FixedEntryBytes > payload.Length - byteLength)
                    throw new InvalidDataException("Policy revision entry is invalid.");
                string policy = StrictUtf8.GetString(payload.Slice(offset + FixedEntryBytes, byteLength));
                if (!policies.TryAdd(id, policy))
                    throw new InvalidDataException("Policy revision contains duplicate regions.");
                offset = checked(offset + FixedEntryBytes + byteLength);
            }
            if (offset != payload.Length) throw new InvalidDataException("Policy revision has trailing data.");
            return new PolicyRevision(
                revision,
                profileId,
                profileRevision,
                policies);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DecoderFallbackException)
        {
            throw new InvalidDataException("Policy revision fields are invalid.", exception);
        }
    }
}

public static class RuntimePolicyAcknowledgementPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int FixedPayloadBytes = 24;

    public static byte[] Encode(PolicyAcknowledgement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RuntimeBinaryPayloadPolicy.ValidatePolicyAcknowledgement(value);
        byte[] code = value.RejectionCode is null ? [] : Encoding.ASCII.GetBytes(value.RejectionCode);
        byte[] payload = new byte[FixedPayloadBytes + code.Length];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        bytes[4] = value.Accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], code.Length);
        code.CopyTo(bytes[FixedPayloadBytes..]);
        return payload;
    }

    public static PolicyAcknowledgement Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] > 1 || payload[5..8].IndexOfAnyExcept((byte)0) >= 0 ||
            payload[20..24].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Policy acknowledgement header is invalid.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]);
        if (length is < 0 or > RuntimeBinaryPayloadPolicy.MaximumStableCodeBytes ||
            payload.Length != FixedPayloadBytes + length)
            throw new InvalidDataException("Policy acknowledgement length is invalid.");
        var result = new PolicyAcknowledgement(
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
            payload[4] == 1,
            length == 0 ? null : Encoding.ASCII.GetString(payload[FixedPayloadBytes..]));
        try { RuntimeBinaryPayloadPolicy.ValidatePolicyAcknowledgement(result); }
        catch (ArgumentException exception) { throw new InvalidDataException("Policy acknowledgement fields are invalid.", exception); }
        return result;
    }
}

internal static class RuntimeBinaryPayloadPolicy
{
    internal const int MaximumStableCodeBytes = 128;

    internal static bool IsStableCode(string value) => value.Length is > 0 and <= MaximumStableCodeBytes &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');

    internal static void ValidatePolicyAcknowledgement(PolicyAcknowledgement value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value.Revision, 1);
        if (value.Accepted != string.IsNullOrEmpty(value.RejectionCode) ||
            value.RejectionCode is not null && !IsStableCode(value.RejectionCode))
            throw new ArgumentException("Policy acknowledgement state is invalid.", nameof(value));
    }
}

public sealed record RuntimeFrameDescriptor(
    RuntimeMessageKind MessageKind,
    Guid RequestId,
    Guid RuntimeEpoch,
    DateTimeOffset DeadlineUtc,
    byte[] Payload);
