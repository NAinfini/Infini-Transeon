using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Controls;

/// <summary>
/// InfoBar(Severity=Error) wrapper with an optional retry command and collapsible, selectable
/// details text, so page-level error states share one implementation and localization set.
/// </summary>
public sealed partial class ErrorBar : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(ErrorBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ErrorBar), new PropertyMetadata(false));

    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(ErrorBar), new PropertyMetadata(null, OnRetryCommandChanged));

    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(
        nameof(Details), typeof(string), typeof(ErrorBar), new PropertyMetadata(string.Empty, OnDetailsChanged));

    public ErrorBar()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public string Details
    {
        get => (string)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    private static void OnRetryCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ErrorBar)d).RetryButton.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;

    private static void OnDetailsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ErrorBar)d;
        bool hasDetails = !string.IsNullOrEmpty((string?)e.NewValue);
        self.DetailsToggle.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
        if (!hasDetails)
        {
            self.DetailsToggle.IsChecked = false;
            self.DetailsText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (RetryCommand?.CanExecute(null) == true)
        {
            RetryCommand.Execute(null);
        }
    }

    private void OnDetailsToggleClick(object sender, RoutedEventArgs e)
        => DetailsText.Visibility = DetailsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
}
