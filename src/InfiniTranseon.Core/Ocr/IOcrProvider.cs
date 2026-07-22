using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Ocr;

public sealed record CloudOcrProviderRequest(
    OcrExecutionToken ExecutionToken,
    string MimeType,
    ReadOnlyMemory<byte> EncodedCrop,
    int PixelWidth,
    int PixelHeight);

public interface IOcrProvider
{
    ValueTask<OcrResultSnapshot> RecognizeAsync(
        CloudOcrProviderRequest request,
        CancellationToken cancellationToken);
}

public sealed record OcrProviderRegistration
{
    public OcrProviderRegistration(string providerId, bool requiresNetwork, Func<IOcrProvider> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(factory);
        ProviderId = providerId;
        RequiresNetwork = requiresNetwork;
        Factory = factory;
    }

    public string ProviderId { get; }
    public bool RequiresNetwork { get; }
    public Func<IOcrProvider> Factory { get; }
}

public sealed record CloudOcrRouteRequest
{
    private readonly byte[] _encodedCrop;

    public CloudOcrRouteRequest(
        OcrExecutionToken executionToken,
        string mimeType,
        ReadOnlySpan<byte> encodedCrop,
        int pixelWidth,
        int pixelHeight,
        bool explicitCloudConsent,
        long consentPolicyRevision = 1,
        int? encodedByteCeiling = null,
        DateTimeOffset? deadlineUtc = null)
    {
        ArgumentNullException.ThrowIfNull(executionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(consentPolicyRevision, 1);
        int byteCeiling = encodedByteCeiling ?? RuntimeProtocol.MaxPayloadBytes;
        if (byteCeiling is < 1 or > RuntimeProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(encodedByteCeiling));
        if (encodedCrop.IsEmpty || encodedCrop.Length > byteCeiling)
            throw new ArgumentOutOfRangeException(nameof(encodedCrop));
        ExecutionToken = executionToken;
        MimeType = mimeType;
        _encodedCrop = encodedCrop.ToArray();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        ExplicitCloudConsent = explicitCloudConsent;
        ConsentPolicyRevision = consentPolicyRevision;
        EncodedByteCeiling = byteCeiling;
        DeadlineUtc = deadlineUtc ?? DateTimeOffset.UtcNow.AddSeconds(30);
    }

    public OcrExecutionToken ExecutionToken { get; }
    public string MimeType { get; }
    public ReadOnlyMemory<byte> EncodedCrop => _encodedCrop;
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public bool ExplicitCloudConsent { get; }
    public long ConsentPolicyRevision { get; }
    public int EncodedByteCeiling { get; }
    public DateTimeOffset DeadlineUtc { get; }
}
