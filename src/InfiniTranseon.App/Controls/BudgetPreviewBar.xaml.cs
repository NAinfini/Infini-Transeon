using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Controls;

public sealed partial class BudgetPreviewBar : UserControl
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    public static readonly DependencyProperty UsedChannelsProperty = DependencyProperty.Register(
        nameof(UsedChannels),
        typeof(int),
        typeof(BudgetPreviewBar),
        new PropertyMetadata(0, OnBudgetChanged));
    public static readonly DependencyProperty MaximumChannelsProperty = DependencyProperty.Register(
        nameof(MaximumChannels),
        typeof(int),
        typeof(BudgetPreviewBar),
        new PropertyMetadata(4, OnBudgetChanged));
    public static readonly DependencyProperty WorstCaseRequestsProperty = DependencyProperty.Register(
        nameof(WorstCaseRequests),
        typeof(int),
        typeof(BudgetPreviewBar),
        new PropertyMetadata(0, OnBudgetChanged));

    public BudgetPreviewBar()
    {
        InitializeComponent();
        UpdateText();
    }

    public int UsedChannels
    {
        get => (int)GetValue(UsedChannelsProperty);
        set => SetValue(UsedChannelsProperty, value);
    }

    public int MaximumChannels
    {
        get => (int)GetValue(MaximumChannelsProperty);
        set => SetValue(MaximumChannelsProperty, value);
    }

    public int WorstCaseRequests
    {
        get => (int)GetValue(WorstCaseRequestsProperty);
        set => SetValue(WorstCaseRequestsProperty, value);
    }

    private static void OnBudgetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((BudgetPreviewBar)sender).UpdateText();

    private void UpdateText()
    {
        if (SummaryText is null)
        {
            return;
        }

        SummaryText.Text = string.Format(
            Strings.GetString("BudgetPreviewSummary"),
            UsedChannels,
            MaximumChannels,
            WorstCaseRequests);
    }
}
