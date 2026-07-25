using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Settings;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Maps the presentation secret-reference contract onto the real Windows Credential Manager store
/// (<see cref="GenericCredentialStore"/>). The secret value is written straight through and only ever
/// read internally to answer a presence question; view models receive presence and metadata only.
/// </summary>
public sealed class RealSecretReferenceService : ISecretReferenceService
{
    private const string StorageLocationName = "Windows Credential Manager";
    private readonly Func<IReadOnlyList<CatalogProvider>> _providerSource;
    private readonly IBoundCredentialStore _store;
    private readonly ApplicationSettingsRepository? _settings;

    public RealSecretReferenceService(IBoundCredentialStore store)
        : this(store, ProviderCatalog.Default)
    {
    }

    // Overload for tests: a test-only catalog whose credential references are prefixed so they never
    // collide with a user's real provider credentials.
    public RealSecretReferenceService(IBoundCredentialStore store, IReadOnlyList<CatalogProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providers);
        _store = store;
        _settings = null;
        _providerSource = () => providers;
    }

    public RealSecretReferenceService(
        IBoundCredentialStore store,
        Func<IReadOnlyList<CatalogProvider>> providerSource)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providerSource);
        _store = store;
        _settings = null;
        _providerSource = providerSource;
    }

    public RealSecretReferenceService(
        IBoundCredentialStore store,
        ApplicationSettingsRepository settings,
        Func<IReadOnlyList<CatalogProvider>> providerSource)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(providerSource);
        _store = store;
        _settings = settings;
        _providerSource = providerSource;
    }

    public async Task<IReadOnlyList<SecretReference>> GetReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var references = new List<SecretReference>();
        IReadOnlyDictionary<string, string> endpoints =
            await LoadProviderEndpointsAsync(cancellationToken).ConfigureAwait(false);
        foreach (CatalogProvider provider in _providerSource())
        {
            foreach (CatalogCredential credential in provider.Credentials)
            {
                bool present = await ReadPresenceAsync(credential, endpoints, cancellationToken)
                    .ConfigureAwait(false);
                references.Add(new SecretReference(
                    credential.Reference,
                    $"{provider.DisplayName} · {credential.DisplayName}",
                    StorageLocationName,
                    present));
            }
        }

        return references;
    }

    public async Task<bool> HasSecretAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        CatalogProvider? provider = ProviderById(providerId);
        if (provider is null || !provider.RequiresCredential)
        {
            return false;
        }

        IReadOnlyDictionary<string, string> endpoints =
            await LoadProviderEndpointsAsync(cancellationToken).ConfigureAwait(false);
        foreach (CatalogCredential credential in provider.Credentials)
        {
            if (!await ReadPresenceAsync(credential, endpoints, cancellationToken).ConfigureAwait(false))
                return false;
        }
        return true;
    }

    public async Task SetSecretAsync(
        string providerId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        CatalogCredential credential = RequireSingleCredential(providerId);
        IReadOnlyDictionary<string, string> endpoints =
            await LoadProviderEndpointsAsync(cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(
            credential.Reference,
            secret,
            credential.ResolveBinding(endpoints),
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetSecretAsync(
        string providerId,
        string credentialReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        CatalogCredential credential = RequireCredential(providerId, credentialReference);
        IReadOnlyDictionary<string, string> endpoints =
            await LoadProviderEndpointsAsync(cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(
            credential.Reference,
            secret,
            credential.ResolveBinding(endpoints),
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearSecretAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        CatalogCredential credential = RequireSingleCredential(providerId);
        await _store.DeleteAsync(credential.Reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearSecretAsync(
        string providerId,
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        CatalogCredential credential = RequireCredential(providerId, credentialReference);
        await _store.DeleteAsync(credential.Reference, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReadPresenceAsync(
        CatalogCredential credential,
        IReadOnlyDictionary<string, string> providerEndpoints,
        CancellationToken cancellationToken)
    {
        try
        {
            string? secret = await _store
                .ReadAsync(
                    credential.Reference,
                    credential.ResolveBinding(providerEndpoints),
                    cancellationToken)
                .ConfigureAwait(false);
            return secret is not null;
        }
        catch (CredentialBindingException)
        {
            // A credential exists but its origin binding changed; it is present and needs reconfirmation.
            return true;
        }
        catch (InvalidDataException)
        {
            // A provider-specific endpoint has not been configured yet, so no bound credential can
            // be considered usable.
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadProviderEndpointsAsync(
        CancellationToken cancellationToken) =>
        _settings is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ProviderEndpoints;

    private CatalogProvider? ProviderById(string providerId) => _providerSource().FirstOrDefault(provider =>
        string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider.DisplayName, providerId, StringComparison.OrdinalIgnoreCase));

    private CatalogProvider RequireCredentialProvider(string providerId)
    {
        CatalogProvider? provider = ProviderById(providerId);
        if (provider is null)
        {
            throw new InvalidOperationException($"Unknown provider '{providerId}'.");
        }

        if (!provider.RequiresCredential)
        {
            throw new InvalidOperationException($"Provider '{provider.DisplayName}' does not use a credential.");
        }

        return provider;
    }

    private CatalogCredential RequireSingleCredential(string providerId)
    {
        CatalogProvider provider = RequireCredentialProvider(providerId);
        if (provider.Credentials.Count != 1)
            throw new InvalidOperationException(
                $"Provider '{provider.DisplayName}' requires selecting one of its credential fields.");
        return provider.Credentials[0];
    }

    private CatalogCredential RequireCredential(string providerId, string credentialReference)
    {
        CatalogProvider provider = RequireCredentialProvider(providerId);
        CatalogCredential? credential = provider.Credentials.FirstOrDefault(item =>
            string.Equals(item.Reference, credentialReference, StringComparison.OrdinalIgnoreCase));
        return credential ?? throw new InvalidOperationException(
            $"Provider '{provider.DisplayName}' has no credential field '{credentialReference}'.");
    }
}
