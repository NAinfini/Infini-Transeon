using CoreSettings = InfiniTranseon.Core.Settings.ApplicationSettings;
using CoreSettingsRepository = InfiniTranseon.Core.Settings.ApplicationSettingsRepository;
using CoreHistoryRetention = InfiniTranseon.Core.Settings.HistoryRetentionPolicy;
using CoreHotkeySetting = InfiniTranseon.Core.Settings.HotkeySetting;
using CoreHotkeyTargetReference = InfiniTranseon.Core.Settings.HotkeyTargetReference;
using CoreOcrBackend = InfiniTranseon.Core.Settings.OcrBackendPreference;
using CorePerformancePreset = InfiniTranseon.Core.Scheduling.PerformancePreset;
using CoreThemePreference = InfiniTranseon.Core.Settings.ThemePreference;
using LocalModelRuntimeAvailability =
    InfiniTranseon.Core.Translation.Local.LocalModelRuntimeAvailability;

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
    private readonly ResourceTextLookup _text;
    private readonly IReadOnlyList<CatalogProvider> _providers;
    private readonly CustomRestAdapterStore? _customAdapters;
    private readonly LocalModelManagementService? _localModels;
    private readonly OcrBackendPreferenceSource? _ocrBackend;
    private readonly IOcrLanguageAvailability? _ocrLanguages;

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        ResourceTextLookup text,
        CustomRestAdapterStore? customAdapters = null)
        : this(
            coreRepository,
            secrets,
            text,
            ProviderCatalog.Default,
            customAdapters,
            localModels: null,
            ocrBackend: null,
            ocrLanguages: null)
    {
    }

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        ResourceTextLookup text,
        CustomRestAdapterStore customAdapters,
        LocalModelManagementService localModels,
        OcrBackendPreferenceSource? ocrBackend = null,
        IOcrLanguageAvailability? ocrLanguages = null)
        : this(
            coreRepository,
            secrets,
            text,
            ProviderCatalog.Default,
            customAdapters,
            localModels,
            ocrBackend,
            ocrLanguages)
    {
    }

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        ResourceTextLookup text,
        IReadOnlyList<CatalogProvider> providers)
        : this(
            coreRepository,
            secrets,
            text,
            providers,
            customAdapters: null,
            localModels: null,
            ocrBackend: null,
            ocrLanguages: null)
    {
    }

    private RealSettingsService(
        CoreSettingsRepository coreRepository,
        ISecretReferenceService secrets,
        ResourceTextLookup text,
        IReadOnlyList<CatalogProvider> providers,
        CustomRestAdapterStore? customAdapters,
        LocalModelManagementService? localModels,
        OcrBackendPreferenceSource? ocrBackend,
        IOcrLanguageAvailability? ocrLanguages)
    {
        ArgumentNullException.ThrowIfNull(coreRepository);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(providers);
        _coreRepository = coreRepository;
        _secrets = secrets;
        _text = text;
        _providers = providers;
        _customAdapters = customAdapters;
        _localModels = localModels;
        _ocrBackend = ocrBackend;
        _ocrLanguages = ocrLanguages;
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
            FromCoreOcrBackend(core.OcrBackend),
            new Dictionary<string, string>(core.ProviderModels, StringComparer.Ordinal));
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
                ProviderModels = new Dictionary<string, string>(
                    settings.EffectiveProviderModels,
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
                EngineRuntimeComposition.LocalTranslationProviderIdPrefix,
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
            string stateResourceKey = !provider.IsSelectable
                ? provider.UnavailableStateResourceKey ?? "ProviderStateUnavailable"
                : !hasEndpoint ? "ProviderStateEndpointMissing"
                : !provider.RequiresCredential ? "ProviderStateAvailableOffline"
                : present ? "ProviderStateConnected"
                : "ProviderStateCredentialMissing";
            rows.Add(new ProviderRow(
                provider.DisplayName,
                _text(provider.KindResourceKey),
                _text(stateResourceKey),
                !hasEndpoint
                    ? Controls.StatusSeverity.Warning
                    : ProviderCatalog.SeverityFor(provider, present),
                Text(provider.DetailResourceKey, provider.DetailArguments))
            {
                Id = provider.Id,
                IsSelectable = provider.IsSelectable,
                IsTranslationProvider =
                    provider.Capability == CatalogProviderCapability.Translation,
                IsOcrProvider = provider.Capability == CatalogProviderCapability.Ocr,
                IsCustom = provider.IsCustom,
                IsLocalModel = provider.Id.StartsWith(
                    EngineRuntimeComposition.LocalTranslationProviderIdPrefix,
                    StringComparison.OrdinalIgnoreCase),
                CanDownloadModel = false,
                RequiresEndpoint = provider.RequiresEndpoint,
                Endpoint = provider.DefaultEndpoint is not null
                    ? provider.ResolveEndpoint(settings.EffectiveProviderEndpoints).AbsoluteUri
                    : settings.EffectiveProviderEndpoints.GetValueOrDefault(provider.Id),
                EndpointPlaceholder = provider.EndpointPlaceholder ??
                    provider.DefaultEndpoint?.AbsoluteUri,
                DefaultEndpoint = provider.DefaultEndpoint?.AbsoluteUri,
                Model = provider.DefaultModel is not null
                    ? provider.ResolveModel(settings.EffectiveProviderModels)
                    : null,
                DefaultModel = provider.DefaultModel,
                CanOverrideEndpoint = provider.CanOverrideEndpoint,
                CanOverrideModel = provider.CanOverrideModel,
                IsEndpointOverridden =
                    settings.EffectiveProviderEndpoints.ContainsKey(provider.Id),
                IsModelOverridden =
                    settings.EffectiveProviderModels.ContainsKey(provider.Id),
                Credentials = credentialFields,
            });
        }

        // Outside the catalog branch: Windows recognition is the default OCR path and is present
        // whether or not a model catalog is. Leaving it out made the local section read as "this
        // machine can read nothing" on a machine that reads several languages already.
        if (_ocrLanguages is not null)
        {
            rows.Add(ToWindowsRecognizerRow());
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

    /// <summary>Resolves a catalog entry's description, formatting in the machine data an entry may
    /// carry (a REST adapter's method and host) so the surrounding words remain translatable.</summary>
    private string Text(string resourceKey, IReadOnlyList<object?> arguments) =>
        arguments.Count == 0
            ? _text(resourceKey)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _text(resourceKey),
                [.. arguments]);

    private ProviderRow ToDisconnectedRow(CatalogProvider provider) =>
        new(
            provider.DisplayName,
            _text(provider.KindResourceKey),
            _text(provider.RequiresCredential
                ? "ProviderStateCredentialMissing"
                : "ProviderStateAvailable"),
            provider.RequiresCredential ? Controls.StatusSeverity.Warning : Controls.StatusSeverity.Neutral,
            Text(provider.DetailResourceKey, provider.DetailArguments))
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

    /// <summary>
    /// Windows recognition as a local package the user already has. It is not downloadable and not
    /// removable, so the row carries neither action; what it does carry is the list of languages this
    /// machine can actually read today, which is the one thing about it that varies per machine and
    /// the reason the PP-OCR packages below it exist at all.
    /// </summary>
    private ProviderRow ToWindowsRecognizerRow()
    {
        IReadOnlyList<string> tags = _ocrLanguages!.WindowsRecognizerTags;
        bool hasRecognizer = tags.Count > 0;
        return new ProviderRow(
            _text("ProviderWindowsOcrName"),
            _text("ProviderKindOcrLocal"),
            _text(hasRecognizer
                ? "ProviderStateWindowsOcrBuiltIn"
                : "ProviderStateWindowsOcrNoLanguages"),
            hasRecognizer ? Controls.StatusSeverity.Success : Controls.StatusSeverity.Warning,
            hasRecognizer
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    _text("ProviderDetailWindowsOcr"),
                    LanguageNames(tags, _text("UiLanguageTag")))
                : _text("ProviderDetailWindowsOcrNoLanguages"))
        {
            Id = "ocr.windows",
            IsSelectable = false,
            IsOcrProvider = true,
            IsLocalModel = true,
            CanDownloadModel = false,
            CanRemoveModel = false,
        };
    }

    private IEnumerable<ProviderRow> ToLocalModelRows(
        LocalModelCatalogView catalog,
        bool strictOffline)
    {
        if (catalog.State != LocalModelCatalogState.Available ||
            catalog.Packages.Count == 0 ||
            catalog.Problem is not null)
        {
            // catalog.Problem is the verifier's own diagnostic text. It stays verbatim: replacing it
            // with a translated summary would drop the only description of what failed.
            yield return new ProviderRow(
                _text("ProviderLocalCatalogName"),
                _text("ProviderKindNmtLocal"),
                _text(catalog.State switch
                {
                    LocalModelCatalogState.Invalid => "ProviderStateCatalogInvalid",
                    LocalModelCatalogState.Missing => "ProviderStateCatalogMissing",
                    _ => "ProviderStateCatalogEmpty",
                }),
                catalog.State == LocalModelCatalogState.Invalid
                    ? Controls.StatusSeverity.Critical
                    : Controls.StatusSeverity.Neutral,
                catalog.Problem ?? _text("ProviderDetailLocalCatalogEmpty"))
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
            yield return ToLocalModelRow(model, strictOffline);
        }
    }

    internal ProviderRow ToLocalModelRow(
        LocalModelPackageView model,
        bool strictOffline)
    {
        ArgumentNullException.ThrowIfNull(model);
        bool installed = model.State != LocalModelInstallState.NotInstalled;
        bool isOcr = string.Equals(
            model.Runtime,
            LocalModelRuntimeAvailability.PpOcrOnnxRuntime,
            StringComparison.Ordinal);
        return new ProviderRow(
                model.DisplayName,
                _text(isOcr ? "ProviderKindOcrLocal" : "ProviderKindNmtLocal"),
                _text(model.State switch
                {
                    LocalModelInstallState.Installed => "ProviderStateModelReady",
                    LocalModelInstallState.RuntimeUnavailable =>
                        "ProviderStateModelRuntimeUnavailable",
                    LocalModelInstallState.Corrupt => "ProviderStateModelCorrupt",
                    LocalModelInstallState.Uncatalogued => "ProviderStateModelUncatalogued",
                    _ when strictOffline => "ProviderStateModelBlockedByStrictOffline",
                    _ => "ProviderStateModelNotInstalled",
                }),
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
                Id = isOcr
                    ? $"ocr.local.{model.ModelId}"
                    : EngineRuntimeComposition.LocalTranslationProviderId(model.ModelId),
                IsSelectable = !isOcr && model.State == LocalModelInstallState.Installed,
                IsTranslationProvider = !isOcr,
                IsOcrProvider = isOcr,
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
                ModelDownloadSize = ByteSizeText.Format(model.DownloadBytes),
            };
    }

    private string ModelDetail(LocalModelPackageView model)
    {
        if (model.State == LocalModelInstallState.Uncatalogued)
        {
            return _text("ProviderDetailModelUncatalogued");
        }
        // The catalog declares coverage in BCP-47, which is the right form to sign and the wrong form
        // to read: a row that otherwise speaks the user's language listed "zh-Hans, zh-Hant".
        string uiLanguage = _text("UiLanguageTag");
        string languages = model.SourceLanguages.Count == 0 &&
            model.TargetLanguages.Count == 0
                ? _text("ProviderDetailModelLanguagesUndeclared")
                : $"{LanguageNames(model.SourceLanguages, uiLanguage)} → " +
                    LanguageNames(model.TargetLanguages, uiLanguage);
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _text("ProviderDetailModelSummary"),
            ByteSizeText.Format(model.DownloadBytes),
            model.LicenseSpdx,
            model.Runtime,
            languages);
    }

    /// <summary>
    /// Names a list of BCP-47 tags in the user's language. A catalog package declares "en" while
    /// Windows holds "en-US", so the tag is resolved on language plus script rather than by equality
    /// — but only when exactly one catalog language fits. Bare "zh" fits both zh-Hans and zh-Hant,
    /// and naming one of them would be a guess about which script the package can read.
    ///
    /// Names repeat where tags do not: a machine with the en-US and en-GB recognizers installed can
    /// read English, once. What the row answers is which languages are readable, not how many
    /// regional recognizers back each one.
    /// </summary>
    private static string LanguageNames(IReadOnlyList<string> tags, string uiLanguage)
    {
        string[] catalogCodes = [.. LanguageCatalog.CreateSourceOptions(uiLanguage).Select(o => o.Code)];
        return string.Join(", ", tags.Select(tag =>
        {
            string[] fits = [.. catalogCodes.Where(code =>
                WindowsOcrLanguageAvailability.Matches(code, tag))];
            return LanguageCatalog.DisplayNameFor(fits.Length == 1 ? fits[0] : tag, uiLanguage);
        }).Distinct(StringComparer.CurrentCulture));
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
