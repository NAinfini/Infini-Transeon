using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeCapabilitiesTests
{
    [Fact]
    public void VersionOneMatchesTheArchitectureSafetyCeilings()
    {
        RuntimeCapabilities capabilities = RuntimeCapabilities.VersionOne;

        Assert.Equal(1, capabilities.ProtocolVersion);
        Assert.Equal(8, capabilities.MaxCaptureSources);
        Assert.Equal(8, capabilities.MaxTargets);
        Assert.Equal(8192, capabilities.MaxCaptureDimension);
        Assert.Equal(33_554_432L, capabilities.MaxCapturePixelsPerSource);
        Assert.Equal(256, capabilities.MaxRegionsPerTarget);
        Assert.Equal(512, capabilities.MaxActiveTracksPerTarget);
        Assert.Equal(2048, capabilities.MaxOcrBoxesPerResult);
        Assert.Equal(4096, capabilities.MaxSourceChars);
        Assert.Equal(16_384, capabilities.MaxOverlayCharsPerTarget);
        Assert.Equal(4, capabilities.MaxTranslationChannelsPerRegion);
        Assert.Equal(3, capabilities.MaxOutstandingWgcFramesPerSource);
        Assert.Equal(2, capabilities.MaxOwnedFrameTexturesPerSource);
        Assert.Equal(8, capabilities.MaxReadbackCropsPerSource);
        Assert.Equal(8_388_608L, capabilities.MaxReadbackPixelsPerSourceRing);
        Assert.Equal(134_217_728L, capabilities.MaxGlobalOcrCropBytesInFlight);
        Assert.Equal(2, capabilities.MaxMappedReadbacksPerAdapter);
        Assert.Equal(20, capabilities.MaxMappedReadbackHoldMilliseconds);
        Assert.Equal(67_108_864L, capabilities.MaxDetectionPyramidBytesPerSource);
        Assert.Equal(134_217_728L, capabilities.MaxOverlaySurfaceBytesPerTarget);
        Assert.Equal(4, capabilities.MaxOcrSessions);
        Assert.Equal(268_435_456L, capabilities.MaxOcrTensorWorkspaceBytes);
        Assert.Equal(2_147_483_648L, capabilities.MaxEngineCommittedBytes);
        Assert.Equal(1_073_741_824L, capabilities.MaxGpuBytesPerAdapterCeiling);
        Assert.Equal(25, capabilities.MaxGpuBudgetPercentage);
        Assert.Equal(8_388_608, capabilities.MaxIpcMessageBytes);
        Assert.Equal(33_554_432L, capabilities.MaxIpcInFlightBytes);
        Assert.Equal(5_242_880L, capabilities.MaxRecentTranslationBytes);
        Assert.Equal(536_870_912L, capabilities.MaxTranslationCacheBytes);
        Assert.Equal(67_108_864L, capabilities.MaxDatabasePageCacheBytes);
    }
}
