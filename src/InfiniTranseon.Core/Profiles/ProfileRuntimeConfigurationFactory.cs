using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Profiles;

public static class ProfileRuntimeConfigurationFactory
{
    public static RuntimeProcessingConfiguration Create(
        ProfileTarget target,
        TargetInstanceId targetInstanceId,
        long configurationRevision,
        Guid profileId,
        long profileRevision,
        Func<string, RuntimeOcrBackend>? ocrBackendResolver = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        if (target.Regions.Any(region => region.AreaMode == CaptureAreaKind.RemainingArea))
            throw new ArgumentException(
                "Remaining-area settings must use ProfileTarget.RemainingAreaRegion.", nameof(target));
        if (target.ScanRemainingArea &&
            (target.RemainingAreaRegion is null || !target.RemainingAreaRegion.Enabled ||
             target.RemainingAreaRegion.AreaMode != CaptureAreaKind.RemainingArea))
            throw new ArgumentException(
                "Enabled remaining-area scanning requires dedicated remaining-area settings.", nameof(target));
        IEnumerable<ProfileRegion> configuredRegions = target.Regions;
        if (target.ScanRemainingArea)
            configuredRegions = configuredRegions.Append(target.RemainingAreaRegion! with
            {
                Bounds = new NormalizedRect(0, 0, 1, 1),
                RecognitionInterval = target.RemainingAreaInterval,
            });
        RuntimeProcessingRegion[] regions = configuredRegions
            .Where(region => region.Enabled)
            .Select(region => CreateRegion(
                region,
                ocrBackendResolver ?? (_ => RuntimeOcrBackend.Windows)))
            .ToArray();
        return new RuntimeProcessingConfiguration(
            targetInstanceId,
            configurationRevision,
            profileId,
            profileRevision,
            target.DetectionLongEdge,
            target.ScanRemainingArea,
            ToMilliseconds(target.RemainingAreaInterval, 100, nameof(target.RemainingAreaInterval)),
            regions);
    }

    private static RuntimeProcessingRegion CreateRegion(
        ProfileRegion region,
        Func<string, RuntimeOcrBackend> ocrBackendResolver)
    {
        string preprocessing = JsonSerializer.Serialize(region.Ocr.PreprocessingSteps);
        RuntimeOcrBackend backend = region.Ocr.UseCloudOcr
            ? RuntimeOcrBackend.Cloud
            : ocrBackendResolver(region.Ocr.RecognitionLanguage);
        return new RuntimeProcessingRegion(
            new RegionId(region.RegionId),
            region.Bounds,
            (RuntimeRegionPriority)region.Priority,
            region.AreaMode,
            ToMilliseconds(region.RecognitionInterval, 16, nameof(region.RecognitionInterval)),
            region.LockDegradation,
            region.Ocr.DetectOrientation,
            backend,
            region.Ocr.CloudConsentPolicyRevision,
            region.Ocr.DetectionScale,
            (RuntimeLineBreakMode)region.LineBreakMode,
            region.Ocr.ProviderId,
            region.Ocr.RecognitionLanguage,
            preprocessing);
    }

    private static int ToMilliseconds(TimeSpan value, int minimum, string parameterName)
    {
        double milliseconds = value.TotalMilliseconds;
        if (!double.IsFinite(milliseconds) || milliseconds < minimum || milliseconds > 3_600_000)
            throw new ArgumentOutOfRangeException(parameterName);
        return checked((int)Math.Ceiling(milliseconds));
    }
}
