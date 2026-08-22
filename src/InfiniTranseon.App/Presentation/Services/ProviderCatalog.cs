using InfiniTranseon.App.Controls;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// A translator provider the control UI can configure: its stable id, display metadata, and — for
/// cloud providers — the credential reference and origin binding used by the Windows Credential
/// Manager store. Local providers require no credential (<see cref="Binding"/> is null).
/// </summary>
public sealed record CatalogCredential(
    string Reference,
    string DisplayName,
    CredentialBinding Binding)
{
    public Func<IReadOnlyDictionary<string, string>, CredentialBinding>? BindingResolver { get; init; }

    public CredentialBinding ResolveBinding(IReadOnlyDictionary<string, string> providerEndpoints) =>
        BindingResolver?.Invoke(providerEndpoints) ?? Binding;
}

/// <summary>
/// A catalog entry names its taxonomy, description and unavailable state by resource key rather than
/// by prose. The catalog is a static table shared by services that must not depend on the resource
/// system — it is read on background threads and in tests where MRT is not registered — so the text
/// is resolved once, at the point a row is built for display.
/// </summary>
public sealed record CatalogProvider(
    string Id,
    string DisplayName,
    string KindResourceKey,
    IReadOnlyList<CatalogCredential> Credentials,
    string DetailResourceKey)
{
    public bool IsSelectable { get; init; } = true;

    /// <summary>Composite format arguments for <see cref="DetailResourceKey"/>. Entries whose
    /// description embeds machine data — an endpoint host, an HTTP method — name a format string and
    /// supply the data here, so the sentence around the data stays translatable.</summary>
    public IReadOnlyList<object?> DetailArguments { get; init; } = [];

    public string? UnavailableStateResourceKey { get; init; }

    public CatalogProviderCapability Capability { get; init; } =
        CatalogProviderCapability.Translation;

    public bool IsCustom { get; init; }

    public bool RequiresEndpoint { get; init; }

    public string? EndpointPlaceholder { get; init; }

    public Uri? DefaultEndpoint { get; init; }

    public string? DefaultModel { get; init; }

    public bool CanOverrideEndpoint { get; init; }

    public bool CanOverrideModel { get; init; }

    public CatalogProvider(
        string id,
        string displayName,
        string kindResourceKey,
        string? credentialReference,
        CredentialBinding? binding,
        string detailResourceKey)
        : this(
            id,
            displayName,
            kindResourceKey,
            credentialReference is not null && binding is not null
                ? [new CatalogCredential(credentialReference, "API key", binding)]
                : [],
            detailResourceKey)
    {
        if ((credentialReference is null) != (binding is null))
            throw new ArgumentException("Credential reference and binding must be supplied together.");
    }

    public bool RequiresCredential => Credentials.Count > 0;

    // Compatibility accessors for the many providers that use exactly one API key.
    public string? CredentialReference =>
        Credentials.Count == 1 ? Credentials[0].Reference : null;

    public CredentialBinding? Binding =>
        Credentials.Count == 1 ? Credentials[0].Binding : null;

    public Uri ResolveEndpoint(IReadOnlyDictionary<string, string> providerEndpoints)
    {
        ArgumentNullException.ThrowIfNull(providerEndpoints);
        if (providerEndpoints.TryGetValue(Id, out string? configured) &&
            Uri.TryCreate(configured, UriKind.Absolute, out Uri? endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(endpoint.UserInfo) &&
            string.IsNullOrEmpty(endpoint.Query) &&
            string.IsNullOrEmpty(endpoint.Fragment))
        {
            return endpoint;
        }
        return DefaultEndpoint ?? throw new InvalidDataException(
            $"Provider '{Id}' requires a valid HTTPS endpoint.");
    }

    public string ResolveModel(IReadOnlyDictionary<string, string> providerModels)
    {
        ArgumentNullException.ThrowIfNull(providerModels);
        string? model = providerModels.GetValueOrDefault(Id) ?? DefaultModel;
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Any(char.IsControl))
        {
            throw new InvalidDataException($"Provider '{Id}' requires a valid model name.");
        }
        return model;
    }
}

public enum CatalogProviderCapability
{
    Translation,
    Ocr,
}

/// <summary>
/// UI projection of <see cref="BuiltInProviderSpecs"/>, shared by
/// <see cref="RealSettingsService"/> and <see cref="RealSecretReferenceService"/>. A secret saved
/// through this catalog uses the same binding definition as the runtime factory.
/// </summary>
public static class ProviderCatalog
{
    // Built-ins only. A local model is not a built-in: it exists on this machine or it does not, and
    // LocalModelManagementService is the only thing that knows which. A static entry here claimed
    // "not installed" for as long as the process lived, which outlived the download that installed it.
    public static IReadOnlyList<CatalogProvider> Default { get; } =
        [.. BuiltInProviderSpecs.All.Select(spec => spec.Catalog)];

    public static CatalogProvider? Find(string idOrDisplayName) => Default.FirstOrDefault(provider =>
        string.Equals(provider.Id, idOrDisplayName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider.DisplayName, idOrDisplayName, StringComparison.OrdinalIgnoreCase));

    public static StatusSeverity SeverityFor(CatalogProvider provider, bool hasCredential) =>
        !provider.IsSelectable || !provider.RequiresCredential ? StatusSeverity.Neutral
        : hasCredential ? StatusSeverity.Success
        : StatusSeverity.Warning;
}
