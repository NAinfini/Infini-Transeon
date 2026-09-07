using System.Text.Json;
using InfiniTranseon.App.Presentation;
using InfiniTranseon.Contracts.Translation;
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
    // Resolved per lookup so a UI language change takes effect without restarting; see AppStrings.
    private static ResourceLoader Strings => Localization.AppStrings.Loader;

    public static async Task<bool> ShowAsync(
        XamlRoot xamlRoot,
        ProviderRow provider,
        ISecretReferenceService secrets,
        ISettingsService settings,
        IRuntimeControlService runtime)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);

        ApplicationSettings current = await settings.GetSettingsAsync();

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
            Text = Strings.GetString(
                provider.DefaultEndpoint is not null || provider.DefaultModel is not null ||
                provider.RequiresEndpoint
                    ? "ProviderConfigurationPrivacy"
                    : "ProviderCredentialsPrivacy"),
            TextWrapping = TextWrapping.Wrap,
            Style = Application.Current.Resources["CaptionTextStyle"] as Style,
        });
        content.Children.Add(error);

        TextBox? endpointInput = null;
        if (provider.DefaultEndpoint is not null || provider.RequiresEndpoint)
        {
            endpointInput = new TextBox
            {
                Header = Strings.GetString("ProviderEndpointLabel"),
                Text = provider.Endpoint ?? string.Empty,
                PlaceholderText = provider.EndpointPlaceholder,
                IsReadOnly = !provider.CanOverrideEndpoint && !provider.RequiresEndpoint,
                MaxLength = 2048,
            };
            AutomationProperties.SetName(
                endpointInput,
                Strings.GetString("ProviderEndpointLabel"));
            content.Children.Add(endpointInput);
            content.Children.Add(new TextBlock
            {
                Text = Strings.GetString("ProviderEndpointCompatibilityHint"),
                TextWrapping = TextWrapping.Wrap,
                Style = Application.Current.Resources["CaptionTextStyle"] as Style,
            });
        }

        TextBox? modelInput = null;
        if (provider.DefaultModel is not null)
        {
            modelInput = new TextBox
            {
                Header = Strings.GetString("ProviderModelLabel"),
                Text = provider.Model ?? provider.DefaultModel,
                IsReadOnly = !provider.CanOverrideModel,
                MaxLength = 256,
            };
            AutomationProperties.SetName(
                modelInput,
                Strings.GetString("ProviderModelLabel"));
            content.Children.Add(modelInput);
            content.Children.Add(new TextBlock
            {
                Text = Strings.GetString("ProviderModelHint"),
                TextWrapping = TextWrapping.Wrap,
                Style = Application.Current.Resources["CaptionTextStyle"] as Style,
            });
        }

        ComboBox? reasoningEffortInput = provider.CanOverrideReasoningEffort
            ? AddReasoningEffortInput(content, provider.ReasoningEffort)
            : null;

        if (provider.DefaultEndpoint is not null || provider.DefaultModel is not null ||
            reasoningEffortInput is not null)
        {
            var restoreDefaults = new Button
            {
                Content = Strings.GetString("ProviderRestoreDefaults"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(
                restoreDefaults,
                Strings.GetString("ProviderRestoreDefaults"));
            restoreDefaults.Click += (_, _) =>
            {
                if (endpointInput is { IsReadOnly: false } && provider.DefaultEndpoint is not null)
                    endpointInput.Text = provider.DefaultEndpoint;
                if (modelInput is { IsReadOnly: false } && provider.DefaultModel is not null)
                    modelInput.Text = provider.DefaultModel;
                if (reasoningEffortInput is not null)
                    reasoningEffortInput.SelectedIndex = 0;
            };
            content.Children.Add(restoreDefaults);
        }

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
            IsPrimaryButtonEnabled =
                fields.Count > 0 || endpointInput is not null || modelInput is not null ||
                reasoningEffortInput is not null,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                CredentialInput[] changed = fields
                    .Where(field => !string.IsNullOrWhiteSpace(field.Value))
                    .ToArray();

                var endpoints = new Dictionary<string, string>(
                    current.EffectiveProviderEndpoints,
                    StringComparer.Ordinal);
                Uri? configuredEndpoint = null;
                if (endpointInput is not null)
                {
                    string endpointText = endpointInput.Text.Trim();
                    if (!TryNormalizeEndpoint(endpointText, out Uri normalizedEndpoint))
                    {
                        args.Cancel = true;
                        error.Message = Strings.GetString("ProviderEndpointRequired");
                        error.IsOpen = true;
                        return;
                    }
                    configuredEndpoint = normalizedEndpoint;
                    if (provider.RequiresEndpoint && configuredEndpoint.AbsolutePath != "/")
                    {
                        args.Cancel = true;
                        error.Message = Strings.GetString("ProviderEndpointRequired");
                        error.IsOpen = true;
                        return;
                    }
                    if (provider.DefaultEndpoint is not null &&
                        configuredEndpoint == new Uri(provider.DefaultEndpoint))
                    {
                        endpoints.Remove(provider.Id);
                    }
                    else
                    {
                        endpoints[provider.Id] = configuredEndpoint.AbsoluteUri;
                    }
                }

                var models = new Dictionary<string, string>(
                    current.EffectiveProviderModels,
                    StringComparer.Ordinal);
                if (modelInput is not null)
                {
                    if (!TryNormalizeModel(modelInput.Text, out string model))
                    {
                        args.Cancel = true;
                        error.Message = Strings.GetString("ProviderModelInvalid");
                        error.IsOpen = true;
                        return;
                    }
                    if (string.Equals(model, provider.DefaultModel, StringComparison.Ordinal))
                    {
                        models.Remove(provider.Id);
                    }
                    else
                    {
                        models[provider.Id] = model;
                    }
                }

                var reasoningEfforts = new Dictionary<string, ModelReasoningEffort>(
                    current.EffectiveProviderReasoningEfforts,
                    StringComparer.Ordinal);
                if (reasoningEffortInput is not null)
                {
                    ModelReasoningEffort? reasoningEffort =
                        GetSelectedReasoningEffort(reasoningEffortInput);
                    if (reasoningEffort is null)
                        reasoningEfforts.Remove(provider.Id);
                    else
                        reasoningEfforts[provider.Id] = reasoningEffort.Value;
                }

                bool endpointOriginChanged = configuredEndpoint is not null &&
                    provider.Endpoint is not null &&
                    !SameOrigin(new Uri(provider.Endpoint), configuredEndpoint);
                if (endpointOriginChanged && fields.Any(field =>
                        field.Metadata.IsPresent && string.IsNullOrWhiteSpace(field.Value)))
                {
                    args.Cancel = true;
                    error.Message = Strings.GetString("ProviderCredentialReentryRequired");
                    error.IsOpen = true;
                    return;
                }

                bool settingsChanged =
                    !DictionaryEqual(current.EffectiveProviderEndpoints, endpoints) ||
                    !DictionaryEqual(current.EffectiveProviderModels, models) ||
                    !DictionaryEqual(
                        current.EffectiveProviderReasoningEfforts,
                        reasoningEfforts);
                if (!settingsChanged && changed.Length == 0)
                {
                    args.Cancel = true;
                    error.Message = Strings.GetString("ProviderCredentialsNoChanges");
                    error.IsOpen = true;
                    return;
                }

                if (settingsChanged)
                {
                    current = current with
                    {
                        ProviderEndpoints = endpoints,
                        ProviderModels = models,
                        ProviderReasoningEfforts = reasoningEfforts,
                    };
                    await settings.UpdateAsync(current);
                }
                foreach (CredentialInput field in changed)
                {
                    await secrets.SetSecretAsync(
                        provider.Id,
                        field.Metadata.ReferenceId,
                        field.Value!);
                }
                if (settingsChanged) await runtime.ApplySettingsAsync();
                foreach (CredentialInput field in fields) field.Clear();
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
        return saved;
    }

    internal static bool TryNormalizeEndpoint(string text, out Uri endpoint)
    {
        if (text.Length is 0 or > 2048 ||
            !Uri.TryCreate(text, UriKind.Absolute, out Uri? candidate) ||
            candidate is null ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(candidate.IdnHost) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            endpoint = null!;
            return false;
        }
        endpoint = candidate;
        return true;
    }

    internal static bool TryNormalizeModel(string text, out string model)
    {
        model = text.Trim();
        return model.Length is > 0 and <= 256 && !model.Any(char.IsControl);
    }

    internal static ComboBox AddReasoningEffortInput(
        StackPanel content,
        ModelReasoningEffort? selected)
    {
        ReasoningEffortChoice[] choices =
        [
            new(Strings.GetString("ProviderReasoningDefault"), null),
            new(Strings.GetString("ProviderReasoningLow"), ModelReasoningEffort.Low),
            new(Strings.GetString("ProviderReasoningMedium"), ModelReasoningEffort.Medium),
            new(Strings.GetString("ProviderReasoningHigh"), ModelReasoningEffort.High),
        ];
        var input = new ComboBox
        {
            Header = Strings.GetString("ProviderReasoningEffortLabel"),
            ItemsSource = choices,
            DisplayMemberPath = nameof(ReasoningEffortChoice.Label),
            SelectedIndex = Array.FindIndex(choices, choice => choice.Value == selected),
        };
        if (input.SelectedIndex < 0) input.SelectedIndex = 0;
        AutomationProperties.SetName(
            input,
            Strings.GetString("ProviderReasoningEffortLabel"));
        content.Children.Add(input);
        content.Children.Add(new TextBlock
        {
            Text = Strings.GetString("ProviderReasoningEffortHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = Application.Current.Resources["CaptionTextStyle"] as Style,
        });
        return input;
    }

    internal static ModelReasoningEffort? GetSelectedReasoningEffort(ComboBox input) =>
        (input.SelectedItem as ReasoningEffortChoice)?.Value;

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out string? value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, ModelReasoningEffort> left,
        IReadOnlyDictionary<string, ModelReasoningEffort> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out ModelReasoningEffort value) &&
            pair.Value == value);

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

    private sealed record ReasoningEffortChoice(
        string Label,
        ModelReasoningEffort? Value);
}
