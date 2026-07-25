using System.Text.Json;
using InfiniTranseon.App.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InfiniTranseon.App.Features.Settings;

internal static class ProviderCredentialDialog
{
    private const int MaximumServiceAccountBytes = 256 * 1024;
    private static readonly ResourceLoader Strings = new(
        ResourceLoader.GetDefaultResourceFilePath(),
        "Resources");

    public static async Task<bool> ShowAsync(
        XamlRoot xamlRoot,
        ProviderRow provider,
        ISecretReferenceService secrets)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(secrets);

        var fields = new List<CredentialInput>();
        var error = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = Strings.GetString("ProviderCredentialsPrivacy"),
            TextWrapping = TextWrapping.Wrap,
            Style = Application.Current.Resources["CaptionTextStyle"] as Style,
        });
        content.Children.Add(error);

        foreach (ProviderCredentialField field in provider.Credentials)
        {
            var model = new CredentialInput(field);
            if (field.ReferenceId.EndsWith(".service-account-json", StringComparison.Ordinal))
            {
                AddServiceAccountPicker(content, model, error);
            }
            else
            {
                var input = new PasswordBox
                {
                    Header = field.DisplayName,
                    PlaceholderText = field.IsPresent
                        ? Strings.GetString("ProviderCredentialKeepPlaceholder")
                        : Strings.GetString("ProviderCredentialRequiredPlaceholder"),
                    PasswordRevealMode = PasswordRevealMode.Peek,
                };
                AutomationProperties.SetName(input, field.DisplayName);
                content.Children.Add(input);
                model.PasswordInput = input;
            }
            fields.Add(model);
        }

        bool saved = false;
        var dialog = new ContentDialog
        {
            Title = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Strings.GetString("ProviderCredentialsTitle"),
                provider.Name),
            Content = content,
            PrimaryButtonText = Strings.GetString("ProviderCredentialsSave"),
            CloseButtonText = Strings.GetString("ProviderCredentialsCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            IsPrimaryButtonEnabled = fields.Count > 0,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try
            {
                CredentialInput[] changed = fields
                    .Where(field => !string.IsNullOrWhiteSpace(field.Value))
                    .ToArray();
                if (changed.Length == 0)
                {
                    args.Cancel = true;
                    error.Message = Strings.GetString("ProviderCredentialsNoChanges");
                    error.IsOpen = true;
                    return;
                }

                foreach (CredentialInput field in changed)
                {
                    await secrets.SetSecretAsync(
                        provider.Id,
                        field.Metadata.ReferenceId,
                        field.Value!);
                    field.Clear();
                }
                saved = true;
            }
            catch (Exception exception)
            {
                args.Cancel = true;
                error.Message = exception.Message;
                error.IsOpen = true;
            }
            finally
            {
                foreach (CredentialInput field in fields) field.Clear();
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
        return saved;
    }

    private static void AddServiceAccountPicker(
        Panel content,
        CredentialInput model,
        InfoBar error)
    {
        var status = new TextBlock
        {
            Text = model.Metadata.IsPresent
                ? Strings.GetString("ProviderCredentialKeepPlaceholder")
                : Strings.GetString("ProviderCredentialRequiredPlaceholder"),
            Style = Application.Current.Resources["CaptionTextStyle"] as Style,
        };
        var button = new Button
        {
            Content = Strings.GetString("ProviderCredentialImportServiceAccount"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, model.Metadata.DisplayName);
        button.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".json");
                InitializeWithWindow.Initialize(
                    picker,
                    WindowNative.GetWindowHandle(App.MainWindow));
                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is null) return;
                if ((await file.GetBasicPropertiesAsync()).Size > MaximumServiceAccountBytes)
                    throw new InvalidDataException(
                        Strings.GetString("ProviderCredentialServiceAccountTooLarge"));
                string json = await FileIO.ReadTextAsync(file);
                using JsonDocument document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions { MaxDepth = 8 });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out JsonElement type) ||
                    type.GetString() != "service_account" ||
                    !root.TryGetProperty("project_id", out _) ||
                    !root.TryGetProperty("client_email", out _) ||
                    !root.TryGetProperty("private_key", out _))
                    throw new InvalidDataException(
                        Strings.GetString("ProviderCredentialServiceAccountInvalid"));
                model.ImportedValue = json;
                status.Text = Strings.GetString("ProviderCredentialServiceAccountReady");
                error.IsOpen = false;
            }
            catch (Exception exception)
            {
                error.Message = exception.Message;
                error.IsOpen = true;
            }
        };
        var group = new StackPanel { Spacing = 6 };
        group.Children.Add(new TextBlock
        {
            Text = model.Metadata.DisplayName,
            Style = Application.Current.Resources["BodyTextStyle"] as Style,
        });
        group.Children.Add(button);
        group.Children.Add(status);
        content.Children.Add(group);
    }

    private sealed class CredentialInput(ProviderCredentialField metadata)
    {
        public ProviderCredentialField Metadata { get; } = metadata;
        public PasswordBox? PasswordInput { get; set; }
        public string? ImportedValue { get; set; }
        public string? Value => ImportedValue ?? PasswordInput?.Password;

        public void Clear()
        {
            ImportedValue = null;
            if (PasswordInput is not null) PasswordInput.Password = string.Empty;
        }
    }
}
