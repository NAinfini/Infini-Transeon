using InfiniTranseon.App.DesignData;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Glossary;

public sealed partial class GlossaryPage : Page
{
    public GlossaryPage()
    {
        InitializeComponent();
    }

    public IReadOnlyList<GlossaryEntry> Entries => SampleData.GlossaryEntries;
}
