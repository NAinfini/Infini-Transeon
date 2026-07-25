using System.Buffers.Binary;
using System.Text;

namespace InfiniTranseon.Contracts.Runtime;

internal static class RuntimeOcrExecutionTokenCodec
{
    internal const int PayloadBytes = 112;

    internal static void Write(Span<byte> destination, OcrExecutionToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (destination.Length < PayloadBytes) throw new ArgumentException("OCR token buffer is too small.");
        SourceGenerationToken source = token.Source;
        source.RuntimeEpoch.TryWriteBytes(destination[0..16]);
        source.TargetInstanceId.Value.TryWriteBytes(destination[16..32]);
        destination[32] = (byte)source.Area.Kind;
        destination[33] = token.IsManual ? (byte)1 : (byte)0;
        source.Area.UserRegionId?.Value.TryWriteBytes(destination[36..52]);
        source.TextTrackId.Value.TryWriteBytes(destination[52..68]);
        BinaryPrimitives.WriteInt64LittleEndian(destination[68..], source.SourceGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(destination[76..], source.ProfileRevision);
        token.OcrRunId.TryWriteBytes(destination[84..100]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[100..], token.Attempt);
        BinaryPrimitives.WriteInt64LittleEndian(destination[104..], token.ResultSequence);
    }

    internal static OcrExecutionToken Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < PayloadBytes ||
            source[33] > 1 ||
            source[34..36].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("OCR execution token is truncated or malformed.");
        try
        {
            var areaKind = (CaptureAreaKind)source[32];
            if (!Enum.IsDefined(areaKind)) throw new ArgumentOutOfRangeException(nameof(areaKind));
            Guid regionIdentity = new(source[36..52]);
            CaptureAreaKey area = areaKind == CaptureAreaKind.UserRegion
                ? CaptureAreaKey.UserRegion(new RegionId(regionIdentity))
                : new CaptureAreaKey(areaKind, null);
            if (areaKind != CaptureAreaKind.UserRegion && regionIdentity != Guid.Empty)
                throw new ArgumentException("Non-region OCR token contains a region identity.");
            return new OcrExecutionToken(
                new SourceGenerationToken(
                    new Guid(source[0..16]),
                    new TargetInstanceId(new Guid(source[16..32])),
                    area,
                    new TextTrackId(new Guid(source[52..68])),
                    BinaryPrimitives.ReadInt64LittleEndian(source[68..]),
                    BinaryPrimitives.ReadInt64LittleEndian(source[76..])),
                new Guid(source[84..100]),
                BinaryPrimitives.ReadInt32LittleEndian(source[100..]),
                BinaryPrimitives.ReadInt64LittleEndian(source[104..]),
                source[33] == 1);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("OCR execution token fields are invalid.", exception);
        }
    }
}

public static class RuntimeCloudOcrCropRequestPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int FixedPayloadBytes = 160;
    public const int MaximumMimeTypeBytes = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(CloudOcrCropRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] mime = StrictUtf8.GetBytes(value.MimeType);
        byte[] provider = StrictUtf8.GetBytes(value.ProviderId);
        ReadOnlySpan<byte> crop = value.EncodedCrop.Span;
        if (!IsMimeType(mime) || !IsProviderId(provider) || crop.Length > value.EncodedByteCeiling ||
            crop.Length > RuntimeProtocol.MaxPayloadBytes - FixedPayloadBytes - mime.Length - provider.Length)
            throw new ArgumentException("Cloud OCR crop payload is invalid.", nameof(value));
        byte[] payload = new byte[checked(FixedPayloadBytes + mime.Length + provider.Length + crop.Length)];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        RuntimeOcrExecutionTokenCodec.Write(bytes[4..116], value.ExecutionToken);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[116..], mime.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[120..], value.ConsentPolicyRevision);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[128..], value.DeadlineUtc.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[136..], value.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[140..], value.PixelHeight);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[144..], value.EncodedByteCeiling);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[148..], crop.Length);
        bytes[152] = value.ExplicitCloudConsent ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes[156..], provider.Length);
        mime.CopyTo(bytes[FixedPayloadBytes..]);
        provider.CopyTo(bytes[(FixedPayloadBytes + mime.Length)..]);
        crop.CopyTo(bytes[(FixedPayloadBytes + mime.Length + provider.Length)..]);
        return payload;
    }

    public static CloudOcrCropRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[152] != 1 || payload[153..156].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Cloud OCR crop payload header is invalid.");
        int mimeBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[116..]);
        int cropBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[148..]);
        int byteCeiling = BinaryPrimitives.ReadInt32LittleEndian(payload[144..]);
        int providerBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[156..]);
        if (mimeBytes is < 1 or > MaximumMimeTypeBytes || cropBytes < 1 ||
            providerBytes is < 1 or > 128 ||
            byteCeiling is < 1 or > RuntimeProtocol.MaxPayloadBytes || cropBytes > byteCeiling ||
            payload.Length != FixedPayloadBytes + mimeBytes + providerBytes + cropBytes)
            throw new InvalidDataException("Cloud OCR crop payload lengths are invalid.");
        ReadOnlySpan<byte> mime = payload.Slice(FixedPayloadBytes, mimeBytes);
        ReadOnlySpan<byte> provider = payload.Slice(FixedPayloadBytes + mimeBytes, providerBytes);
        if (!IsMimeType(mime) || !IsProviderId(provider))
            throw new InvalidDataException("Cloud OCR MIME type or provider identifier is invalid.");
        try
        {
            return new CloudOcrCropRequest(
                RuntimeOcrExecutionTokenCodec.Read(payload[4..116]),
                StrictUtf8.GetString(mime),
                payload.Slice(FixedPayloadBytes + mimeBytes + providerBytes, cropBytes),
                BinaryPrimitives.ReadInt32LittleEndian(payload[136..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[140..]),
                explicitCloudConsent: true,
                BinaryPrimitives.ReadInt64LittleEndian(payload[120..]),
                byteCeiling,
                new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(payload[128..]), TimeSpan.Zero),
                StrictUtf8.GetString(provider));
        }
        catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException)
        {
            throw new InvalidDataException("Cloud OCR crop payload fields are invalid.", exception);
        }
    }

    private static bool IsMimeType(ReadOnlySpan<byte> value) =>
        value.Length is > 0 and <= MaximumMimeTypeBytes &&
        value.StartsWith("image/"u8) &&
        value[6..].IndexOfAnyExceptInRange((byte)'a', (byte)'z') < 0;

    private static bool IsProviderId(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 1 or > 128) return false;
        foreach (byte character in value)
        {
            if (character is not (>= (byte)'a' and <= (byte)'z' or
                >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or
                (byte)'.' or (byte)'_' or (byte)'-')) return false;
        }
        return true;
    }
}

