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

public sealed record CatalogProvider(
    string Id,
    string DisplayName,
    string Kind,
    IReadOnlyList<CatalogCredential> Credentials,
    string Detail)
{
    public bool IsSelectable { get; init; } = true;

    public string? UnavailableStateText { get; init; }

    public CatalogProviderCapability Capability { get; init; } =
        CatalogProviderCapability.Translation;

    public bool IsCustom { get; init; }

    public bool RequiresEndpoint { get; init; }

    public string? EndpointPlaceholder { get; init; }

    public CatalogProvider(
        string id,
        string displayName,
        string kind,
        string? credentialReference,
        CredentialBinding? binding,
        string detail)
        : this(
            id,
            displayName,
            kind,
            credentialReference is not null && binding is not null
                ? [new CatalogCredential(credentialReference, "API key", binding)]
                : [],
            detail)
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
    public static IReadOnlyList<CatalogProvider> Default { get; } =
    [
        .. BuiltInProviderSpecs.All.Select(spec => spec.Catalog),
        new("translation.local.madlad", "Local MADLAD-400 3B", "NMT · local", null, null,
            "Optional model · never downloaded automatically")
        {
            IsSelectable = false,
            UnavailableStateText = "Not installed · install on explicit request",
        },
    ];

    public static CatalogProvider? Find(string idOrDisplayName) => Default.FirstOrDefault(provider =>
        string.Equals(provider.Id, idOrDisplayName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider.DisplayName, idOrDisplayName, StringComparison.OrdinalIgnoreCase));

    public static StatusSeverity SeverityFor(CatalogProvider provider, bool hasCredential) =>
        !provider.IsSelectable || !provider.RequiresCredential ? StatusSeverity.Neutral
        : hasCredential ? StatusSeverity.Success
        : StatusSeverity.Warning;
}
