using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Matches a stored profile target against the windows and displays this machine is showing now.
///
/// A profile records a window title or a display name, never a handle: handles do not survive the
/// game being closed. Two callers need the same answer — the runtime, to bind a capture source, and
/// the profile list, to say whether a profile can start — and they must agree. When the list decided
/// on its own it reported every profile ready, and the readiness checklist beneath it reported every
/// capture target missing; the two views of one fact had drifted because there were two of them.
/// </summary>
public static class CaptureTargetResolver
{
    /// <summary>
    /// The name this target is matched by. A window remembers the exact title it was bound to, which
    /// outlives a profile rename; anything else is matched by the profile's own name for the target.
    /// </summary>
    public static string WantedName(ProfileTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind == CaptureTargetKind.Window &&
            target.MachineBinding?.WindowTitle is { Length: > 0 } title
                ? title
                : target.Name;
    }

    /// <summary>
    /// The live capture target this profile target names, or null when the machine is not showing it.
    /// A fixed desktop region names no window or display, so it never resolves through here.
    /// </summary>
    public static CaptureProbeTarget? Find(
        ProfileTarget target,
        IReadOnlyList<CaptureProbeTarget> candidates)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidates);
        string wanted = WantedName(target);
        return target.Kind switch
        {
            CaptureTargetKind.Window => FirstNamed(Capturable(candidates, "Window"), wanted),
            CaptureTargetKind.Display => FirstNamed(Capturable(candidates, "Display", "Monitor"), wanted)
                // A single-monitor machine has exactly one answer, whatever the display was called
                // when the profile was made: adapters and driver updates rename \\.\DISPLAY1.
                ?? SoleCandidate(Capturable(candidates, "Display", "Monitor")),
            _ => null,
        };
    }

    /// <summary>
    /// Whether this target can be captured right now. A fixed desktop region is a rectangle in screen
    /// coordinates, so it is ready as soon as it has one; everything else has to be found.
    /// </summary>
    public static bool Matches(ProfileTarget target, IReadOnlyList<CaptureProbeTarget> candidates)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind == CaptureTargetKind.DesktopFixedRegion
            ? target.DesktopRegion is not null
            : Find(target, candidates) is not null;
    }

    private static CaptureProbeTarget[] Capturable(
        IReadOnlyList<CaptureProbeTarget> candidates,
        params string[] kinds) =>
        [.. candidates.Where(candidate =>
            candidate.Capturable &&
            candidate.NativeHandle != 0 &&
            kinds.Any(kind => string.Equals(candidate.Kind, kind, StringComparison.OrdinalIgnoreCase)))];

    // Exact first, then containment: a game appends "— 1920x1080" or a chapter name to its title
    // while it runs, and the profile stored the title as it was when the user picked it.
    private static CaptureProbeTarget? FirstNamed(CaptureProbeTarget[] candidates, string wanted) =>
        candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)) ??
        candidates.FirstOrDefault(candidate =>
            candidate.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase));

    private static CaptureProbeTarget? SoleCandidate(CaptureProbeTarget[] candidates) =>
        candidates.Length == 1 ? candidates[0] : null;
}