public static class RuntimeOcrResultPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int FixedPayloadBytes = 144;
    public const int FixedLineBytes = 48;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(OcrResultSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Lines);
        byte[] modelId = EncodeMetadata(value.ModelId, 128, nameof(value.ModelId));
        byte[] modelVersion = EncodeMetadata(value.ModelVersion, 128, nameof(value.ModelVersion));
        byte[] error = value.TerminalErrorCode is null ? [] : Encoding.ASCII.GetBytes(value.TerminalErrorCode);
        if (value.Lines.Count > RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult ||
            error.Length > 128 || error.Length > 0 && !IsStableCode(value.TerminalErrorCode!))
            throw new ArgumentException("OCR result exceeds runtime capacity or contains invalid metadata.", nameof(value));
        var lines = value.Lines.Select(line => (Line: line, Text: StrictUtf8.GetBytes(line.Text))).ToArray();
        int length = checked(FixedPayloadBytes + modelId.Length + modelVersion.Length + error.Length);
        foreach (var item in lines)
        {
            if (item.Text.Length > RuntimeProtocol.MaxPayloadBytes || !double.IsFinite(item.Line.Confidence) ||
                item.Line.Confidence is < 0 or > 1 ||
                item.Line.OrientationDegrees is < -180 or > 180)
                throw new ArgumentException("OCR line is invalid.", nameof(value));
            length = checked(length + FixedLineBytes + item.Text.Length);
        }
        if (length > RuntimeProtocol.MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(value));

        byte[] payload = new byte[length];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], lines.Length);
        RuntimeOcrExecutionTokenCodec.Write(bytes[8..120], value.ExecutionToken);
        bytes[120] = value.IsStable ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes[128..], modelId.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[132..], modelVersion.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[136..], error.Length);
        int offset = FixedPayloadBytes;
        modelId.CopyTo(bytes[offset..]);
        offset += modelId.Length;
        modelVersion.CopyTo(bytes[offset..]);
        offset += modelVersion.Length;
        error.CopyTo(bytes[offset..]);
        offset += error.Length;
        foreach (var item in lines)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], item.Text.Length);
            BinaryPrimitives.WriteInt16LittleEndian(bytes[(offset + 4)..],
                checked((short)item.Line.OrientationDegrees));
            bytes[offset + 6] = item.Line.IsVertical ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 8)..], item.Line.Bounds.X);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 16)..], item.Line.Bounds.Y);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 24)..], item.Line.Bounds.Width);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 32)..], item.Line.Bounds.Height);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 40)..], item.Line.Confidence);
            offset += FixedLineBytes;
            item.Text.CopyTo(bytes[offset..]);
            offset += item.Text.Length;
        }
        return payload;
    }

    public static OcrResultSnapshot Decode(ReadOnlySpan<byte> payload)
    {
        int count = payload.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(payload[4..]) : -1;
        if (payload.Length < FixedPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            count < 0 || count > RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult || payload[120] > 1 ||
            payload[121..128].IndexOfAnyExcept((byte)0) >= 0 || payload[140..144].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("OCR result payload header is invalid.");
        int modelIdBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[128..]);
        int modelVersionBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[132..]);
        int errorBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[136..]);
        if (modelIdBytes is < 1 or > 128 || modelVersionBytes is < 1 or > 128 || errorBytes is < 0 or > 128 ||
            FixedPayloadBytes > payload.Length - modelIdBytes - modelVersionBytes - errorBytes)
            throw new InvalidDataException("OCR result metadata lengths are invalid.");
        try
        {
            int offset = FixedPayloadBytes;
            string modelId = StrictUtf8.GetString(payload.Slice(offset, modelIdBytes));
            offset += modelIdBytes;
            string modelVersion = StrictUtf8.GetString(payload.Slice(offset, modelVersionBytes));
            offset += modelVersionBytes;
            string? error = errorBytes == 0 ? null : Encoding.ASCII.GetString(payload.Slice(offset, errorBytes));
            offset += errorBytes;
            var lines = new List<TextLine>(count);
            for (int index = 0; index < count; index++)
            {
                Require(payload, offset, FixedLineBytes);
                int textBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
                int orientationDegrees = BinaryPrimitives.ReadInt16LittleEndian(payload[(offset + 4)..]);
                if (textBytes < 0 || orientationDegrees is < -180 or > 180 ||
                    payload[offset + 6] > 1 || payload[offset + 7] != 0)
                    throw new InvalidDataException("OCR line header is invalid.");
                Require(payload, offset + FixedLineBytes, textBytes);
                lines.Add(new TextLine(
                    StrictUtf8.GetString(payload.Slice(offset + FixedLineBytes, textBytes)),
                    new NormalizedRect(
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 8)..]),
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 16)..]),
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 24)..]),
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 32)..])),
                    BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 40)..]))
                {
                    OrientationDegrees = orientationDegrees,
                    IsVertical = payload[offset + 6] == 1,
                });
                offset = checked(offset + FixedLineBytes + textBytes);
            }
            if (offset != payload.Length || error is not null && !IsStableCode(error))
                throw new InvalidDataException("OCR result has trailing data or an invalid error code.");
            EncodeMetadata(modelId, 128, nameof(modelId));
            EncodeMetadata(modelVersion, 128, nameof(modelVersion));
            return new OcrResultSnapshot(
                RuntimeOcrExecutionTokenCodec.Read(payload[8..120]),
                lines,
                modelId,
                modelVersion,
                payload[120] == 1,
                error);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DecoderFallbackException)
        {
            throw new InvalidDataException("OCR result payload fields are invalid.", exception);
        }
    }

    private static byte[] EncodeMetadata(string value, int maximumBytes, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        byte[] result = StrictUtf8.GetBytes(value);
        if (result.Length > maximumBytes || value.Any(char.IsControl))
            throw new ArgumentException("OCR metadata is invalid.", parameterName);
        return result;
    }

    private static bool IsStableCode(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');

    private static void Require(ReadOnlySpan<byte> payload, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > payload.Length - length)
            throw new InvalidDataException("OCR result payload is truncated.");
    }
}

