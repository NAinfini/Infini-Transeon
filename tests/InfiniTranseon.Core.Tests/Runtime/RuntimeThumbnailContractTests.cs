using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeThumbnailContractTests
{
    [Fact]
    public void RequestRoundTripsTargetAndBoundedLongEdge()
    {
        var request = new RuntimeThumbnailRequest(
            new TargetInstanceId(Guid.NewGuid()),
            960);

        RuntimeThumbnailRequest decoded = RuntimeThumbnailPayloadCodec.DecodeRequest(
            RuntimeThumbnailPayloadCodec.EncodeRequest(request));

        Assert.Equal(request, decoded);
    }

    [Fact]
    public void AcceptedThumbnailRoundTripsEncodedPixels()
    {
        var target = new TargetInstanceId(Guid.NewGuid());
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4];
        var acknowledgement = new RuntimeThumbnailAcknowledgement(
            target,
            true,
            new RuntimeThumbnail(target, 42, "image/png", png, 960, 540),
            null);

        RuntimeThumbnailAcknowledgement decoded =
            RuntimeThumbnailPayloadCodec.DecodeAcknowledgement(
                RuntimeThumbnailPayloadCodec.EncodeAcknowledgement(acknowledgement));

        Assert.True(decoded.Accepted);
        Assert.Null(decoded.ErrorCode);
        Assert.Equal(42, decoded.Thumbnail!.FrameSequence);
        Assert.Equal(png, decoded.Thumbnail.EncodedImage.ToArray());
    }

    [Fact]
    public void RejectedThumbnailRequiresStableFailure()
    {
        var target = new TargetInstanceId(Guid.NewGuid());
        var acknowledgement = new RuntimeThumbnailAcknowledgement(
            target,
            false,
            null,
            "thumbnail.frameUnavailable");

        RuntimeThumbnailAcknowledgement decoded =
            RuntimeThumbnailPayloadCodec.DecodeAcknowledgement(
                RuntimeThumbnailPayloadCodec.EncodeAcknowledgement(acknowledgement));

        Assert.False(decoded.Accepted);
        Assert.Equal("thumbnail.frameUnavailable", decoded.ErrorCode);
        Assert.Null(decoded.Thumbnail);
    }
}
