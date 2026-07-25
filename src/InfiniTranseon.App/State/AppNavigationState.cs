namespace InfiniTranseon.App.State;

public enum GlobalDestination
{
    Home,
    Activity,
    Providers,
    Settings,
}

public enum WorkspaceSection
{
    Overview,
    Capture,
    Channels,
    Overlay,
    Language,
    History,
}

public sealed record ProfileWorkspaceNavigation(Guid ProfileId, WorkspaceSection Section);

public sealed class AppNavigationRequest(
    GlobalDestination? globalDestination,
    Guid profileId,
    WorkspaceSection? workspaceSection) : EventArgs
{
    public GlobalDestination? GlobalDestination { get; } = globalDestination;
    public Guid ProfileId { get; } = profileId;
    public WorkspaceSection? WorkspaceSection { get; } = workspaceSection;
    public bool IsWorkspace => ProfileId != Guid.Empty && WorkspaceSection is not null;
}

/// <summary>
/// WinUI-free navigation request seam shared by pages and the shell. It carries stable route
/// identity only; the shell remains the sole owner of page types, frames, and navigation chrome.
/// </summary>
public sealed class AppNavigationState
{
    public event EventHandler<AppNavigationRequest>? NavigationRequested;

    public void Navigate(GlobalDestination destination) =>
        NavigationRequested?.Invoke(this, new AppNavigationRequest(destination, Guid.Empty, null));

    public void NavigateToProfile(Guid profileId, WorkspaceSection section = WorkspaceSection.Overview)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A workspace route requires a profile ID.", nameof(profileId));
        }

        NavigationRequested?.Invoke(this, new AppNavigationRequest(null, profileId, section));
    }
}
