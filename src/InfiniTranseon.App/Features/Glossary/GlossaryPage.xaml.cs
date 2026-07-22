using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Glossary;

public sealed partial class GlossaryPage : Page
{
    public GlossaryPage()
    {
        ViewModel = App.GetService<GlossaryViewModel>();
        InitializeComponent();
    }

    public GlossaryViewModel ViewModel { get; }

    public IReadOnlyList<GlossaryEntry> Entries => ViewModel.Entries;
}
