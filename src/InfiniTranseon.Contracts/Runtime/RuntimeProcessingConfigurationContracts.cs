using System.Buffers.Binary;
using System.Text;

namespace InfiniTranseon.Contracts.Runtime;

public enum RuntimeRegionPriority : byte
{
    P0 = 0,
    P1 = 1,
    P2 = 2,
    P3 = 3,
}

public enum RuntimeLineBreakMode : byte
{
    PreserveLines = 0,
    JoinParagraph = 1,
    KeyValueRows = 2,
    CustomSeparator = 3,
    PerBox = 4,
}

public sealed record RuntimeProcessingRegion
{
    public RuntimeProcessingRegion(
        RegionId regionId,
        NormalizedRect bounds,
        RuntimeRegionPriority priority,
        CaptureAreaKind areaMode,
        int recognitionIntervalMilliseconds,
        bool lockDegradation,
        bool detectOrientation,
        bool useCloudOcr,
        long cloudConsentPolicyRevision,
        double detectionScale,
        RuntimeLineBreakMode lineBreakMode,
        string ocrProviderId,
        string recognitionLanguage,
        string preprocessingPipeline)
    {
        ArgumentNullException.ThrowIfNull(regionId);
        ArgumentNullException.ThrowIfNull(bounds);
        if (!Enum.IsDefined(priority) || !Enum.IsDefined(areaMode) || !Enum.IsDefined(lineBreakMode))
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (recognitionIntervalMilliseconds is < 16 or > 3_600_000)
            throw new ArgumentOutOfRangeException(nameof(recognitionIntervalMilliseconds));
        if (!double.IsFinite(detectionScale) || detectionScale is < 0.1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(detectionScale));
        if (cloudConsentPolicyRevision < 0 ||
            useCloudOcr != (cloudConsentPolicyRevision > 0))
            throw new ArgumentOutOfRangeException(nameof(cloudConsentPolicyRevision));
        RuntimeProcessingConfigurationPayloadCodec.ValidateConfigurationText(
            ocrProviderId, 128, nameof(ocrProviderId));
        RuntimeProcessingConfigurationPayloadCodec.ValidateConfigurationText(
            recognitionLanguage, 64, nameof(recognitionLanguage));
        RuntimeProcessingConfigurationPayloadCodec.ValidateConfigurationText(
            preprocessingPipeline, 512, nameof(preprocessingPipeline), allowEmpty: true);

        RegionId = regionId;
        Bounds = bounds;
        Priority = priority;
        AreaMode = areaMode;
        RecognitionIntervalMilliseconds = recognitionIntervalMilliseconds;
        LockDegradation = lockDegradation;
        DetectOrientation = detectOrientation;
        UseCloudOcr = useCloudOcr;
        CloudConsentPolicyRevision = cloudConsentPolicyRevision;
        DetectionScale = detectionScale;
        LineBreakMode = lineBreakMode;
        OcrProviderId = ocrProviderId;
        RecognitionLanguage = recognitionLanguage;
        PreprocessingPipeline = preprocessingPipeline;
    }

    public RegionId RegionId { get; }
    public NormalizedRect Bounds { get; }
    public RuntimeRegionPriority Priority { get; }
    public CaptureAreaKind AreaMode { get; }
    public int RecognitionIntervalMilliseconds { get; }
    public bool LockDegradation { get; }
    public bool DetectOrientation { get; }
    public bool UseCloudOcr { get; }
    public long CloudConsentPolicyRevision { get; }
    public double DetectionScale { get; }
    public RuntimeLineBreakMode LineBreakMode { get; }
    public string OcrProviderId { get; }
    public string RecognitionLanguage { get; }
    public string PreprocessingPipeline { get; }
}

public sealed record RuntimeProcessingConfiguration
{
    public RuntimeProcessingConfiguration(
        TargetInstanceId targetInstanceId,
        long configurationRevision,
        Guid profileId,
        long profileRevision,
        int detectionLongEdge,
        bool scanRemainingArea,
        int remainingAreaIntervalMilliseconds,
        IEnumerable<RuntimeProcessingRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentOutOfRangeException.ThrowIfLessThan(configurationRevision, 1);
        if (profileId == Guid.Empty)
            throw new ArgumentException("Profile identity cannot be empty.", nameof(profileId));
        ArgumentOutOfRangeException.ThrowIfLessThan(profileRevision, 1);
        if (detectionLongEdge is < 320 or > 1920)
            throw new ArgumentOutOfRangeException(nameof(detectionLongEdge));
        if (remainingAreaIntervalMilliseconds is < 100 or > 3_600_000)
            throw new ArgumentOutOfRangeException(nameof(remainingAreaIntervalMilliseconds));
        RuntimeProcessingRegion[] regionArray = regions.ToArray();
        if (regionArray.Length > RuntimeCapabilities.VersionOne.MaxRegionsPerTarget ||
            regionArray.Select(item => item.RegionId).Distinct().Count() != regionArray.Length)
            throw new ArgumentException("Processing regions exceed capacity or contain duplicate IDs.", nameof(regions));

        TargetInstanceId = targetInstanceId;
        ConfigurationRevision = configurationRevision;
        ProfileId = profileId;
        ProfileRevision = profileRevision;
        DetectionLongEdge = detectionLongEdge;
        ScanRemainingArea = scanRemainingArea;
        RemainingAreaIntervalMilliseconds = remainingAreaIntervalMilliseconds;
        Regions = Array.AsReadOnly(regionArray);
    }

