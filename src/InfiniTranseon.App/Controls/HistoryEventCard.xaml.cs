using InfiniTranseon.App.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;

namespace InfiniTranseon.App.Controls;

public sealed partial class HistoryEventCard : UserControl
{
    // Resolved per lookup so a UI language change takes effect without restarting; see AppStrings.
    private static ResourceLoader Strings => Localization.AppStrings.Loader;
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item),
        typeof(HistoryEvent),
        typeof(HistoryEventCard),
        new PropertyMetadata(null, OnItemChanged));

    private readonly IHistoryService _history;
    private readonly IGlossaryService _glossary;

    public HistoryEventCard()
    {
        _history = App.GetService<IHistoryService>();
        _glossary = App.GetService<IGlossaryService>();
        InitializeComponent();
    }

    public HistoryEvent Item
    {
        get => (HistoryEvent)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>
    /// Entries written before history recorded the region carry no name, and no later lookup can
    /// recover it. The label is omitted for those rather than filled with a placeholder that reads
    /// like a region called "—".
    /// </summary>
    public Visibility RegionVisibility => string.IsNullOrEmpty(Item?.Region)
        ? Visibility.Collapsed
        : Visibility.Visible;

    private static void OnItemChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((HistoryEventCard)sender).Bindings.Update();

    private void OnBeginCorrectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChannelResult result })
            return;
        CorrectionTextBox.Text = result.Text;
        CorrectionPanel.Visibility = Visibility.Visible;
        CorrectionStatusBar.IsOpen = false;
        CorrectionTextBox.Focus(FocusState.Programmatic);
    }

    private void OnCancelCorrectionClick(object sender, RoutedEventArgs e)
    {
        CorrectionTextBox.Text = string.Empty;
        CorrectionPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnSaveCorrectionClick(object sender, RoutedEventArgs e) =>
        await SaveCorrectionAsync(addToGlossary: false);

    private async void OnSaveCorrectionAndGlossaryClick(object sender, RoutedEventArgs e) =>
        await SaveCorrectionAsync(addToGlossary: true);

    private async Task SaveCorrectionAsync(bool addToGlossary)
    {
        string corrected = CorrectionTextBox.Text.Trim();
        if (corrected.Length == 0)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                Strings.GetString("HistoryCorrectionEmptyTitle"),
                Strings.GetString("HistoryCorrectionEmptyMessage"));
            return;
        }
        try
        {
            if (addToGlossary)
            {
                _glossary.SelectProfile(Item.ProfileId);
                await _glossary.AddOrUpdateAsync(
                    new GlossaryEntry(
                        Item.SourceText,
                        corrected,
                        Strings.GetString("HistoryCorrectionGlossaryScope"),
                        CaseSensitive: false,
                        Protected: false,
                        Notes: Strings.GetString("HistoryCorrectionGlossaryNote")),
                    replacingSourceTerm: Item.SourceText);
            }
            // Corrections belong to the resulting glossary, including a term added here.
            await _history.SaveCorrectionAsync(Item, corrected);
            CorrectionPanel.Visibility = Visibility.Collapsed;
            ShowStatus(
                InfoBarSeverity.Success,
                Strings.GetString(
                    addToGlossary
                        ? "HistoryCorrectionAndGlossarySavedTitle"
                        : "HistoryCorrectionSavedTitle"),
                Strings.GetString("HistoryCorrectionSavedMessage"));
        }
        catch (Exception exception)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                Strings.GetString("HistoryCorrectionSaveFailedTitle"),
                exception.Message);
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        CorrectionStatusBar.Severity = severity;
        CorrectionStatusBar.Title = title;
        CorrectionStatusBar.Message = message;
        CorrectionStatusBar.IsOpen = true;
    }

    private void OnCopyTextClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string text } || text.Length == 0)
            return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
