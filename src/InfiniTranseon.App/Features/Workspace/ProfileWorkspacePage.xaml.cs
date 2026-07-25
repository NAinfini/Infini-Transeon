using System.ComponentModel;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.Workspace;

/// <summary>
/// Workspace route host. Each section receives the same profile identity; section pages remain
/// independently navigable while the outer shell owns the two-state navigation chrome. The container
/// also owns the save bar, because the draft being edited is shared by every section.
/// </summary>
public sealed partial class ProfileWorkspacePage : Page
{
    private const string SavingGlyph = "";
    private const string ErrorGlyph = "";
    private const string UnsavedGlyph = "";
    private const string SavedGlyph = "";

    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private readonly WorkbenchViewModel _workbench;
    private readonly AppNavigationState _navigation;
    private ProfileWorkspaceNavigation? _route;
    private bool _saving;
    private bool _resultPending;
    // Only a failure of a save this bar started may be reported as a save failure; the view model's
    // HasError also covers load failures, which the bar must not relabel as unsaved work.
    private bool _saveFailed;

    public ProfileWorkspacePage()
    {
        _workbench = App.GetService<WorkbenchViewModel>();
        _navigation = App.GetService<AppNavigationState>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ProfileWorkspaceNavigation route)
        {
            return;
        }

        _route = route;

        Type pageType = route.Section switch
        {
            WorkspaceSection.Overview => typeof(WorkspaceOverviewPage),
            WorkspaceSection.Capture => typeof(CaptureSectionPage),
            WorkspaceSection.Channels => typeof(ChannelsSectionPage),
            WorkspaceSection.Overlay => typeof(OverlaySectionPage),
            WorkspaceSection.Language => typeof(Features.Glossary.GlossaryPage),
            WorkspaceSection.History => typeof(Features.History.HistoryPage),
            _ => typeof(WorkspaceOverviewPage),
        };
        object parameter =
            pageType == typeof(CaptureSectionPage) ||
            pageType == typeof(ChannelsSectionPage) ||
            pageType == typeof(OverlaySectionPage)
                ? route
                : route.ProfileId;
        WorkspaceFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _workbench.PropertyChanged -= OnWorkbenchPropertyChanged;
        _workbench.PropertyChanged += OnWorkbenchPropertyChanged;
        RefreshSaveBar();
        if (_route is null)
        {
            return;
        }

        // The container needs the draft loaded for its own chrome: the breadcrumb names the profile
        // and the save bar reports its dirty state, neither of which any single section owns.
        await _workbench.EnsureLoadedAsync(_route.ProfileId);
        RefreshBreadcrumb();
    }

    private void RefreshBreadcrumb()
    {
        if (_route is null)
        {
            return;
        }

        string section = _route.Section switch
        {
            WorkspaceSection.Overview => "NavWorkspaceOverview/Content",
            WorkspaceSection.Capture => "NavWorkspaceCapture/Content",
            WorkspaceSection.Channels => "NavWorkspaceChannels/Content",
            WorkspaceSection.Overlay => "NavWorkspaceOverlay/Content",
            WorkspaceSection.Language => "NavWorkspaceLanguage/Content",
            WorkspaceSection.History => "NavWorkspaceHistory/Content",
            _ => "NavWorkspaceOverview/Content",
        };
        WorkspaceBreadcrumb.ItemsSource = new[]
        {
            _workbench.ProfileName,
            Strings.GetString(section),
        };
    }

    private void OnBreadcrumbItemClicked(
        BreadcrumbBar sender,
        BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0 && _route is not null)
        {
            _navigation.NavigateToProfile(_route.ProfileId, WorkspaceSection.Overview);
        }
    }

    // The view model is a singleton that outlives this page, so unsubscribing is mandatory rather
    // than merely tidy: without it every workspace visit would leave another live handler behind.
    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        _workbench.PropertyChanged -= OnWorkbenchPropertyChanged;

    private void OnWorkbenchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchViewModel.IsDirty)
            or nameof(WorkbenchViewModel.ApplyState))
        {
            RefreshSaveBar();
        }
        else if (e.PropertyName is nameof(WorkbenchViewModel.ProfileName))
        {
            RefreshBreadcrumb();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _saving = true;
        _resultPending = false;
        _saveFailed = false;
        RefreshSaveBar();
        try
        {
            await _workbench.SaveAsync();
        }
        finally
        {
            _saving = false;
        }

        // The apply result decides whether a running target already picked the change up, so it is
        // reported explicitly and stays until dismissed instead of vanishing with the bar.
        _saveFailed = _workbench.HasError;
        _resultPending = !_saveFailed;
        RefreshSaveBar();
    }

    private async void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.GetString("WorkbenchUnsavedTitle"),
            Content = Strings.GetString("WorkbenchUnsavedMessage"),
            PrimaryButtonText = Strings.GetString("WorkbenchDiscard"),
            CloseButtonText = Strings.GetString("WorkbenchKeepEditing"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _resultPending = false;
        _saveFailed = false;
        await _workbench.DiscardChangesAsync();
        RefreshSaveBar();
    }

    private void OnDismissResultClick(object sender, RoutedEventArgs e)
    {
        _resultPending = false;
        RefreshSaveBar();
    }

    private void RefreshSaveBar()
    {
        bool dirty = _workbench.IsDirty;
        bool failed = _saveFailed;
        if (!dirty && !failed && !_saving && !_resultPending)
        {
            SaveBar.Visibility = Visibility.Collapsed;
            return;
        }

        SaveBar.Visibility = Visibility.Visible;
        bool editable = !_saving && (dirty || failed);
        SaveBarDiscardButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveBarSaveButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveBarDismissButton.Visibility =
            !editable && _resultPending ? Visibility.Visible : Visibility.Collapsed;

        if (_saving)
        {
            Announce(SavingGlyph, "StatusNeutralBrush", Strings.GetString("WorkspaceSaveBarSaving"));
            return;
        }

        if (failed)
        {
            Announce(
                ErrorGlyph,
                "StatusCriticalBrush",
                $"{Strings.GetString("WorkbenchSaveErrorTitle")} · {_workbench.ErrorMessage}");
            return;
        }

        if (dirty)
        {
            Announce(UnsavedGlyph, "StatusWarningBrush", Strings.GetString("WorkbenchUnsavedStatus"));
            return;
        }

        // The apply state separates "stored for the next run" from "the running target already took
        // it" — the difference between the user having to restart and not.
        string applyDetail = Strings.GetString($"WorkbenchApply{_workbench.ApplyState}");
        string success = Strings.GetString("WorkbenchSaveSuccessTitle");
        Announce(
            SavedGlyph,
            "StatusSuccessBrush",
            string.IsNullOrEmpty(applyDetail) ? success : $"{success} · {applyDetail}");
    }

    private void Announce(string glyph, string brushKey, string message)
    {
        SaveBarGlyph.Glyph = glyph;
        SaveBarGlyph.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current
            .Resources[brushKey];
        SaveBarStatusText.Text = message;
        AutomationProperties.SetName(SaveBar, message);
    }
}
