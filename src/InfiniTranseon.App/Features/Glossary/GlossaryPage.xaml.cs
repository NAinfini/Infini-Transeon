using System.Collections.ObjectModel;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using InfiniTranseon.App.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InfiniTranseon.App.Features.Glossary;

public sealed partial class GlossaryPage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");
    private bool _loadingPrompt;
    private Guid _profileId;
    private string? _editingSourceTerm;
    private readonly WorkbenchViewModel _workbench;
    private readonly AppNavigationState _navigation;

    public GlossaryPage()
    {
        ViewModel = App.GetService<GlossaryViewModel>();
        _workbench = App.GetService<WorkbenchViewModel>();
        _navigation = App.GetService<AppNavigationState>();
        InitializeComponent();
    }

    public GlossaryViewModel ViewModel { get; }

    public ObservableCollection<GlossaryEntry> Entries => ViewModel.Entries;

    // A profile with entries, or currently loading, never shows the table and the empty state at the
    // same time: the table only appears once a profile is active and it actually has entries.
    private Visibility TableVisibility(bool hasActiveProfile, bool isEmpty) =>
        hasActiveProfile && !isEmpty ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is Guid profileId && profileId != Guid.Empty)
        {
            _profileId = profileId;
            ViewModel.SelectProfile(profileId);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loadingPrompt = true;
        try
        {
            await ViewModel.InitializeAsync();
            if (_profileId != Guid.Empty)
            {
                await _workbench.EnsureLoadedAsync(_profileId);
                ContextSourceLanguageBox.Text = _workbench.SourceLanguage;
                ContextTargetLanguageBox.Text = _workbench.TargetLanguage;
                ContextGameNameBox.Text = _workbench.GameName;
                ContextGameDescriptionBox.Text = _workbench.GameDescription;
                ContextRecentLinesBox.Value = _workbench.RecentLineCount;
            }
            RefreshPromptEditor();
        }
        finally
        {
            _loadingPrompt = false;
        }
    }

    private async void OnAddTermClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasActiveProfile)
        {
            return;
        }
        await AddTermAsync();
    }

    private async Task AddTermAsync()
    {
        var source = new TextBox
        {
            Header = Strings.GetString("GlossaryFieldSource"),
        };
        var target = new TextBox
        {
            Header = Strings.GetString("GlossaryFieldTarget"),
        };
        var notes = new TextBox
        {
            Header = Strings.GetString("GlossaryFieldNotes"),
        };
        var isProtected = new CheckBox
        {
            Content = Strings.GetString("GlossaryFieldProtected"),
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(source);
        panel.Children.Add(target);
        panel.Children.Add(notes);
        panel.Children.Add(isProtected);

        var dialog = new ContentDialog
        {
            Title = Strings.GetString("AddTermDialogTitle"),
            Content = panel,
            PrimaryButtonText = Strings.GetString("AddTermDialogSave"),
            CloseButtonText = Strings.GetString("AddTermDialogCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
        };

        // Blocking the save button beats accepting the click and dropping the term: an empty pair
        // used to close the dialog and add nothing, which reads exactly like a successful save.
        void UpdateSaveEnabled(object _, TextChangedEventArgs __) =>
            dialog.IsPrimaryButtonEnabled =
                !string.IsNullOrWhiteSpace(source.Text) &&
                !string.IsNullOrWhiteSpace(target.Text);
        source.TextChanged += UpdateSaveEnabled;
        target.TextChanged += UpdateSaveEnabled;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var entry = new GlossaryEntry(
            source.Text.Trim(),
            target.Text.Trim(),
            $"Profile · {ViewModel.ActiveProfileName}",
            CaseSensitive: false,
            Protected: isProtected.IsChecked == true,
            notes.Text?.Trim() ?? string.Empty);
        await ViewModel.AddOrUpdateAsync(entry, replacingSourceTerm: null);
    }

    // Inline edit (audited defect fix): clicking a row's edit button no longer opens a modal dialog.
    // It reveals the shared edit panel pre-filled with that row's data; saving reuses AddOrUpdateAsync
    // with the original source term as replacingSourceTerm, so a real rename removes the old entry
    // instead of leaving a duplicate behind.
    private void OnEditTermClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GlossaryEntry entry })
        {
            return;
        }

        _editingSourceTerm = entry.SourceTerm;
        GlossaryEditSourceBox.Text = entry.SourceTerm;
        GlossaryEditTargetBox.Text = entry.TargetTerm;
        GlossaryEditNotesBox.Text = entry.Notes;
        GlossaryEditProtectedCheck.IsChecked = entry.Protected;
        GlossaryEditStatusBar.IsOpen = false;
        GlossaryEditPanel.Visibility = Visibility.Visible;
        GlossaryEditSourceBox.Focus(FocusState.Programmatic);
    }

    private void OnCancelGlossaryEditClick(object sender, RoutedEventArgs e)
    {
        _editingSourceTerm = null;
        GlossaryEditPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnSaveGlossaryEditClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GlossaryEditSourceBox.Text) ||
            string.IsNullOrWhiteSpace(GlossaryEditTargetBox.Text))
        {
            GlossaryEditStatusBar.Severity = InfoBarSeverity.Error;
            GlossaryEditStatusBar.Title = Strings.GetString("GlossaryEditRequiredError");
            GlossaryEditStatusBar.IsOpen = true;
            return;
        }

        var entry = new GlossaryEntry(
            GlossaryEditSourceBox.Text.Trim(),
            GlossaryEditTargetBox.Text.Trim(),
            $"Profile · {ViewModel.ActiveProfileName}",
            CaseSensitive: false,
            Protected: GlossaryEditProtectedCheck.IsChecked == true,
            GlossaryEditNotesBox.Text?.Trim() ?? string.Empty);
        try
        {
            await ViewModel.AddOrUpdateAsync(entry, replacingSourceTerm: _editingSourceTerm);
            _editingSourceTerm = null;
            GlossaryEditPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            GlossaryEditStatusBar.Severity = InfoBarSeverity.Error;
            GlossaryEditStatusBar.Title = exception.Message;
            GlossaryEditStatusBar.IsOpen = true;
        }
    }

    private void OnPromoteFromHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_profileId != Guid.Empty)
        {
            _navigation.NavigateToProfile(_profileId, WorkspaceSection.History);
        }
    }

    private void OnGameContextTextChanged(object sender, TextChangedEventArgs e) =>
        PushGameContextEdit();

    private void OnGameContextValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        PushGameContextEdit();

    /// <summary>
    /// Mirrors the context card into the shared workbench draft on every keystroke. The card is not
    /// data-bound, so without this the draft would hold stale text: leaving the section would drop the
    /// edits without the unsaved-changes guard ever seeing them, and a save triggered from anywhere
    /// else would silently write the old values over what the user is looking at.
    /// </summary>
    private void PushGameContextEdit()
    {
        if (_loadingPrompt || _profileId == Guid.Empty)
            return;
        _workbench.SourceLanguage = ContextSourceLanguageBox.Text.Trim();
        _workbench.TargetLanguage = ContextTargetLanguageBox.Text.Trim();
        _workbench.GameName = ContextGameNameBox.Text.Trim();
        _workbench.GameDescription = ContextGameDescriptionBox.Text.Trim();
        _workbench.RecentLineCount = double.IsNaN(ContextRecentLinesBox.Value)
            ? 6
            : checked((int)Math.Round(ContextRecentLinesBox.Value));
        _workbench.MarkEditorChanged();
    }

    private async void OnSaveGameContextClick(object sender, RoutedEventArgs e)
    {
        if (_profileId == Guid.Empty)
            return;
        string source = ContextSourceLanguageBox.Text.Trim();
        string target = ContextTargetLanguageBox.Text.Trim();
        if (source.Length == 0 || target.Length == 0 ||
            string.Equals(source, target, StringComparison.CurrentCultureIgnoreCase))
        {
            // The same rule the creation wizard enforces; saving an invalid pair would produce a
            // profile that silently cannot translate.
            ContextStatusBar.Title = Strings.GetString("GameContextSaveFailedTitle");
            ContextStatusBar.Message = Strings.GetString("ContextLanguagePairInvalid");
            ContextStatusBar.Severity = InfoBarSeverity.Error;
            ContextStatusBar.IsOpen = true;
            return;
        }

        try
        {
            PushGameContextEdit();
            await _workbench.SaveAsync();
            ContextStatusBar.Title = Strings.GetString("GameContextSavedTitle");
            ContextStatusBar.Message = Strings.GetString("GameContextSavedMessage");
            ContextStatusBar.Severity = InfoBarSeverity.Success;
            ContextStatusBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ContextStatusBar.Title = Strings.GetString("GameContextSaveFailedTitle");
            ContextStatusBar.Message = exception.Message;
            ContextStatusBar.Severity = InfoBarSeverity.Error;
            ContextStatusBar.IsOpen = true;
        }
    }

    private async void OnDeleteTermClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string sourceTerm } ||
            string.IsNullOrWhiteSpace(sourceTerm))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Strings.GetString("DeleteTermDialogTitle"),
            Content = string.Format(
                Strings.GetString("DeleteTermDialogBody"),
                sourceTerm),
            PrimaryButtonText = Strings.GetString("DeleteTermDialogConfirm"),
            CloseButtonText = Strings.GetString("DeleteTermDialogCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveAsync(sourceTerm);
        }
    }

    private void OnGlossarySearchChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            ViewModel.ApplyFilter(sender.Text);
    }

    private async void OnExportGlossaryClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = "InfiniTranseon-glossary" };
        picker.FileTypeChoices.Add(
            Strings.GetString("GlossaryArchiveFileType"),
            [".itrglossary"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await using Stream stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, new GlossaryTransferDocument(
                SchemaVersion: 1,
                Entries.ToArray()));
            await stream.FlushAsync();
        }
        catch (Exception exception)
        {
            await ShowTransferErrorAsync(exception.Message);
        }
    }

    private async void OnImportGlossaryClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".itrglossary");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            if ((await file.GetBasicPropertiesAsync()).Size > 5 * 1024 * 1024)
                throw new InvalidDataException(Strings.GetString("GlossaryArchiveTooLarge"));
            await using Stream stream = await file.OpenStreamForReadAsync();
            GlossaryTransferDocument document =
                await JsonSerializer.DeserializeAsync<GlossaryTransferDocument>(stream) ??
                throw new InvalidDataException(Strings.GetString("GlossaryArchiveInvalid"));
            if (document.SchemaVersion != 1 || document.Entries.Count > 10_000)
                throw new InvalidDataException(Strings.GetString("GlossaryArchiveInvalid"));
            foreach (GlossaryEntry entry in document.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.SourceTerm) ||
                    string.IsNullOrWhiteSpace(entry.TargetTerm) ||
                    entry.SourceTerm.Length > 1024 ||
                    entry.TargetTerm.Length > 4096 ||
                    entry.Notes.Length > 4096)
                    throw new InvalidDataException(Strings.GetString("GlossaryArchiveInvalid"));
            }
            await ViewModel.ImportAsync(document.Entries);
        }
        catch (Exception exception)
        {
            await ShowTransferErrorAsync(exception.Message);
        }
    }

    private async Task ShowTransferErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.GetString("GlossaryTransferErrorTitle"),
            Content = message,
            CloseButtonText = Strings.GetString("GlossaryTransferErrorClose"),
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnStylePromptVersionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        RefreshPromptEditor();
        if (_loadingPrompt ||
            ViewModel.SelectedStylePromptVersion is not { } selected ||
            selected.Version == ViewModel.ActiveStylePromptVersion)
        {
            return;
        }

        _loadingPrompt = true;
        try
        {
            await ViewModel.ActivateStylePromptVersionAsync(selected.Version);
            RefreshPromptEditor();
            // Transient notice (audited noise defect fix): only shown right after an explicit switch,
            // never left open by default.
            StylePromptCacheWarning.IsOpen = true;
        }
        finally
        {
            _loadingPrompt = false;
        }
    }

    private async void OnSavePromptVersionClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(StylePromptNameBox.Text) ||
            string.IsNullOrWhiteSpace(StylePromptEditor.Text))
        {
            await ShowTransferErrorAsync(Strings.GetString("StylePromptRequiredError"));
            return;
        }

        _loadingPrompt = true;
        try
        {
            await ViewModel.SaveStylePromptVersionAsync(
                StylePromptNameBox.Text,
                StylePromptEditor.Text);
            RefreshPromptEditor();
            // Transient notice (audited noise defect fix): only shown right after an explicit save.
            StylePromptCacheWarning.IsOpen = true;
        }
        finally
        {
            _loadingPrompt = false;
        }
    }

    private void RefreshPromptEditor()
    {
        StylePromptVersion? selected = ViewModel.SelectedStylePromptVersion;
        StylePromptNameBox.Text = selected?.Name ?? string.Empty;
        StylePromptEditor.Text = selected?.Template ?? string.Empty;
    }

    private sealed record GlossaryTransferDocument(
        int SchemaVersion,
        IReadOnlyList<GlossaryEntry> Entries);
}