    public TargetInstanceId TargetInstanceId { get; }
    public long ConfigurationRevision { get; }
    public Guid ProfileId { get; }
    public long ProfileRevision { get; }
    public int DetectionLongEdge { get; }
    public bool ScanRemainingArea { get; }
    public int RemainingAreaIntervalMilliseconds { get; }
    public IReadOnlyList<RuntimeProcessingRegion> Regions { get; }
}

public enum RuntimeProcessingConfigurationStatus : byte
{
    Applied = 1,
    StaleRevision = 2,
    StaleProfile = 3,
    TargetMissing = 4,
    InvalidConfiguration = 5,
}

public sealed record RuntimeProcessingConfigurationAcknowledgement
{
    public RuntimeProcessingConfigurationAcknowledgement(
        TargetInstanceId targetInstanceId,
        long configurationRevision,
        bool accepted,
        RuntimeProcessingConfigurationStatus status,
        string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(configurationRevision, 1);
        if (!Enum.IsDefined(status) || accepted != (status == RuntimeProcessingConfigurationStatus.Applied) ||
            accepted != string.IsNullOrEmpty(errorCode) ||
            errorCode is not null && !RuntimeProcessingConfigurationPayloadCodec.IsStableCode(errorCode))
            throw new ArgumentException("Processing configuration acknowledgement is invalid.");
        TargetInstanceId = targetInstanceId;
        ConfigurationRevision = configurationRevision;
        Accepted = accepted;
        Status = status;
        ErrorCode = errorCode;
    }

    public TargetInstanceId TargetInstanceId { get; }
    public long ConfigurationRevision { get; }
    public bool Accepted { get; }
    public RuntimeProcessingConfigurationStatus Status { get; }
    public string? ErrorCode { get; }
}

