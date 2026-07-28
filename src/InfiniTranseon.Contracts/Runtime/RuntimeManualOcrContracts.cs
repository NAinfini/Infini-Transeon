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

/// <summary>The target selection carried by a manual OCR request.</summary>
public enum RuntimeManualOcrScope : byte
{
    AllTargets = 1,
    ExplicitTargets = 2,
}

/// <summary>
/// Selects either every runtime target or a non-empty bounded set of target instances.
/// An explicit empty set is intentionally invalid and never means all targets.
/// </summary>
public sealed record RuntimeManualOcrRequest
{
    private readonly TargetInstanceId[] _targetInstanceIds;

    private RuntimeManualOcrRequest(
        RuntimeManualOcrScope scope,
        TargetInstanceId[] targetInstanceIds)
    {
        Scope = scope;
        _targetInstanceIds = targetInstanceIds;
    }

    public const int MaximumExplicitTargets = 8;

    public static RuntimeManualOcrRequest AllTargets { get; } = new(
        RuntimeManualOcrScope.AllTargets,
        []);

    public RuntimeManualOcrScope Scope { get; }
    public IReadOnlyList<TargetInstanceId> TargetInstanceIds => _targetInstanceIds;

    public static RuntimeManualOcrRequest Explicit(
        IEnumerable<TargetInstanceId> targetInstanceIds)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceIds);
        TargetInstanceId[] targets = targetInstanceIds.ToArray();
        if (targets.Length == 0)
            throw new ArgumentException(
                "An explicit manual OCR request requires at least one target.",
                nameof(targetInstanceIds));
        if (targets.Length > MaximumExplicitTargets)
            throw new ArgumentOutOfRangeException(
                nameof(targetInstanceIds),
                $"At most {MaximumExplicitTargets} explicit OCR targets are allowed.");

        var identities = new HashSet<Guid>();
        foreach (TargetInstanceId target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (!identities.Add(target.Value))
                throw new ArgumentException(
                    "An explicit manual OCR request cannot contain duplicate targets.",
                    nameof(targetInstanceIds));
        }
        return new RuntimeManualOcrRequest(RuntimeManualOcrScope.ExplicitTargets, targets);
    }
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
    // Schema v2 introduces an explicit scope byte and target count; it does not reuse
    // the v1 reserved bytes, so v1 cannot reinterpret an explicit selection as all.
    public const int SchemaVersion = 2;
    public const byte Operation = 1;
    public const int RequestHeaderBytes = 8;
    public const int RequestPayloadBytes = RequestHeaderBytes;
    public const int TargetInstanceIdBytes = 16;
    public const int MaximumRequestPayloadBytes = RequestHeaderBytes +
        RuntimeManualOcrRequest.MaximumExplicitTargets * TargetInstanceIdBytes;
    public const int AcknowledgementFixedBytes = 20;
    public const int MaximumErrorCodeBytes = 128;

    public static byte[] EncodeRequest() => EncodeRequest(RuntimeManualOcrRequest.AllTargets);

    public static byte[] EncodeRequest(RuntimeManualOcrRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int targetCount = request.Scope == RuntimeManualOcrScope.ExplicitTargets
            ? request.TargetInstanceIds.Count
            : 0;
        if (request.Scope is not RuntimeManualOcrScope.AllTargets and
            not RuntimeManualOcrScope.ExplicitTargets ||
            (request.Scope == RuntimeManualOcrScope.AllTargets && targetCount != 0) ||
            (request.Scope == RuntimeManualOcrScope.ExplicitTargets &&
                (targetCount is < 1 or > RuntimeManualOcrRequest.MaximumExplicitTargets)))
            throw new ArgumentException("Manual OCR request scope is invalid.", nameof(request));

        byte[] payload = new byte[checked(RequestHeaderBytes +
            targetCount * TargetInstanceIdBytes)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, SchemaVersion);
        payload[4] = Operation;
        payload[5] = (byte)request.Scope;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)targetCount);
        for (int index = 0; index < targetCount; index++)
            request.TargetInstanceIds[index].Value.TryWriteBytes(
                payload.AsSpan(RequestHeaderBytes + index * TargetInstanceIdBytes,
                    TargetInstanceIdBytes));
        return payload;
    }

    public static void ValidateRequest(ReadOnlySpan<byte> payload) =>
        _ = DecodeRequest(payload);

    public static RuntimeManualOcrRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < RequestHeaderBytes or > MaximumRequestPayloadBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] != Operation)
            throw new InvalidDataException("Manual OCR request payload is invalid.");

        RuntimeManualOcrScope scope = (RuntimeManualOcrScope)payload[5];
        int targetCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        if (payload.Length != RequestHeaderBytes + targetCount * TargetInstanceIdBytes ||
            scope is not RuntimeManualOcrScope.AllTargets and
                not RuntimeManualOcrScope.ExplicitTargets ||
            (scope == RuntimeManualOcrScope.AllTargets && targetCount != 0) ||
            (scope == RuntimeManualOcrScope.ExplicitTargets &&
                (targetCount is < 1 or > RuntimeManualOcrRequest.MaximumExplicitTargets)))
            throw new InvalidDataException("Manual OCR request scope is invalid.");

        if (scope == RuntimeManualOcrScope.AllTargets)
            return RuntimeManualOcrRequest.AllTargets;

        try
        {
            TargetInstanceId[] targets = new TargetInstanceId[targetCount];
            for (int index = 0; index < targetCount; index++)
            {
                Guid identity = new(payload.Slice(
                    RequestHeaderBytes + index * TargetInstanceIdBytes,
                    TargetInstanceIdBytes));
                targets[index] = new TargetInstanceId(identity);
            }
            return RuntimeManualOcrRequest.Explicit(targets);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Manual OCR target selection is invalid.", exception);
        }
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
