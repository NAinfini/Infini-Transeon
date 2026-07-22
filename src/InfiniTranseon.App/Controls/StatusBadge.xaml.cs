using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InfiniTranseon.App.Controls;

/// <summary>Text-plus-icon status chip; never communicates state by color alone.</summary>
public sealed partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(StatusSeverity), typeof(StatusBadge), new PropertyMetadata(StatusSeverity.Neutral, OnVisualChanged));

    public StatusBadge()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusSeverity Severity
    {
        get => (StatusSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatusBadge)d).UpdateVisual();

    private void UpdateVisual()
    {
        var (fgKey, bgKey, glyph) = Severity switch
        {
            StatusSeverity.Success => ("StatusSuccessBrush", "StatusSuccessBackgroundBrush", ""),
            StatusSeverity.Warning => ("StatusWarningBrush", "StatusWarningBackgroundBrush", ""),
            StatusSeverity.Critical => ("StatusCriticalBrush", "StatusCriticalBackgroundBrush", ""),
            StatusSeverity.Info => ("StatusInfoBrush", "StatusInfoBackgroundBrush", ""),
            _ => ("StatusNeutralBrush", "StatusNeutralBackgroundBrush", ""),
        };

        var fg = (Brush)Application.Current.Resources[fgKey];
        Label.Text = Text;
        Label.Foreground = fg;
        Glyph.Glyph = glyph;
        Glyph.Foreground = fg;
        Root.Background = (Brush)Application.Current.Resources[bgKey];
        AutomationProperties.SetName(this, Text);
    }
}
