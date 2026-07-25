using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.Workspace;

public sealed partial class WorkspaceOverviewPage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private readonly IProfileService _profiles;
    private readonly AppNavigationState _navigation;
    private readonly RunningTargetsViewModel _runtime;
    private Guid _profileId;
    private ProfileCard? _profile;

    public WorkspaceOverviewPage()
    {
        _profiles = App.GetService<IProfileService>();
        _navigation = App.GetService<AppNavigationState>();
        _runtime = App.GetService<RunningTargetsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _profileId = e.Parameter is Guid profileId ? profileId : Guid.Empty;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Shell.IsLoading = true;
        try
        {
            _profile = (await _profiles.GetProfilesAsync())
                .SingleOrDefault(profile => profile.ProfileId == _profileId);
            if (_profile is null)
            {
                Shell.ErrorMessage = Strings.GetString("WorkspaceProfileMissing");
                return;
            }

            Shell.Title = _profile.Name;
            Shell.Subtitle = Strings.GetString("WorkspaceOverviewSubtitle");
            ApplyReadiness(_profile);
            TargetNameText.Text = _profile.TargetDescription;
            TargetDetailText.Text = _profile.Resolution;
            await _runtime.InitializeAsync();
        }
        catch (Exception exception)
        {
            Shell.ErrorMessage = exception.Message;
        }
        finally
        {
            Shell.IsLoading = false;
        }
    }

    /// <summary>
    /// Renders the readiness checklist from the profile's real state. Every row carries its verdict in
    /// icon, colour and text; a fixed green tick would have claimed readiness for a profile with no
    /// regions, no channels or an unmatched target.
    /// </summary>
    private void ApplyReadiness(ProfileCard profile)
    {
        bool targetReady = profile.MatchSeverity == StatusSeverity.Success;
        ApplyReadinessRow(
            TargetReadinessIcon,
            TargetReadinessText,
            targetReady,
            targetReady
                ? string.Format(Strings.GetString("WorkspaceTargetReady"), profile.TargetDescription)
                : string.Format(Strings.GetString("WorkspaceTargetNotReady"), profile.MatchStateText),
            TargetReadinessRow);

        bool languageReady = !string.IsNullOrWhiteSpace(profile.Languages);
        ApplyReadinessRow(
            LanguageReadinessIcon,
            LanguageReadinessText,
            languageReady,
            languageReady
                ? string.Format(Strings.GetString("WorkspaceLanguageReady"), profile.Languages)
                : Strings.GetString("WorkspaceLanguageNotReady"),
            LanguageReadinessRow);

        bool regionsReady = profile.RegionCount > 0 && profile.ChannelCount > 0;
        ApplyReadinessRow(
            RegionReadinessIcon,
            RegionReadinessText,
            regionsReady,
            string.Format(
                Strings.GetString(regionsReady ? "WorkspaceRegionsReady" : "WorkspaceRegionsNotReady"),
                profile.RegionCount,
                profile.ChannelCount),
            RegionReadinessRow);

        bool ready = targetReady && languageReady && regionsReady;
        StartButton.IsEnabled = ready;
        ToolTipService.SetToolTip(
            StartButton,
            ready ? null : Strings.GetString("WorkspaceStartBlocked"));
    }

    private static void ApplyReadinessRow(
        FontIcon icon,
        TextBlock text,
        bool ready,
        string message,
        FrameworkElement row)
    {
        icon.Glyph = ready ? "" : "";
        icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            ready ? "StatusSuccessBrush" : "StatusWarningBrush"];
        text.Text = message;
        AutomationProperties.SetName(row, message);
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            return;
        }

        _runtime.SelectedProfile = _runtime.Profiles.FirstOrDefault(
            profile => profile.ProfileId == _profile.ProfileId);
        if (!_runtime.StartCommand.CanExecute(null))
        {
            // Never a silent no-op: the engine refused, so say why instead of swallowing the click.
            Shell.ErrorMessage = string.Format(
                Strings.GetString("WorkspaceStartUnavailable"),
                Strings.GetString(EngineStatusPresenter.ResourceKeyFor(_runtime.EngineStatus)));
            return;
        }

        Shell.ErrorMessage = string.Empty;
        await _runtime.StartCommand.ExecuteAsync(null);
    }

    private void OnEditCaptureClick(object sender, RoutedEventArgs e) =>
        _navigation.NavigateToProfile(_profileId, WorkspaceSection.Capture);

    private void OnTargetReadinessClick(object sender, RoutedEventArgs e) =>
        _navigation.NavigateToProfile(_profileId, WorkspaceSection.Capture);

    private void OnLanguageReadinessClick(object sender, RoutedEventArgs e) =>
        _navigation.NavigateToProfile(_profileId, WorkspaceSection.Language);

    private void OnRegionReadinessClick(object sender, RoutedEventArgs e) =>
        _navigation.NavigateToProfile(
            _profileId,
            _profile is { RegionCount: 0 } ? WorkspaceSection.Capture : WorkspaceSection.Channels);
}