public enum RuntimeOcrResultApplyStatus
{
    Applied = 1,
    StaleProfile = 2,
    StaleGeneration = 3,
    TargetMissing = 4,
    InvalidArea = 5,
    RuntimeFailure = 6,
}

public sealed record RuntimeOcrResultAcknowledgement(
    TargetInstanceId TargetInstanceId,
    long SourceGeneration,
    long ResultSequence,
    RuntimeOcrResultApplyStatus Status,
    string? ErrorCode)
{
    public bool Accepted => Status == RuntimeOcrResultApplyStatus.Applied;
}

public static class RuntimeOcrResultAcknowledgementPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int FixedPayloadBytes = 48;

    public static byte[] Encode(RuntimeOcrResultAcknowledgement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.TargetInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(value.SourceGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value.ResultSequence, 1);
        if (!Enum.IsDefined(value.Status)) throw new ArgumentOutOfRangeException(nameof(value));
        byte[] error = value.ErrorCode is null ? [] : Encoding.ASCII.GetBytes(value.ErrorCode);
        if (error.Length > 128 || error.Length > 0 && !IsStableCode(value.ErrorCode!))
            throw new ArgumentException("OCR acknowledgement error code is invalid.", nameof(value));
        if (value.Accepted != (error.Length == 0))
            throw new ArgumentException("OCR acknowledgement status and error code disagree.", nameof(value));
        byte[] payload = new byte[FixedPayloadBytes + error.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), (int)value.Status);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), value.SourceGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), value.ResultSequence);
        value.TargetInstanceId.Value.TryWriteBytes(payload.AsSpan(24, 16));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(40), error.Length);
        error.CopyTo(payload.AsSpan(FixedPayloadBytes));
        return payload;
    }

    public static RuntimeOcrResultAcknowledgement Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[44..48].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("OCR acknowledgement header is invalid.");
        int errorBytes = BinaryPrimitives.ReadInt32LittleEndian(payload[40..]);
        var status = (RuntimeOcrResultApplyStatus)BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        long sourceGeneration = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        long resultSequence = BinaryPrimitives.ReadInt64LittleEndian(payload[16..]);
        if (!Enum.IsDefined(status) || sourceGeneration < 1 || resultSequence < 1 ||
            errorBytes is < 0 or > 128 || payload.Length != FixedPayloadBytes + errorBytes)
            throw new InvalidDataException("OCR acknowledgement fields are invalid.");
        string? error = errorBytes == 0 ? null : Encoding.ASCII.GetString(payload[FixedPayloadBytes..]);
        if (error is not null && !IsStableCode(error) ||
            status == RuntimeOcrResultApplyStatus.Applied != (error is null))
            throw new InvalidDataException("OCR acknowledgement status and error code disagree.");
        try
        {
            return new RuntimeOcrResultAcknowledgement(
                new TargetInstanceId(new Guid(payload[24..40])),
                sourceGeneration,
                resultSequence,
                status,
                error);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("OCR acknowledgement identity is invalid.", exception);
        }
    }

    private static bool IsStableCode(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');
}