public static class RuntimeProcessingConfigurationPayloadCodec
{
    public const int SchemaVersion = 3;
    public const int FixedPayloadBytes = 72;
    public const int FixedRegionBytes = 80;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(RuntimeProcessingConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var entries = value.Regions.Select(region => (
            Region: region,
            Provider: StrictUtf8.GetBytes(region.OcrProviderId),
            Language: StrictUtf8.GetBytes(region.RecognitionLanguage),
            Pipeline: StrictUtf8.GetBytes(region.PreprocessingPipeline))).ToArray();
        int length = entries.Aggregate(FixedPayloadBytes, (total, entry) => checked(
            total + FixedRegionBytes + entry.Provider.Length + entry.Language.Length + entry.Pipeline.Length));
        if (length > RuntimeProtocol.MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(value));

        byte[] payload = new byte[length];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], entries.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.ConfigurationRevision);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], value.ProfileRevision);
        value.TargetInstanceId.Value.TryWriteBytes(bytes[24..40]);
        value.ProfileId.TryWriteBytes(bytes[40..56]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], value.DetectionLongEdge);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[60..], value.RemainingAreaIntervalMilliseconds);
        bytes[64] = value.ScanRemainingArea ? (byte)1 : (byte)0;
        int offset = FixedPayloadBytes;
        foreach (var entry in entries)
        {
            RuntimeProcessingRegion region = entry.Region;
            region.RegionId.Value.TryWriteBytes(bytes[offset..(offset + 16)]);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 16)..], region.Bounds.X);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 24)..], region.Bounds.Y);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 32)..], region.Bounds.Width);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 40)..], region.Bounds.Height);
            bytes[offset + 48] = (byte)region.Priority;
            bytes[offset + 49] = (byte)region.AreaMode;
            bytes[offset + 50] = region.LockDegradation ? (byte)1 : (byte)0;
            bytes[offset + 51] = (byte)((region.DetectOrientation ? 1 : 0) | (region.UseCloudOcr ? 2 : 0));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(offset + 52)..], region.RecognitionIntervalMilliseconds);
            bytes[offset + 56] = (byte)region.LineBreakMode;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 58)..], checked((ushort)entry.Provider.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 60)..], checked((ushort)entry.Language.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 62)..], checked((ushort)entry.Pipeline.Length));
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[(offset + 64)..], region.DetectionScale);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[(offset + 72)..], region.CloudConsentPolicyRevision);
            offset += FixedRegionBytes;
            entry.Provider.CopyTo(bytes[offset..]);
            offset += entry.Provider.Length;
            entry.Language.CopyTo(bytes[offset..]);
            offset += entry.Language.Length;
            entry.Pipeline.CopyTo(bytes[offset..]);
            offset += entry.Pipeline.Length;
        }
        return payload;
    }

    public static RuntimeProcessingConfiguration Decode(ReadOnlySpan<byte> payload)
    {
        int count = payload.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(payload[4..]) : -1;
        if (payload.Length < FixedPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            count < 0 || count > RuntimeCapabilities.VersionOne.MaxRegionsPerTarget ||
            payload[64] > 1 || payload[65..72].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Processing configuration header is invalid.");
        int offset = FixedPayloadBytes;
        var regions = new List<RuntimeProcessingRegion>(count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                Require(payload, offset, FixedRegionBytes);
                int providerBytes = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 58)..]);
                int languageBytes = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 60)..]);
                int pipelineBytes = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 62)..]);
                int variableBytes = checked(providerBytes + languageBytes + pipelineBytes);
                Require(payload, offset + FixedRegionBytes, variableBytes);
                if (payload[offset + 50] > 1 || (payload[offset + 51] & ~3) != 0 || payload[offset + 57] != 0)
                    throw new InvalidDataException("Processing region flags are invalid.");
                int textOffset = offset + FixedRegionBytes;
                string provider = StrictUtf8.GetString(payload.Slice(textOffset, providerBytes));
                textOffset += providerBytes;
                string language = StrictUtf8.GetString(payload.Slice(textOffset, languageBytes));
                textOffset += languageBytes;
                string pipeline = StrictUtf8.GetString(payload.Slice(textOffset, pipelineBytes));
                regions.Add(new RuntimeProcessingRegion(
                    new RegionId(new Guid(payload.Slice(offset, 16))),
                    new NormalizedRect(
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 16)..]),
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 24)..]),
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 32)..]),
                        BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 40)..])),
                    (RuntimeRegionPriority)payload[offset + 48],
                    (CaptureAreaKind)payload[offset + 49],
                    BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 52)..]),
                    payload[offset + 50] == 1,
                    (payload[offset + 51] & 1) != 0,
                    (payload[offset + 51] & 2) != 0,
                    BinaryPrimitives.ReadInt64LittleEndian(payload[(offset + 72)..]),
                    BinaryPrimitives.ReadDoubleLittleEndian(payload[(offset + 64)..]),
                    (RuntimeLineBreakMode)payload[offset + 56],
                    provider,
                    language,
                    pipeline));
                offset = checked(offset + FixedRegionBytes + variableBytes);
            }
            if (offset != payload.Length) throw new InvalidDataException("Processing configuration has trailing data.");
            return new RuntimeProcessingConfiguration(
                new TargetInstanceId(new Guid(payload[24..40])),
                BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
                new Guid(payload[40..56]),
                BinaryPrimitives.ReadInt64LittleEndian(payload[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[56..]),
                payload[64] == 1,
                BinaryPrimitives.ReadInt32LittleEndian(payload[60..]),
                regions);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DecoderFallbackException)
        {
            throw new InvalidDataException("Processing configuration fields are invalid.", exception);
        }
    }

    internal static void ValidateConfigurationText(string value, int maximumBytes, string parameterName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        int bytes = StrictUtf8.GetByteCount(value);
        if ((!allowEmpty && bytes == 0) || bytes > maximumBytes || value.Any(character => char.IsControl(character)))
            throw new ArgumentException("Runtime configuration text is invalid.", parameterName);
    }

    internal static bool IsStableCode(string value) =>
        value.Length is > 0 and <= 128 && value.All(character => character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static void Require(ReadOnlySpan<byte> payload, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > payload.Length - length)
            throw new InvalidDataException("Processing configuration payload is truncated.");
    }
}

public static class RuntimeProcessingConfigurationAcknowledgementPayloadCodec
{
    public const int SchemaVersion = 1;
    public const int FixedPayloadBytes = 40;

    public static byte[] Encode(RuntimeProcessingConfigurationAcknowledgement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] error = value.ErrorCode is null ? [] : Encoding.ASCII.GetBytes(value.ErrorCode);
        byte[] payload = new byte[FixedPayloadBytes + error.Length];
        Span<byte> bytes = payload;
        BinaryPrimitives.WriteInt32LittleEndian(bytes, SchemaVersion);
        bytes[4] = value.Accepted ? (byte)1 : (byte)0;
        bytes[5] = (byte)value.Status;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.ConfigurationRevision);
        value.TargetInstanceId.Value.TryWriteBytes(bytes[16..32]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], error.Length);
        error.CopyTo(bytes[FixedPayloadBytes..]);
        return payload;
    }

    public static RuntimeProcessingConfigurationAcknowledgement Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion ||
            payload[4] > 1 || payload[6] != 0 || payload[7] != 0 ||
            payload[36..40].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Processing configuration acknowledgement header is invalid.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(payload[32..]);
        if (length is < 0 or > 128 || payload.Length != FixedPayloadBytes + length)
            throw new InvalidDataException("Processing configuration acknowledgement length is invalid.");
        try
        {
            return new RuntimeProcessingConfigurationAcknowledgement(
                new TargetInstanceId(new Guid(payload[16..32])),
                BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
                payload[4] == 1,
                (RuntimeProcessingConfigurationStatus)payload[5],
                length == 0 ? null : Encoding.ASCII.GetString(payload[FixedPayloadBytes..]));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Processing configuration acknowledgement fields are invalid.", exception);
        }
    }
}
