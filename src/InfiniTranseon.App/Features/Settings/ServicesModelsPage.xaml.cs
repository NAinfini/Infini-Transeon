using System.Collections.ObjectModel;
using InfiniTranseon.App.Controls.Dialogs;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InfiniTranseon.App.Features.Settings;

public sealed partial class ServicesModelsPage : Page
{
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    public ServicesModelsPage()
    {
        ViewModel = App.GetService<ServicesModelsViewModel>();
        _settings = App.GetService<ISettingsService>();
        _dialogs = new DialogService(() => XamlRoot);
        InitializeComponent();
    }

    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;
    public ServicesModelsViewModel ViewModel { get; }

    public ObservableCollection<ProviderRow> Providers => ViewModel.Providers;
    public ObservableCollection<ProviderRow> CloudTranslationProviders { get; } = [];
    public ObservableCollection<ProviderRow> CloudOcrProviders { get; } = [];
    public ObservableCollection<ProviderRow> LocalModelProviders { get; } = [];
    public ObservableCollection<ProviderRow> CustomProviders { get; } = [];
    public ObservableCollection<ProviderRow> OtherProviders { get; } = [];
    public string ProvidersTitle => Strings.GetString("ProvidersTitle");
    public string ProvidersSubtitle => Strings.GetString("ProvidersSubtitle");
    public string ProvidersEmptyTitle => Strings.GetString("ProvidersEmptyTitle");
    public string ProvidersEmptyBody => Strings.GetString("ProvidersEmptyBody");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        ApplicationSettings settings = await _settings.GetSettingsAsync();
        OfflineModeBar.IsOpen = settings.StrictOffline;
        RebuildGroups();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.CancelModelOperation();

    private void OnCancelLocalModelOperationClick(object sender, RoutedEventArgs e) =>
        ViewModel.CancelModelOperation();

