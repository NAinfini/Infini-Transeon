using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.App.Presentation.Services;

internal static class ProfilePresentationMapper
{
    public static CaptureTargetKind CaptureTargetKind(string value) =>
        Enum.TryParse(value, ignoreCase: true, out CaptureTargetKind parsed)
            ? parsed
            : InfiniTranseon.Core.Profiles.CaptureTargetKind.Window;

    public static RegionPriority Priority(RegionPriorityLevel value) => value switch
    {
        RegionPriorityLevel.P0 => RegionPriority.P0,
        RegionPriorityLevel.P1 => RegionPriority.P1,
        RegionPriorityLevel.P2 => RegionPriority.P2,
        _ => RegionPriority.P3,
    };

    public static RegionPriorityLevel Priority(RegionPriority value) => value switch
    {
        RegionPriority.P0 => RegionPriorityLevel.P0,
        RegionPriority.P1 => RegionPriorityLevel.P1,
        RegionPriority.P2 => RegionPriorityLevel.P2,
        _ => RegionPriorityLevel.P3,
    };

    public static ProfileRegionContextRole ContextRole(RegionContextRole value) => value switch
    {
        RegionContextRole.Speaker => ProfileRegionContextRole.Speaker,
        RegionContextRole.Scene => ProfileRegionContextRole.Scene,
        RegionContextRole.Dialogue => ProfileRegionContextRole.Dialogue,
        _ => ProfileRegionContextRole.None,
    };

    public static RegionContextRole ContextRole(ProfileRegionContextRole value) => value switch
    {
        ProfileRegionContextRole.Speaker => RegionContextRole.Speaker,
        ProfileRegionContextRole.Scene => RegionContextRole.Scene,
        ProfileRegionContextRole.Dialogue => RegionContextRole.Dialogue,
        _ => RegionContextRole.None,
    };
}
