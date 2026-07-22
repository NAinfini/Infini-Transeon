using System;
using Microsoft.UI.Xaml;

namespace InfiniTranseon.App.Shell;

// Local recovery window shown when service composition fails at startup. It exposes the real
// exception detail instead of degrading silently.
public sealed partial class CompositionErrorWindow : Window
{
    public CompositionErrorWindow(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        InitializeComponent();
        DetailsBox.Text = exception.ToString();
    }
}
