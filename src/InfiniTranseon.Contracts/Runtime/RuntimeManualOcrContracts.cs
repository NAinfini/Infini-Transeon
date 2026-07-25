using System.Buffers.Binary;
using System.Text;

namespace InfiniTranseon.Contracts.Runtime;

/// <summary>The terminal scheduling result for an explicit manual OCR request.</summary>
public enum RuntimeManualOcrStatus : byte
{
    Scheduled = 1,
    NoTargets = 2,
    NoRegions = 3,
    Busy = 4,
    RuntimeFailure = 5,
    TargetUnavailable = 6,
}

/// <summary>
/// Acknowledges that one OCR pass was scheduled for every eligible running target.
/// Recognition results continue over the normal <see cref="RuntimeMessageKind.OcrResult"/>
/// event path.
/// </summary>
public sealed record RuntimeManualOcrAcknowledgement
{
    public RuntimeManualOcrAcknowledgement(
        bool accepted,
        RuntimeManualOcrStatus status,
        int targetCount,
        int regionCount,
        string? errorCode)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentOutOfRangeException.ThrowIfNegative(targetCount);
        ArgumentOutOfRangeException.ThrowIfNegative(regionCount);
        bool validSuccess = accepted &&
            status == RuntimeManualOcrStatus.Scheduled &&
            targetCount > 0 &&
            regionCount > 0 &&
            errorCode is null;
        bool validFailure = !accepted &&
            status != RuntimeManualOcrStatus.Scheduled &&
            targetCount == 0 &&
            regionCount == 0 &&
            IsStableErrorCode(errorCode);
        if (!validSuccess && !validFailure)
            throw new ArgumentException(
                "Manual OCR acknowledgement fields are inconsistent.");

        Accepted = accepted;
        Status = status;
        TargetCount = targetCount;
        RegionCount = regionCount;
        ErrorCode = errorCode;
    }

    public bool Accepted { get; }
    public RuntimeManualOcrStatus Status { get; }
    public int TargetCount { get; }
    public int RegionCount { get; }
    public string? ErrorCode { get; }

    private static bool IsStableErrorCode(string? value) =>
        value is { Length: > 0 and <= RuntimeManualOcrPayloadCodec.MaximumErrorCodeBytes } &&
        value.All(character => character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');
}

/// <summary>
/// Versioned payload carried by ControlRequest/ControlResponse. Empty control payloads
/// remain heartbeats; operation 1 requests one immediate OCR pass for every eligible
/// region on running targets that have produced a frame.
/// </summary>
public static class RuntimeManualOcrPayloadCodec
{
    public const int SchemaVersion = 1;
    public const byte Operation = 1;
    public const int RequestPayloadBytes = 8;
    public const int AcknowledgementFixedBytes = 20;
    public const int MaximumErrorCodeBytes = 128;

    public static byte[] EncodeRequest()
    {
        byte[] payload = new byte[RequestPayloadBytes];
        BinaryPrimitives.WriteInt32LittleEndian(payload, SchemaVersion);
        payload[4] = Operation;
        return payload;
    }

    public static void ValidateRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != RequestPayloadBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] != Operation ||
            payload[5] != 0 ||
            payload[6] != 0 ||
            payload[7] != 0)
            throw new InvalidDataException("Manual OCR request payload is invalid.");
    }

    public static byte[] EncodeAcknowledgement(RuntimeManualOcrAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        int errorBytes = acknowledgement.ErrorCode?.Length ?? 0;
        byte[] payload = new byte[checked(AcknowledgementFixedBytes + errorBytes)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, SchemaVersion);
        payload[4] = acknowledgement.Accepted ? (byte)1 : (byte)0;
        payload[5] = (byte)acknowledgement.Status;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), acknowledgement.TargetCount);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), acknowledgement.RegionCount);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), errorBytes);
        if (errorBytes > 0)
            Encoding.ASCII.GetBytes(
                acknowledgement.ErrorCode!, payload.AsSpan(AcknowledgementFixedBytes));
        return payload;
    }

    public static RuntimeManualOcrAcknowledgement DecodeAcknowledgement(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < AcknowledgementFixedBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] > 1 ||
            payload[6] != 0 ||
            payload[7] != 0)
            throw new InvalidDataException("Manual OCR acknowledgement header is invalid.");

        int errorBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]);
        if (errorBytes is < 0 or > MaximumErrorCodeBytes ||
            payload.Length != AcknowledgementFixedBytes + errorBytes)
            throw new InvalidDataException(
                "Manual OCR acknowledgement error length is invalid.");

        try
        {
            return new RuntimeManualOcrAcknowledgement(
                payload[4] == 1,
                (RuntimeManualOcrStatus)payload[5],
                BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[12..]),
                errorBytes == 0
                    ? null
                    : Encoding.ASCII.GetString(payload[AcknowledgementFixedBytes..]));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Manual OCR acknowledgement fields are invalid.", exception);
        }
    }
}
