using CommunityToolkit.Mvvm.ComponentModel;

namespace InfiniTranseon.App.Presentation.ViewModels;

/// <summary>
/// Shared async-load scaffolding for data-driven page view models. Pages call
/// <see cref="InitializeAsync"/> from their Loaded handler; the base surfaces explicit IsLoading /
/// HasError / ErrorMessage / IsEmpty states so failures render as an inline error with the real
/// message rather than being swallowed or blocking on a synchronous repository call.
/// </summary>
public abstract class PageViewModelBase : ObservableObject
{
    private bool _isLoading;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private bool _isEmpty;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        protected set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>Loads the page's data, resolving its own IsLoading/HasError/ErrorMessage state.</summary>
    public abstract Task InitializeAsync(CancellationToken cancellationToken = default);

    // Runs a load/mutation body, capturing any failure as an explicit inline error. Exceptions are
    // recorded, not rethrown, so the UI can show ErrorMessage; callers that need the failure can check
    // HasError afterwards.
    protected async Task RunGuardedAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            HasError = true;
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
