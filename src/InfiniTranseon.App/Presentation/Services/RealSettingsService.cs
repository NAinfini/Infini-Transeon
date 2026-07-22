using CoreSettings = InfiniTranseon.Core.Settings.ApplicationSettings;
using CoreSettingsRepository = InfiniTranseon.Core.Settings.ApplicationSettingsRepository;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Real settings service. The UI language is the one field shared with the Core application-settings
/// schema, so it round-trips through <see cref="CoreSettingsRepository"/>; the app-only presentation
/// preferences (theme, strict-offline default, history retention) persist alongside it in a JSON file.
/// Provider rows combine the static provider catalog with live credential presence.
/// </summary>
public sealed class RealSettingsService : ISettingsService
{
    private readonly CoreSettingsRepository _coreRepository;
    private readonly UiPreferencesStore _preferences;
    private readonly ISecretReferenceService _secrets;
    private readonly IReadOnlyList<CatalogProvider> _providers;

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        UiPreferencesStore preferences,
        ISecretReferenceService secrets)
        : this(coreRepository, preferences, secrets, ProviderCatalog.Default)
    {
    }

    public RealSettingsService(
        CoreSettingsRepository coreRepository,
        UiPreferencesStore preferences,
        ISecretReferenceService secrets,
        IReadOnlyList<CatalogProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(coreRepository);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(providers);
        _coreRepository = coreRepository;
        _preferences = preferences;
        _secrets = secrets;
        _providers = providers;
    }

    public async Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        CoreSettings core = await _coreRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        UiPreferences preferences = _preferences.Load();
        return new ApplicationSettings(
            preferences.Theme,
            preferences.StrictOffline,
            preferences.HistoryRetention,
            core.UiLanguage);
    }

    public async Task UpdateAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _preferences.Save(new UiPreferences(settings.Theme, settings.StrictOffline, settings.HistoryRetention));
        CoreSettings core = await _coreRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _coreRepository
            .SaveAsync(core with { UiLanguage = settings.UiLanguage }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderRow>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<ProviderRow>();
        foreach (CatalogProvider provider in _providers)
        {
            bool present = provider.RequiresCredential &&
                await _secrets.HasSecretAsync(provider.DisplayName, cancellationToken).ConfigureAwait(false);
            string stateText = !provider.RequiresCredential ? "Available (offline)"
                : present ? "Connected"
                : "Credential missing";
            rows.Add(new ProviderRow(
                provider.DisplayName,
                provider.Kind,
                stateText,
                ProviderCatalog.SeverityFor(provider, present),
                provider.Detail));
        }

        return rows;
    }
}
