using InfiniTranseon.App.Presentation.ViewModels;

namespace InfiniTranseon.App.Shell;

/// <summary>
/// A single navigation destination: the menu tag and the view model its page resolves from the
/// container (null for pages that currently have no view model).
/// </summary>
public sealed record NavigationDestination(string Tag, Type? ViewModelType);

/// <summary>
/// Canonical navigation destinations for the shell. Free of WinUI types so the tag set and each
/// destination's view model can be verified without a UI host. <see cref="AppShell"/> owns the
/// tag→page-type map (page types are WinUI) and validates its tag set against this source of truth
/// at startup so a missing or orphaned navigation entry fails fast instead of producing a dead menu
/// item.
/// </summary>
public static class NavigationMap
{
    public static IReadOnlyList<NavigationDestination> Destinations { get; } =
    [
        new("home", null),
        new("workspace-home", null),
        new("overview", typeof(ProfileCenterViewModel)),
        new("capture", typeof(WorkbenchViewModel)),
        new("channels", typeof(WorkbenchViewModel)),
        new("overlay", typeof(WorkbenchViewModel)),
        new("language", typeof(GlossaryViewModel)),
        new("history", typeof(HistoryViewModel)),
        new("activity", typeof(DiagnosticsViewModel)),
        new("providers", typeof(ServicesModelsViewModel)),
        new("settings", typeof(SettingsViewModel)),
    ];

    public static IReadOnlySet<string> Tags { get; } =
        Destinations.Select(destination => destination.Tag).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Throws when the shell's page-tag set diverges from the canonical destinations, naming the
    /// missing and orphaned tags so the mismatch is diagnosable at startup.
    /// </summary>
    public static void EnsureConsistent(IEnumerable<string> pageTags)
    {
        ArgumentNullException.ThrowIfNull(pageTags);

        HashSet<string> actual = pageTags.ToHashSet(StringComparer.Ordinal);
        if (actual.SetEquals(Tags))
        {
            return;
        }

        IEnumerable<string> missing = Tags.Except(actual);
        IEnumerable<string> orphaned = actual.Except(Tags);
        throw new InvalidOperationException(
            $"Navigation tag mismatch. Missing page(s): [{string.Join(", ", missing)}]; " +
            $"orphaned page(s): [{string.Join(", ", orphaned)}].");
    }
}
