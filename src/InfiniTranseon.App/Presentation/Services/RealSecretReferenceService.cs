using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Maps the presentation secret-reference contract onto the real Windows Credential Manager store
/// (<see cref="GenericCredentialStore"/>). The secret value is written straight through and only ever
/// read internally to answer a presence question; view models receive presence and metadata only.
/// </summary>
public sealed class RealSecretReferenceService : ISecretReferenceService
{
    private const string StorageLocationName = "Windows Credential Manager";
    private readonly IReadOnlyList<CatalogProvider> _providers;
    private readonly IBoundCredentialStore _store;

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
        _providers = providers;
    }

    public async Task<IReadOnlyList<SecretReference>> GetReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var references = new List<SecretReference>();
        foreach (CatalogProvider provider in _providers)
        {
            if (!provider.RequiresCredential)
            {
                continue;
            }

            bool present = await ReadPresenceAsync(provider, cancellationToken).ConfigureAwait(false);
            references.Add(new SecretReference(
                provider.CredentialReference!,
                provider.DisplayName,
                StorageLocationName,
                present));
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

        return await ReadPresenceAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSecretAsync(
        string providerId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        CatalogProvider provider = RequireCredentialProvider(providerId);
        await _store.WriteAsync(provider.CredentialReference!, secret, provider.Binding!, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearSecretAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        CatalogProvider provider = RequireCredentialProvider(providerId);
        await _store.DeleteAsync(provider.CredentialReference!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReadPresenceAsync(CatalogProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            string? secret = await _store
                .ReadAsync(provider.CredentialReference!, provider.Binding!, cancellationToken)
                .ConfigureAwait(false);
            return secret is not null;
        }
        catch (CredentialBindingException)
        {
            // A credential exists but its origin binding changed; it is present and needs reconfirmation.
            return true;
        }
    }

    private CatalogProvider? ProviderById(string providerId) => _providers.FirstOrDefault(provider =>
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
}
