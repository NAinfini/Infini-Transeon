using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Profiles;

public static class ProfileOverlaySnapshotFactory
{
    public static (OverlayPixelRect Bounds, OverlayRegionStyleSnapshot Style) Create(
        ProfileRegion region,
        int targetPixelWidth,
        int targetPixelHeight,
        NormalizedRect? detectedBounds = null,
        bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (targetPixelWidth is < 1 or > 16_384)
            throw new ArgumentOutOfRangeException(nameof(targetPixelWidth));
        if (targetPixelHeight is < 1 or > 16_384)
            throw new ArgumentOutOfRangeException(nameof(targetPixelHeight));

        OverlayBackgroundTreatment background = region.Overlay.Mode switch
        {
            OverlayMode.TranslucentOrBlurred => OverlayBackgroundTreatment.Translucent,
            OverlayMode.Offset => OverlayBackgroundTreatment.Offset,
            OverlayMode.FloatingPanel => OverlayBackgroundTreatment.FloatingPanel,
            OverlayMode.NoCover => OverlayBackgroundTreatment.NoCover,
            OverlayMode.Replace => region.Overlay.BackgroundMode switch
            {
                OverlayBackgroundMode.AutomaticContrastBlur => OverlayBackgroundTreatment.AutomaticContrast,
                OverlayBackgroundMode.Solid => OverlayBackgroundTreatment.Opaque,
                OverlayBackgroundMode.Transparent => OverlayBackgroundTreatment.NoCover,
                OverlayBackgroundMode.Translucent => OverlayBackgroundTreatment.Translucent,
                OverlayBackgroundMode.CachedCleanBackground => OverlayBackgroundTreatment.TemporalCache,
                _ => throw new ArgumentOutOfRangeException(nameof(region)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(region)),
        };
        OverlayPixelRect? destination = region.Overlay.Mode is OverlayMode.Offset or OverlayMode.FloatingPanel
            ? Map(region.Overlay.OffsetDestination ?? throw new ArgumentException(
                "Offset and floating overlay modes require a destination region.", nameof(region)),
                targetPixelWidth,
                targetPixelHeight)
            : null;
        var style = new OverlayRegionStyleSnapshot(
            background,
            region.LineAlignment switch
            {
                LineAlignment.Center => OverlayTextAlignment.Center,
                LineAlignment.Right => OverlayTextAlignment.Right,
                _ => OverlayTextAlignment.Left,
            },
            region.Overlay.BackgroundColor ?? "#CC000000",
            region.Overlay.TextColor ?? "#FFFFFFFF",
            region.Overlay.Opacity,
            region.Overlay.BlurRadius,
            Padding: 8,
            PreferredFontSize: region.Overlay.PreferredFontSize,
            MinimumFontSize: 12,
            MaximumLines: region.MaximumLines)
        {
            DestinationBounds = destination,
            OutlineColor = region.Overlay.OutlineColor ?? "#FF000000",
            OutlineWidth = region.Overlay.OutlineWidth,
            MinimumDwellMilliseconds = checked((int)region.Overlay.MinimumDwell.TotalMilliseconds),
            CrossfadeMilliseconds = reducedMotion
                ? 0
                : checked((int)region.Overlay.CrossfadeDuration.TotalMilliseconds),
            ReducedMotion = reducedMotion,
        };
        OverlayPixelRect bounds = Map(
            detectedBounds ?? region.Bounds, targetPixelWidth, targetPixelHeight);
        _ = new OverlayRegionSnapshot(region.RegionId, bounds, style, []);
        return (bounds, style);
    }

    private static OverlayPixelRect Map(NormalizedRect value, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(value.X * width), 0, width);
        int top = Math.Clamp((int)Math.Floor(value.Y * height), 0, height);
        int right = Math.Clamp(
            (int)Math.Ceiling((value.X + value.Width) * width - 1e-9), 0, width);
        int bottom = Math.Clamp(
            (int)Math.Ceiling((value.Y + value.Height) * height - 1e-9), 0, height);
        return new OverlayPixelRect(left, top, right - left, bottom - top);
    }
}
