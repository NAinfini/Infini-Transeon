using InfiniTranseon.App.DesignData;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.Settings;

public sealed partial class ServicesModelsPage : Page
{
    public ServicesModelsPage()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ProviderRow> Providers => SampleData.Providers;
}