    private async void OnConfigureProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProviderRow provider })
        {
            return;
        }

        bool saved = await ProviderCredentialDialog.ShowAsync(
            XamlRoot,
            provider,
            App.GetService<ISecretReferenceService>());
        if (saved)
        {
            await ViewModel.InitializeAsync();
            RebuildGroups();
        }
    }

    private async void OnSaveEndpointClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                Tag: ProviderRow { RequiresEndpoint: true } provider,
            })
        {
            return;
        }
        string endpoint = provider.Endpoint?.Trim() ?? string.Empty;
        if (endpoint.Length == 0)
        {
            PageNoticeBar.Title = Strings.GetString("ProviderEndpointRequired");
            PageNoticeBar.Message = provider.Name;
            PageNoticeBar.Severity = InfoBarSeverity.Error;
            PageNoticeBar.IsOpen = true;
            return;
        }
        try
        {
            ApplicationSettings current = await _settings.GetSettingsAsync();
            var endpoints = new Dictionary<string, string>(
                current.EffectiveProviderEndpoints,
                StringComparer.Ordinal)
            {
                [provider.Id] = endpoint,
            };
            await _settings.UpdateAsync(current with { ProviderEndpoints = endpoints });
            await ViewModel.InitializeAsync();
            RebuildGroups();
            PageNoticeBar.Title = Strings.GetString("ProviderEndpointSavedTitle");
            PageNoticeBar.Message = provider.Name;
            PageNoticeBar.Severity = InfoBarSeverity.Success;
            PageNoticeBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            PageNoticeBar.Title = Strings.GetString("ProviderEndpointSaveFailedTitle");
            PageNoticeBar.Message = exception.Message;
            PageNoticeBar.Severity = InfoBarSeverity.Error;
            PageNoticeBar.IsOpen = true;
        }
    }

    private async void OnAddRestAdapterClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await using Stream stream = await file.OpenStreamForReadAsync();
        await ViewModel.ImportRestAdapterAsync(stream);
        if (!ViewModel.HasError)
        {
            await ViewModel.InitializeAsync();
            RebuildGroups();
            PageNoticeBar.Title = Strings.GetString("RestAdapterImportedTitle");
            PageNoticeBar.Message = Strings.GetString("RestAdapterImportedMessage");
            PageNoticeBar.Severity = InfoBarSeverity.Success;
            PageNoticeBar.IsOpen = true;
        }
    }

    private async void OnRemoveCustomProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProviderRow { IsCustom: true } provider })
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogOptions(
            Strings.GetString("RemoveRestAdapterTitle"),
            string.Format(Strings.GetString("RemoveRestAdapterBody"), provider.Name),
            Strings.GetString("RemoveRestAdapterConfirm"),
            Strings.GetString("RemoveRestAdapterCancel"),
            IsDestructive: true));
        if (!confirmed)
        {
            return;
        }

        await ViewModel.RemoveCustomProviderAsync(provider.Id);
        if (!ViewModel.HasError)
        {
            RebuildGroups();
            PageNoticeBar.Title = Strings.GetString("RestAdapterRemovedTitle");
            PageNoticeBar.Message = provider.Name;
            PageNoticeBar.Severity = InfoBarSeverity.Success;
            PageNoticeBar.IsOpen = true;
        }
    }

    private async void OnInstallLocalModelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsModelOperationInProgress ||
            sender is not FrameworkElement
            {
                Tag: ProviderRow
                {
                    CanDownloadModel: true,
                    ModelId: not null,
                    ModelVersion: not null,
                } provider,
            })
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogOptions(
            Strings.GetString("InstallLocalModelTitle"),
            string.Format(
                Strings.GetString("InstallLocalModelBody"),
                provider.Name,
                provider.ModelDownloadSize,
                provider.ModelLicense,
                provider.ModelRuntime),
            Strings.GetString("InstallLocalModelConfirm"),
            Strings.GetString("InstallLocalModelCancel")));
        if (!confirmed)
        {
            return;
        }

        await ViewModel.InstallLocalModelAsync(provider, userApproved: true);
        if (ViewModel.ModelOperationSucceeded)
        {
            RebuildGroups();
            ShowModelNotice(
                Strings.GetString("LocalModelInstalledTitle"),
                provider.Name);
        }
    }

    private async void OnRemoveLocalModelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsModelOperationInProgress ||
            sender is not FrameworkElement
            {
                Tag: ProviderRow
                {
                    CanRemoveModel: true,
                    ModelId: not null,
                    ModelVersion: not null,
                } provider,
            })
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogOptions(
            Strings.GetString("RemoveLocalModelTitle"),
            string.Format(
                Strings.GetString("RemoveLocalModelBody"),
                provider.Name,
                provider.ModelPackageDirectory),
            Strings.GetString("RemoveLocalModelConfirm"),
            Strings.GetString("RemoveLocalModelCancel"),
            IsDestructive: true));
        if (!confirmed)
        {
            return;
        }

        await ViewModel.RemoveLocalModelAsync(provider, userConfirmed: true);
        if (ViewModel.ModelOperationSucceeded)
        {
            RebuildGroups();
            ShowModelNotice(
                Strings.GetString("LocalModelRemovedTitle"),
                provider.Name);
        }
    }

    private void OnGoToPrivacySettingsClick(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SettingsPage), "privacy");

    private void ShowModelNotice(string title, string message)
    {
        PageNoticeBar.Title = title;
        PageNoticeBar.Message = message;
        PageNoticeBar.Severity = InfoBarSeverity.Success;
        PageNoticeBar.IsOpen = true;
    }

    private void OnProviderFilterChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            RebuildGroups();
    }

    private void RebuildGroups()
    {
        string query = ProviderSearchBox.Text.Trim();
        IEnumerable<ProviderRow> matches = Providers.Where(provider =>
            query.Length == 0 ||
            provider.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            provider.Kind.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            provider.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            provider.StateText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        ILookup<ServicesModelsViewModel.ProviderGroup, ProviderRow> grouped =
            matches.ToLookup(ServicesModelsViewModel.ClassifyGroup);
        Replace(CloudTranslationProviders, grouped[ServicesModelsViewModel.ProviderGroup.CloudTranslation]);
        Replace(CloudOcrProviders, grouped[ServicesModelsViewModel.ProviderGroup.CloudOcr]);
        Replace(LocalModelProviders, grouped[ServicesModelsViewModel.ProviderGroup.LocalModel]);
        Replace(CustomProviders, grouped[ServicesModelsViewModel.ProviderGroup.Custom]);
        Replace(OtherProviders, grouped[ServicesModelsViewModel.ProviderGroup.Other]);

        CloudTranslationSection.Visibility = CloudTranslationProviders.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CloudOcrSection.Visibility = CloudOcrProviders.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalModelSection.Visibility = LocalModelProviders.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        OtherProviderSection.Visibility = OtherProviders.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CustomProviderEmptyState.Visibility = Providers.Any(provider => provider.IsCustom)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void Replace(
        ObservableCollection<ProviderRow> destination,
        IEnumerable<ProviderRow> source)
    {
        destination.Clear();
        foreach (ProviderRow provider in source.OrderBy(
            item => item.Name,
            StringComparer.CurrentCultureIgnoreCase))
        {
            destination.Add(provider);
        }
    }
}
