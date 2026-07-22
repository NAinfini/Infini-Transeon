using InfiniTranseon.App.DesignData;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Features.ProfileCenter;

public sealed partial class ProfileCenterPage : Page
{
    public ProfileCenterPage()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ProfileCard> Profiles => SampleData.Profiles;
}
