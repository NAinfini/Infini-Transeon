using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniTranseon.App.Controls.Dialogs;

/// <summary>Options for <see cref="DialogService.ConfirmAsync"/>.</summary>
public sealed record ConfirmDialogOptions(
    string Title,
    string Body,
    string ConfirmText,
    string CancelText,
    bool IsDestructive = false);

/// <summary>
/// Creates WinUI dialogs against an injected <see cref="XamlRoot"/>, so every dialog in the app
/// gets consistent focus containment and Esc-to-cancel behavior, and only one can be open at a
/// time. Construct with a provider rather than a fixed XamlRoot because the active window's root
/// can change (or be briefly unavailable) over the app's lifetime.
/// </summary>
public sealed class DialogService
{
    private readonly Func<XamlRoot?> _xamlRootProvider;
    private bool _isDialogOpen;

    public DialogService(Func<XamlRoot?> xamlRootProvider)
    {
        _xamlRootProvider = xamlRootProvider ?? throw new ArgumentNullException(nameof(xamlRootProvider));
    }

    /// <summary>
    /// Shows a generic confirm/cancel dialog. Returns <see langword="false"/> without showing
    /// anything if a dialog is already open or no XamlRoot is currently available.
    /// </summary>
    public async Task<bool> ConfirmAsync(ConfirmDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_isDialogOpen)
        {
            return false;
        }

        XamlRoot? xamlRoot = _xamlRootProvider();
        if (xamlRoot is null)
        {
            return false;
        }

        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = options.Title,
                Content = options.Body,
                PrimaryButtonText = options.ConfirmText,
                CloseButtonText = options.CancelText,
                // Esc always activates the close button (WinUI default). Enter activates
                // DefaultButton: normally the confirm action, but destructive actions get no
                // default button so an accidental Enter cannot trigger them.
                DefaultButton = options.IsDestructive ? ContentDialogButton.None : ContentDialogButton.Primary,
            };

            if (options.IsDestructive)
            {
                var criticalStyle = new Style(typeof(Button))
                {
                    BasedOn = (Style)Application.Current.Resources["DefaultButtonStyle"],
                };
                criticalStyle.Setters.Add(
                    new Setter(Control.ForegroundProperty, Application.Current.Resources["StatusCriticalBrush"]));
                dialog.PrimaryButtonStyle = criticalStyle;
            }

            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        finally
        {
            _isDialogOpen = false;
        }
    }
}
