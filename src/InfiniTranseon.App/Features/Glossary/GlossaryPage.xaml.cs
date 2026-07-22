using System.Collections.ObjectModel;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.Glossary;

public sealed partial class GlossaryPage : Page
{
    private static readonly ResourceLoader Strings = new();

    public GlossaryPage()
    {
        ViewModel = App.GetService<GlossaryViewModel>();
        InitializeComponent();
    }

    public GlossaryViewModel ViewModel { get; }

    public ObservableCollection<GlossaryEntry> Entries => ViewModel.Entries;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();

    private async void OnAddTermClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasActiveProfile)
        {
            return;
        }

        var source = new TextBox { Header = Strings.GetString("GlossaryFieldSource") };
        var target = new TextBox { Header = Strings.GetString("GlossaryFieldTarget") };
        var notes = new TextBox { Header = Strings.GetString("GlossaryFieldNotes") };
        var isProtected = new CheckBox { Content = Strings.GetString("GlossaryFieldProtected") };
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
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(source.Text) || string.IsNullOrWhiteSpace(target.Text))
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

    private async void OnDeleteTermClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sourceTerm } && !string.IsNullOrWhiteSpace(sourceTerm))
        {
            await ViewModel.RemoveAsync(sourceTerm);
        }
    }
}
