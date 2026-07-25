using InfiniTranseon.App.State;

namespace InfiniTranseon.App.Tests;

public sealed class AppNavigationStateTests
{
    [Fact]
    public void Global_navigation_raises_a_global_request()
    {
        var state = new AppNavigationState();
        AppNavigationRequest? request = null;
        state.NavigationRequested += (_, value) => request = value;

        state.Navigate(GlobalDestination.Providers);

        Assert.NotNull(request);
        Assert.Equal(GlobalDestination.Providers, request.GlobalDestination);
        Assert.False(request.IsWorkspace);
    }

    [Fact]
    public void Workspace_navigation_requires_a_profile_and_preserves_section()
    {
        var state = new AppNavigationState();
        AppNavigationRequest? request = null;
        Guid profileId = Guid.NewGuid();
        state.NavigationRequested += (_, value) => request = value;

        state.NavigateToProfile(profileId, WorkspaceSection.Channels);

        Assert.NotNull(request);
        Assert.True(request.IsWorkspace);
        Assert.Equal(profileId, request.ProfileId);
        Assert.Equal(WorkspaceSection.Channels, request.WorkspaceSection);
        Assert.Throws<ArgumentException>(() => state.NavigateToProfile(Guid.Empty));
    }
}
