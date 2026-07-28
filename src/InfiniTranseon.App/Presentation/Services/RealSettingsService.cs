using CoreSettings = InfiniTranseon.Core.Settings.ApplicationSettings;
using CoreSettingsRepository = InfiniTranseon.Core.Settings.ApplicationSettingsRepository;
using CoreHistoryRetention = InfiniTranseon.Core.Settings.HistoryRetentionPolicy;
using CoreHotkeySetting = InfiniTranseon.Core.Settings.HotkeySetting;
using CoreHotkeyTargetReference = InfiniTranseon.Core.Settings.HotkeyTargetReference;
using CoreOcrBackend = InfiniTranseon.Core.Settings.OcrBackendPreference;
using CorePerformancePreset = InfiniTranseon.Core.Scheduling.PerformancePreset;
using CoreThemePreference = InfiniTranseon.Core.Settings.ThemePreference;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Real settings service. SQLite owns the complete application-settings document; this service only
/// maps its platform-neutral values to presentation types and combines provider metadata with live
/// credential presence.
/// </summary>
public sealed class RealSettingsService : ISettingsService
{
    private readonly CoreSettingsRepository _coreRepository;
    private readonly ISecretReferenceService _secrets;
    private readonly IReadOnlyList<CatalogProvider> _providers;
    private readonly CustomRestAdapterStore? _customAdapters;
    private readonly LocalModelManagementService? _localModels;
    private readonly OcrBackendPreferenceSource? _ocrBackend;

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        CustomRestAdapterStore? customAdapters = null)
        : this(
            coreRepository,
            secrets,
            ProviderCatalog.Default,
            customAdapters,
            localModels: null,
            ocrBackend: null)
    {
    }

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        CustomRestAdapterStore customAdapters,
        LocalModelManagementService localModels,
        OcrBackendPreferenceSource? ocrBackend = null)
        : this(
            coreRepository,
            secrets,
            ProviderCatalog.Default,
            customAdapters,
            localModels,
            ocrBackend)
    {
    }

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        IReadOnlyList<CatalogProvider> providers)
        : this(
            coreRepository,
            secrets,
            providers,
            customAdapters: null,
            localModels: null,
            ocrBackend: null)
    {
    }

    private RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        IReadOnlyList<CatalogProvider> providers,
        CustomRestAdapterStore? customAdapters,
        LocalModelManagementService? localModels,
        OcrBackendPreferenceSource? ocrBackend)
    {
        ArgumentNullException.ThrowIfNull(coreRepository);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(providers);
        _coreRepository = coreRepository;
        _secrets = secrets;
        _providers = providers;
        _customAdapters = customAdapters;
        _localModels = localModels;
        _ocrBackend = ocrBackend;
    }

    public async Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        CoreSettings core = await _coreRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        // The OCR probe routes synchronously on the frame path and cannot await this read, so the
        // persisted choice is republished here and in UpdateAsync.
        _ocrBackend?.Publish(FromCoreOcrBackend(core.OcrBackend));
        return new ApplicationSettings(
            FromCoreTheme(core.Theme),
            core.StrictOffline,
            FromCoreRetention(core.HistoryRetention),
            core.UiLanguage,
            core.Hotkeys is null
                ? HotkeyDefaults.Create()
                : core.Hotkeys.Select(FromCoreHotkey).ToArray(),
            FromCorePreset(core.Performance.Preset),
            core.ReducedMotion,
            new Dictionary<string, string>(core.ProviderEndpoints, StringComparer.Ordinal),
            core.CloseToTray,
            core.CloseToTrayConfirmed,
            [.. core.PinnedProfileIds],
            FromCoreOcrBackend(core.OcrBackend));
    }

    public async Task UpdateAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CoreSettings core = await _coreRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _coreRepository
            .SaveAsync(core with
            {
                UiLanguage = settings.UiLanguage,
                Theme = ToCoreTheme(settings.Theme),
                StrictOffline = settings.StrictOffline,
                OcrBackend = ToCoreOcrBackend(settings.OcrBackend),
                HistoryRetention = ToCoreRetention(settings.HistoryRetention),
                Hotkeys = settings.EffectiveHotkeys.Select(ToCoreHotkey).ToArray(),
                ProviderEndpoints = new Dictionary<string, string>(
                    settings.EffectiveProviderEndpoints,
                    StringComparer.Ordinal),
                ReducedMotion = settings.ReducedMotion,
                CloseToTray = settings.CloseToTray,
                CloseToTrayConfirmed = settings.CloseToTrayConfirmed,
                PinnedProfileIds = [.. settings.EffectivePinnedProfileIds],
                Performance = core.Performance with
                {
                    Preset = ToCorePreset(settings.PerformancePreset),
                    CustomThresholds = null,
                },
            }, cancellationToken)
            .ConfigureAwait(false);
        _ocrBackend?.Publish(settings.OcrBackend);
    }

    public async Task<IReadOnlyList<ProviderRow>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        ApplicationSettings settings =
            await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, SecretReference> references =
            (await _secrets.GetReferencesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(reference => reference.ReferenceId, StringComparer.OrdinalIgnoreCase);
        var rows = new List<ProviderRow>();
        foreach (CatalogProvider provider in CurrentProviders().Where(provider =>
            _localModels is null ||
            !provider.Id.StartsWith(
                "translation.local.",
                StringComparison.OrdinalIgnoreCase)))
        {
            ProviderCredentialField[] credentialFields = provider.Credentials
                .Select(credential =>
                {
                    bool isPresent = references.TryGetValue(
                        credential.Reference,
                        out SecretReference? reference) && reference.IsPresent;
                    return new ProviderCredentialField(
                        credential.Reference,
                        credential.DisplayName,
                        isPresent);
                })
                .ToArray();
            bool present = credentialFields.Length > 0 &&
                credentialFields.All(field => field.IsPresent);
            bool hasEndpoint = !provider.RequiresEndpoint ||
                settings.EffectiveProviderEndpoints.TryGetValue(
                    provider.Id,
                    out string? configuredEndpoint) &&
                !string.IsNullOrWhiteSpace(configuredEndpoint);
            string stateText = !provider.IsSelectable
                ? provider.UnavailableStateText ?? "Unavailable"
                : !hasEndpoint ? "Endpoint missing"
                : !provider.RequiresCredential ? "Available (offline)"
                : present ? "Connected"
                : "Credential missing";
            rows.Add(new ProviderRow(
                provider.DisplayName,
                provider.Kind,
                stateText,
                !hasEndpoint
                    ? Controls.StatusSeverity.Warning
                    : ProviderCatalog.SeverityFor(provider, present),
                provider.Detail)
            {
                Id = provider.Id,
                IsSelectable = provider.IsSelectable,
                IsTranslationProvider =
                    provider.Capability == CatalogProviderCapability.Translation,
                IsCustom = provider.IsCustom,
                IsLocalModel = provider.Id.StartsWith(
                    "translation.local.", StringComparison.OrdinalIgnoreCase),
                CanDownloadModel = false,
                RequiresEndpoint = provider.RequiresEndpoint,
                Endpoint = settings.EffectiveProviderEndpoints.GetValueOrDefault(provider.Id),
                EndpointPlaceholder = provider.EndpointPlaceholder,
                Credentials = credentialFields,
            });
        }

        if (_localModels is not null)
        {
            rows.AddRange(ToLocalModelRows(
                _localModels.GetSnapshot(),
                settings.StrictOffline));
        }

        return rows;
    }

    public Task<ProviderRow> ImportRestAdapterAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CustomRestAdapterStore store = _customAdapters ??
            throw new InvalidOperationException(
                "Custom REST adapters are unavailable in this composition.");
        CatalogProvider provider = CustomRestAdapterStore.ToCatalogProvider(store.Import(source));
        return Task.FromResult(ToDisconnectedRow(provider));
    }

    public Task RemoveCustomProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CustomRestAdapterStore store = _customAdapters ??
            throw new InvalidOperationException(
                "Custom REST adapters are unavailable in this composition.");
        store.Remove(providerId);
        return Task.CompletedTask;
    }

    private IReadOnlyList<CatalogProvider> CurrentProviders()
    {
        if (_customAdapters is null)
        {
            return _providers;
        }

        return _providers.Concat(_customAdapters.GetCatalogProviders()).ToArray();
    }

    private static ProviderRow ToDisconnectedRow(CatalogProvider provider) =>
        new(
            provider.DisplayName,
            provider.Kind,
            provider.RequiresCredential ? "Credential missing" : "Available",
            provider.RequiresCredential ? Controls.StatusSeverity.Warning : Controls.StatusSeverity.Neutral,
            provider.Detail)
        {
            Id = provider.Id,
            IsSelectable = provider.IsSelectable,
            IsTranslationProvider = true,
            IsCustom = provider.IsCustom,
            Credentials = provider.Credentials
                .Select(credential => new ProviderCredentialField(
                    credential.Reference,
                    credential.DisplayName,
                    IsPresent: false))
                .ToArray(),
        };

    private static IEnumerable<ProviderRow> ToLocalModelRows(
        LocalModelCatalogView catalog,
        bool strictOffline)
    {
        if (catalog.State != LocalModelCatalogState.Available ||
            catalog.Packages.Count == 0 ||
            catalog.Problem is not null)
        {
            yield return new ProviderRow(
                "Local model catalog",
                "NMT · local",
                catalog.State switch
                {
                    LocalModelCatalogState.Invalid => "Catalog verification failed",
                    LocalModelCatalogState.Missing => "Package catalog not published",
                    _ => "No model packages published",
                },
                catalog.State == LocalModelCatalogState.Invalid
                    ? Controls.StatusSeverity.Critical
                    : Controls.StatusSeverity.Neutral,
                catalog.Problem ?? "No local model package is currently available.")
            {
                Id = "translation.local.catalog",
                IsSelectable = false,
                IsTranslationProvider = true,
                IsLocalModel = true,
            };
            if (catalog.Packages.Count == 0)
                yield break;
        }

        foreach (LocalModelPackageView model in catalog.Packages)
        {
            bool installed = model.State != LocalModelInstallState.NotInstalled;
            yield return new ProviderRow(
                model.DisplayName,
                "NMT · local",
                model.State switch
                {
                    LocalModelInstallState.Installed => "Installed · ready",
                    LocalModelInstallState.RuntimeUnavailable => "Installed · runtime unavailable",
                    LocalModelInstallState.Corrupt => "Installed package is corrupt",
                    LocalModelInstallState.Uncatalogued => "Installed · absent from signed catalog",
                    _ when strictOffline => "Not installed · strict-offline mode blocks download",
                    _ => "Not installed · download only on request",
                },
                model.State switch
                {
                    LocalModelInstallState.Installed => Controls.StatusSeverity.Success,
                    LocalModelInstallState.Corrupt => Controls.StatusSeverity.Critical,
                    LocalModelInstallState.RuntimeUnavailable => Controls.StatusSeverity.Warning,
                    LocalModelInstallState.Uncatalogued => Controls.StatusSeverity.Warning,
                    _ when strictOffline => Controls.StatusSeverity.Warning,
                    _ => Controls.StatusSeverity.Neutral,
                },
                ModelDetail(model))
            {
                Id = $"translation.local.{model.ModelId}",
                IsSelectable = model.State == LocalModelInstallState.Installed,
                IsTranslationProvider = true,
                IsLocalModel = true,
                CanDownloadModel =
                    model.State == LocalModelInstallState.NotInstalled &&
                    !strictOffline,
                CanRemoveModel = installed,
                ModelId = model.ModelId,
                ModelVersion = model.Version,
                ModelLicense = model.LicenseSpdx,
                ModelRuntime = model.Runtime,
                ModelPackageDirectory = model.PackageDirectory,
                ModelDownloadSize = FormatBytes(model.DownloadBytes),
            };
        }
    }

    private static string ModelDetail(LocalModelPackageView model)
    {
        if (model.State == LocalModelInstallState.Uncatalogued)
        {
            return "This application-managed package is no longer listed in the signed catalog. " +
                "It cannot be selected, but it can still be removed.";
        }
        string languages = model.SourceLanguages.Count == 0 &&
            model.TargetLanguages.Count == 0
                ? "language coverage declared by package"
                : $"{string.Join(", ", model.SourceLanguages)} → " +
                    string.Join(", ", model.TargetLanguages);
        return $"{FormatBytes(model.DownloadBytes)} · {model.LicenseSpdx} · " +
            $"{model.Runtime} · {languages}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static AppPerformancePreset FromCorePreset(CorePerformancePreset preset) => preset switch
    {
        CorePerformancePreset.Eco => AppPerformancePreset.Eco,
        CorePerformancePreset.Performance => AppPerformancePreset.Performance,
        _ => AppPerformancePreset.Balanced,
    };

    private static CorePerformancePreset ToCorePreset(AppPerformancePreset preset) => preset switch
    {
        AppPerformancePreset.Eco => CorePerformancePreset.Eco,
        AppPerformancePreset.Performance => CorePerformancePreset.Performance,
        _ => CorePerformancePreset.Balanced,
    };

    private static UiThemePreference FromCoreTheme(CoreThemePreference theme) => theme switch
    {
        CoreThemePreference.Light => UiThemePreference.Light,
        CoreThemePreference.Dark => UiThemePreference.Dark,
        _ => UiThemePreference.System,
    };

    private static CoreThemePreference ToCoreTheme(UiThemePreference theme) => theme switch
    {
        UiThemePreference.Light => CoreThemePreference.Light,
        UiThemePreference.Dark => CoreThemePreference.Dark,
        _ => CoreThemePreference.System,
    };

    private static HistoryRetention FromCoreRetention(CoreHistoryRetention retention) => retention switch
    {
        CoreHistoryRetention.Off => HistoryRetention.Off,
        CoreHistoryRetention.Days90 => HistoryRetention.Days90,
        _ => HistoryRetention.Days30,
    };

    private static CoreHistoryRetention ToCoreRetention(HistoryRetention retention) => retention switch
    {
        HistoryRetention.Off => CoreHistoryRetention.Off,
        HistoryRetention.Days90 => CoreHistoryRetention.Days90,
        _ => CoreHistoryRetention.Days30,
    };

    private static AppOcrBackend FromCoreOcrBackend(CoreOcrBackend backend) => backend switch
    {
        CoreOcrBackend.Windows => AppOcrBackend.Windows,
        CoreOcrBackend.Local => AppOcrBackend.Local,
        _ => AppOcrBackend.Automatic,
    };

    private static CoreOcrBackend ToCoreOcrBackend(AppOcrBackend backend) => backend switch
    {
        AppOcrBackend.Windows => CoreOcrBackend.Windows,
        AppOcrBackend.Local => CoreOcrBackend.Local,
        _ => CoreOcrBackend.Automatic,
    };

    private static CoreHotkeySetting ToCoreHotkey(AppHotkeyBinding hotkey) =>
        ToCoreHotkeyNormalized(HotkeyBindingRules.Normalize(hotkey));

    private static CoreHotkeySetting ToCoreHotkeyNormalized(AppHotkeyBinding hotkey) => new(
        hotkey.Action.ToString(),
        hotkey.Gesture,
        hotkey.Enabled,
        hotkey.Scope.ToString(),
        hotkey.EffectiveSpecificTargets
            .Select(target => new CoreHotkeyTargetReference(target.ProfileId, target.ProfileTargetId))
            .ToArray());

    private static AppHotkeyBinding FromCoreHotkey(CoreHotkeySetting hotkey)
    {
        if (!Enum.TryParse(hotkey.Action, ignoreCase: true, out AppHotkeyAction action) ||
            !Enum.IsDefined(action) ||
            !Enum.TryParse(hotkey.Scope, ignoreCase: true, out AppHotkeyScope scope) ||
            !Enum.IsDefined(scope))
        {
            throw new InvalidDataException("A persisted global hotkey uses an unsupported action or scope.");
        }
        return HotkeyBindingRules.Normalize(new AppHotkeyBinding(
            action,
            hotkey.Gesture,
            hotkey.Enabled,
            scope,
            (hotkey.SpecificTargets ?? [])
                .Select(target => new AppHotkeyTargetReference(target.ProfileId, target.ProfileTargetId))
                .ToArray()));
    }
}
