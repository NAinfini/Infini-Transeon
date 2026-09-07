using InfiniTranseon.App.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Features.Settings;

internal static class ThirdPartyApiDialog
{
    private static ResourceLoader Strings => Localization.AppStrings.Loader;

    public static async Task<bool> ShowAsync(
        XamlRoot xamlRoot,
        ISettingsService settings,
        ISecretReferenceService secrets,
        IRuntimeControlService runtime)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(runtime);

        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = Strings.GetString("ThirdPartyApiPrivacy"),
            TextWrapping = TextWrapping.Wrap,
            Style = Application.Current.Resources["CaptionTextStyle"] as Style,
        });
        var error = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
        };
        content.Children.Add(error);

        var nameInput = AddTextInput(
            content,
            "ThirdPartyApiNameLabel",
            "ThirdPartyApiNamePlaceholder",
            maximumLength: 80);
        var endpointInput = AddTextInput(
            content,
            "ThirdPartyApiEndpointLabel",
            "ThirdPartyApiEndpointPlaceholder",
            maximumLength: 2048);
        var modelInput = AddTextInput(
            content,
            "ProviderModelLabel",
            "ThirdPartyApiModelPlaceholder",
            maximumLength: 256);
        ComboBox reasoningInput =
            ProviderCredentialDialog.AddReasoningEffortInput(content, selected: null);
        var keyInput = new PasswordBox
        {
            Header = Strings.GetString("ThirdPartyApiKeyLabel"),
            PlaceholderText = Strings.GetString("ProviderCredentialRequiredPlaceholder"),
            PasswordRevealMode = PasswordRevealMode.Peek,
            MaxLength = 8192,
        };
        AutomationProperties.SetName(keyInput, Strings.GetString("ThirdPartyApiKeyLabel"));
        content.Children.Add(keyInput);

        ProviderRow? created = null;
        bool saved = false;
        var dialog = new ContentDialog
        {
            Title = Strings.GetString("ThirdPartyApiTitle"),
            Content = content,
            PrimaryButtonText = Strings.GetString("ThirdPartyApiAdd"),
            CloseButtonText = Strings.GetString("ThirdPartyApiCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                if (created is null)
                {
                    string displayName = nameInput.Text.Trim();
                    if (displayName.Length is 0 or > 80 || displayName.Any(char.IsControl))
                    {
                        ShowError(error, "ThirdPartyApiNameInvalid");
                        args.Cancel = true;
                        return;
                    }
                    if (!ProviderCredentialDialog.TryNormalizeEndpoint(
                            endpointInput.Text.Trim(),
                            out Uri endpoint))
                    {
                        ShowError(error, "ThirdPartyApiEndpointInvalid");
                        args.Cancel = true;
                        return;
                    }
                    if (!ProviderCredentialDialog.TryNormalizeModel(
                            modelInput.Text,
                            out string model))
                    {
                        ShowError(error, "ProviderModelInvalid");
                        args.Cancel = true;
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(keyInput.Password))
                    {
                        ShowError(error, "ThirdPartyApiKeyRequired");
                        args.Cancel = true;
                        return;
                    }

                    created = await settings.AddOpenAiCompatibleProviderAsync(
                        displayName,
                        endpoint,
                        model,
                        ProviderCredentialDialog.GetSelectedReasoningEffort(reasoningInput));
                    nameInput.IsEnabled = false;
                    endpointInput.IsEnabled = false;
                    modelInput.IsEnabled = false;
                    reasoningInput.IsEnabled = false;
                }
                if (string.IsNullOrWhiteSpace(keyInput.Password))
                {
                    ShowError(error, "ThirdPartyApiKeyRequired");
                    args.Cancel = true;
                    return;
                }

                ProviderCredentialField credential = AssertSingleCredential(created);
                await secrets.SetSecretAsync(
                    created.Id,
                    credential.ReferenceId,
                    keyInput.Password);
                await runtime.ApplySettingsAsync();
                keyInput.Password = string.Empty;
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
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
        keyInput.Password = string.Empty;
        return saved;
    }

    private static TextBox AddTextInput(
        Panel content,
        string labelResourceKey,
        string placeholderResourceKey,
        int maximumLength)
    {
        string label = Strings.GetString(labelResourceKey);
        var input = new TextBox
        {
            Header = label,
            PlaceholderText = Strings.GetString(placeholderResourceKey),
            MaxLength = maximumLength,
        };
        AutomationProperties.SetName(input, label);
        content.Children.Add(input);
        return input;
    }

    private static ProviderCredentialField AssertSingleCredential(ProviderRow provider) =>
        provider.Credentials.Count == 1
            ? provider.Credentials[0]
            : throw new InvalidDataException(
                "A custom OpenAI-compatible provider must define exactly one API key.");

    private static void ShowError(InfoBar error, string resourceKey)
    {
        error.Message = Strings.GetString(resourceKey);
        error.IsOpen = true;
    }
}
