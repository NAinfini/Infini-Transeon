using System.Buffers.Binary;
using System.Text;

namespace InfiniTranseon.Contracts.Runtime;

public sealed record RuntimeThumbnailRequest
{
    public RuntimeThumbnailRequest(
        TargetInstanceId targetInstanceId,
        int maximumLongEdge)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLongEdge, 320);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLongEdge, 1280);
        TargetInstanceId = targetInstanceId;
        MaximumLongEdge = maximumLongEdge;
    }

    public TargetInstanceId TargetInstanceId { get; }
    public int MaximumLongEdge { get; }
}

public sealed record RuntimeThumbnailAcknowledgement(
    TargetInstanceId TargetInstanceId,
    bool Accepted,
    RuntimeThumbnail? Thumbnail,
    string? ErrorCode);

public static class RuntimeThumbnailPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int RequestBytes = 24;
    public const int AcknowledgementFixedBytes = 52;
    public const int MaximumMimeTypeBytes = 64;
    public const int MaximumErrorCodeBytes = 128;
    public const int MaximumImageBytes = 4 * 1024 * 1024;

    public static byte[] EncodeRequest(RuntimeThumbnailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] payload = new byte[RequestBytes];
        BinaryPrimitives.WriteInt32LittleEndian(payload, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), request.MaximumLongEdge);
        request.TargetInstanceId.Value.TryWriteBytes(payload.AsSpan(8, 16));
        return payload;
    }

    public static RuntimeThumbnailRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != RequestBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion)
            throw new InvalidDataException("Thumbnail request header is invalid.");
        try
        {
            return new RuntimeThumbnailRequest(
                new TargetInstanceId(new Guid(payload[8..24])),
                BinaryPrimitives.ReadInt32LittleEndian(payload[4..]));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Thumbnail request fields are invalid.", exception);
        }
    }

    public static byte[] EncodeAcknowledgement(
        RuntimeThumbnailAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        RuntimeThumbnail? thumbnail = acknowledgement.Thumbnail;
        string mimeType = thumbnail?.MimeType ?? string.Empty;
        string errorCode = acknowledgement.ErrorCode ?? string.Empty;
        byte[] mime = Encoding.ASCII.GetBytes(mimeType);
        byte[] error = Encoding.ASCII.GetBytes(errorCode);
        byte[] image = thumbnail?.EncodedImage.ToArray() ?? [];
        ValidateAcknowledgement(
            acknowledgement.Accepted,
            thumbnail,
            mime,
            error,
            image);
        byte[] payload = new byte[checked(
            AcknowledgementFixedBytes + mime.Length + error.Length + image.Length)];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        bytes[4] = acknowledgement.Accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], thumbnail?.FrameSequence ?? 0);
        acknowledgement.TargetInstanceId.Value.TryWriteBytes(bytes[16..32]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], thumbnail?.PixelWidth ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[36..], thumbnail?.PixelHeight ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[40..], mime.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[44..], image.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], error.Length);
        int offset = AcknowledgementFixedBytes;
        mime.CopyTo(bytes[offset..]);
        offset += mime.Length;
        error.CopyTo(bytes[offset..]);
        offset += error.Length;
        image.CopyTo(bytes[offset..]);
        return payload;
    }

    public static RuntimeThumbnailAcknowledgement DecodeAcknowledgement(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < AcknowledgementFixedBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] > 1 ||
            payload[5] != 0 ||
            payload[6] != 0 ||
            payload[7] != 0)
            throw new InvalidDataException("Thumbnail acknowledgement header is invalid.");
        bool accepted = payload[4] != 0;
        int mimeLength = BinaryPrimitives.ReadInt32LittleEndian(payload[40..]);
        int imageLength = BinaryPrimitives.ReadInt32LittleEndian(payload[44..]);
        int errorLength = BinaryPrimitives.ReadInt32LittleEndian(payload[48..]);
        if (mimeLength is < 0 or > MaximumMimeTypeBytes ||
            imageLength is < 0 or > MaximumImageBytes ||
            errorLength is < 0 or > MaximumErrorCodeBytes ||
            payload.Length != AcknowledgementFixedBytes +
                mimeLength + errorLength + imageLength)
            throw new InvalidDataException("Thumbnail acknowledgement lengths are invalid.");
        int offset = AcknowledgementFixedBytes;
        string mime = Encoding.ASCII.GetString(payload.Slice(offset, mimeLength));
        offset += mimeLength;
        string error = Encoding.ASCII.GetString(payload.Slice(offset, errorLength));
        offset += errorLength;
        byte[] image = payload.Slice(offset, imageLength).ToArray();
        TargetInstanceId target;
        RuntimeThumbnail? thumbnail = null;
        try
        {
            target = new TargetInstanceId(new Guid(payload[16..32]));
            if (accepted)
            {
                thumbnail = new RuntimeThumbnail(
                    target,
                    BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
                    mime,
                    image,
                    BinaryPrimitives.ReadInt32LittleEndian(payload[32..]),
                    BinaryPrimitives.ReadInt32LittleEndian(payload[36..]));
            }
            ValidateAcknowledgement(
                accepted,
                thumbnail,
                Encoding.ASCII.GetBytes(mime),
                Encoding.ASCII.GetBytes(error),
                image);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Thumbnail acknowledgement fields are invalid.",
                exception);
        }
        return new RuntimeThumbnailAcknowledgement(
            target,
            accepted,
            thumbnail,
            error.Length == 0 ? null : error);
    }

    private static void ValidateAcknowledgement(
        bool accepted,
        RuntimeThumbnail? thumbnail,
        byte[] mime,
        byte[] error,
        byte[] image)
    {
        bool validAccepted = accepted &&
            thumbnail is not null &&
            thumbnail.FrameSequence > 0 &&
            thumbnail.PixelWidth is > 0 and <= 1280 &&
            thumbnail.PixelHeight is > 0 and <= 1280 &&
            mime is { Length: > 0 and <= MaximumMimeTypeBytes } &&
            image is { Length: > 0 and <= MaximumImageBytes } &&
            error.Length == 0;
        bool validRejected = !accepted &&
            thumbnail is null &&
            mime.Length == 0 &&
            image.Length == 0 &&
            error is { Length: > 0 and <= MaximumErrorCodeBytes };
        if (!validAccepted && !validRejected)
            throw new ArgumentException("Thumbnail acknowledgement fields are inconsistent.");
    }
}
