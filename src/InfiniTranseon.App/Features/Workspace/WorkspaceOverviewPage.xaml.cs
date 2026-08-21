using InfiniTranseon.App.Controls;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
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
    // Resolved per lookup so a UI language change takes effect without restarting; see AppStrings.
    private static ResourceLoader Strings => Localization.AppStrings.Loader;
    private readonly IProfileService _profiles;
    private readonly ISecretReferenceService _secrets;
    private readonly AppNavigationState _navigation;
    private readonly RunningTargetsViewModel _runtime;
    private Guid _profileId;
    private ProfileCard? _profile;

    public WorkspaceOverviewPage()
    {
        _profiles = App.GetService<IProfileService>();
        _secrets = App.GetService<ISecretReferenceService>();
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
            await ApplyReadinessAsync(_profile);
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
    private async Task ApplyReadinessAsync(ProfileCard profile)
    {
        bool targetReady = profile.MatchState == ProfileTargetMatchState.Matched;
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

        // This shared preflight covers both translation channels and enabled cloud OCR regions. It
        // uses the same credential bindings as runtime startup, so a green row cannot turn into a
        // first-frame provider failure.
        IReadOnlyList<ProviderReadinessStatus> providerReadiness =
            await ProfileProviderReadiness.EvaluateAsync(
                await _profiles.GetRequiredProviderIdsAsync(profile.ProfileId),
                ProfileProviderReadiness.GetCatalog(App.GetService<CustomRestAdapterStore>()),
                _secrets);
        ProviderReadinessStatus[] unconfigured =
            [.. providerReadiness.Where(status => !status.IsReady)];
        bool credentialsReady = providerReadiness.Count > 0 && unconfigured.Length == 0;
        ApplyReadinessRow(
            CredentialReadinessIcon,
            CredentialReadinessText,
            credentialsReady,
            providerReadiness.Count == 0
                ? Strings.GetString("WorkspaceCredentialsNoProvider")
                : credentialsReady
                    ? string.Format(
                        Strings.GetString("WorkspaceCredentialsReady"),
                        string.Join("、", providerReadiness.Select(status => status.DisplayName)))
                    : string.Format(
                        Strings.GetString("WorkspaceCredentialsNotReady"),
                        string.Join("、", unconfigured.Select(status => status.DisplayName))),
            CredentialReadinessRow);

        bool ready = targetReady && languageReady && regionsReady && credentialsReady;
        StartButton.IsEnabled = ready;

        // Naming the outstanding steps in order turns four independent checks into a route the user
        // can follow. Each name matches the readiness row above it, so the text says where to click.
        string[] outstanding =
        [
            .. new (bool Ready, string Key)[]
            {
                (targetReady, "WorkspaceStepTarget"),
                (languageReady, "WorkspaceStepLanguage"),
                (regionsReady, "WorkspaceStepRegions"),
                (credentialsReady, "WorkspaceStepCredentials"),
            }
            .Where(step => !step.Ready)
            .Select(step => Strings.GetString(step.Key)),
        ];
        StartBlockedText.Text = ready
            ? string.Empty
            : string.Format(
                Strings.GetString("WorkspaceStartBlocked"),
                string.Join(Strings.GetString("ListSeparator"), outstanding));
        StartBlockedText.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(StartButton, ready ? null : StartBlockedText.Text);
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
        _navigation.NavigateToProfileSetup(_profileId);

    private void OnTargetReadinessClick(object sender, RoutedEventArgs e) =>
        _navigation.NavigateToProfileSetup(_profileId);

    private void OnLanguageReadinessClick(object sender, RoutedEventArgs e) =>
        _navigation.NavigateToProfile(_profileId, WorkspaceSection.Language);

    private void OnCredentialReadinessClick(object sender, RoutedEventArgs e) =>
        _navigation.Navigate(GlobalDestination.Providers);

    private void OnRegionReadinessClick(object sender, RoutedEventArgs e) =>
        _navigation.NavigateToProfile(
            _profileId,
            _profile is { RegionCount: 0 } ? WorkspaceSection.Capture : WorkspaceSection.Channels);
}
