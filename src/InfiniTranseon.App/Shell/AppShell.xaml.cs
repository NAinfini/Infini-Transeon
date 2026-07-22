using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace InfiniTranseon.App.Shell;

public sealed partial class AppShell : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["profiles"] = typeof(Features.ProfileCenter.ProfileCenterPage),
        ["running"] = typeof(Features.RuntimeControls.RunningTargetsPage),
        ["history"] = typeof(Features.History.HistoryPage),
        ["services"] = typeof(Features.Settings.ServicesModelsPage),
        ["glossary"] = typeof(Features.Glossary.GlossaryPage),
        ["overlaystyles"] = typeof(Features.OverlayStyles.OverlayStylePage),
        ["workbench"] = typeof(Features.Workbench.OpticalWorkbenchPage),
        ["diagnostics"] = typeof(Features.Diagnostics.DiagnosticsPage),
        ["settings"] = typeof(Features.Settings.SettingsPage),
    };

    public AppShell()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        ContentFrame.Navigate(typeof(Features.ProfileCenter.ProfileCenterPage));
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }
            && Pages.TryGetValue(tag, out var pageType)
            && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }
    }
}
