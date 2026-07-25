using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Controls;

/// <summary>
/// Shared page frame with mutually exclusive loading, error, empty, and content states.
/// </summary>
public sealed partial class PageShell : UserControl
{
    public static readonly DependencyProperty TitleProperty = Register<string>(nameof(Title), string.Empty, OnTextChanged);
    public static readonly DependencyProperty SubtitleProperty = Register<string>(nameof(Subtitle), string.Empty, OnTextChanged);
    public static readonly DependencyProperty HeaderCommandsProperty = Register<object?>(nameof(HeaderCommands), null);
    public static readonly DependencyProperty PageContentProperty = Register<object?>(nameof(PageContent), null);
    public static readonly DependencyProperty IsLoadingProperty = Register(nameof(IsLoading), false, OnStateChanged);
    public static readonly DependencyProperty ErrorMessageProperty = Register<string>(nameof(ErrorMessage), string.Empty, OnStateChanged);
    public static readonly DependencyProperty ErrorDetailsProperty = Register<string>(nameof(ErrorDetails), string.Empty);
    public static readonly DependencyProperty RetryCommandProperty = Register<ICommand?>(nameof(RetryCommand), null);
    public static readonly DependencyProperty IsEmptyProperty = Register(nameof(IsEmpty), false, OnStateChanged);
    public static readonly DependencyProperty EmptyGlyphProperty = Register<string>(nameof(EmptyGlyph), string.Empty);
    public static readonly DependencyProperty EmptyTitleProperty = Register<string>(nameof(EmptyTitle), string.Empty);
    public static readonly DependencyProperty EmptyBodyProperty = Register<string>(nameof(EmptyBody), string.Empty);
    public static readonly DependencyProperty EmptyActionProperty = Register<object?>(nameof(EmptyAction), null);

    public PageShell()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? HeaderCommands { get => GetValue(HeaderCommandsProperty); set => SetValue(HeaderCommandsProperty, value); }
    public object? PageContent { get => GetValue(PageContentProperty); set => SetValue(PageContentProperty, value); }
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public string ErrorMessage { get => (string)GetValue(ErrorMessageProperty); set => SetValue(ErrorMessageProperty, value); }
    public string ErrorDetails { get => (string)GetValue(ErrorDetailsProperty); set => SetValue(ErrorDetailsProperty, value); }
    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }
    public bool IsEmpty { get => (bool)GetValue(IsEmptyProperty); set => SetValue(IsEmptyProperty, value); }
    public string EmptyGlyph { get => (string)GetValue(EmptyGlyphProperty); set => SetValue(EmptyGlyphProperty, value); }
    public string EmptyTitle { get => (string)GetValue(EmptyTitleProperty); set => SetValue(EmptyTitleProperty, value); }
    public string EmptyBody { get => (string)GetValue(EmptyBodyProperty); set => SetValue(EmptyBodyProperty, value); }
    public object? EmptyAction { get => GetValue(EmptyActionProperty); set => SetValue(EmptyActionProperty, value); }

    public Visibility SubtitleVisibility =>
        string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

    private static DependencyProperty Register<T>(
        string name,
        T defaultValue,
        PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(PageShell), new PropertyMetadata(defaultValue, changed));

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((PageShell)sender).Bindings.Update();

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((PageShell)sender).UpdateVisualState();

    private void UpdateVisualState()
    {
        if (LoadingState is null)
        {
            return;
        }

        bool hasError = !string.IsNullOrWhiteSpace(ErrorMessage);
        LoadingState.Visibility = IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ErrorState.Visibility = !IsLoading && hasError ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateHost.Visibility = !IsLoading && !hasError && IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        ContentState.Visibility = !IsLoading && !hasError && !IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        Bindings.Update();
    }
}
