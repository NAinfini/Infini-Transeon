using InfiniTranseon.App.Controls;
using InfiniTranseon.Core.Privacy;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// A translator provider the control UI can configure: its stable id, display metadata, and — for
/// cloud providers — the credential reference and origin binding used by the Windows Credential
/// Manager store. Local providers require no credential (<see cref="Binding"/> is null).
/// </summary>
public sealed record CatalogProvider(
    string Id,
    string DisplayName,
    string Kind,
    string? CredentialReference,
    CredentialBinding? Binding,
    string Detail)
{
    public bool RequiresCredential => CredentialReference is not null && Binding is not null;
}

/// <summary>
/// The single source of truth for configurable providers, shared by <see cref="RealSettingsService"/>
/// (provider rows) and <see cref="RealSecretReferenceService"/> (credential references and bindings).
/// References and bindings are derived from the SAME definitions the runtime providers read with
/// (<see cref="EngineRuntimeComposition"/>): a secret saved here is byte-for-byte the credential the
/// provider resolves at translation time. Hand-written bindings previously diverged from the
/// providers' own origin checks, which made every saved key unreadable at runtime.
/// </summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<CatalogProvider> Default { get; } =
    [
        // DeepL's declarative definition names its credential reference "api-key" in its templates;
        // the store keys on that reference, so the catalog must use it verbatim.
        new(EngineRuntimeComposition.DeepLDefinition.Id, "DeepL", "NMT · cloud",
            EngineRuntimeComposition.DeepLDefinition.CredentialReferences[0],
            Core.Translation.Rest.DeclarativeRestProvider.CreateBinding(
                EngineRuntimeComposition.DeepLDefinition,
                EngineRuntimeComposition.DeepLDefinition.CredentialReferences[0]),
            "API key stored in Windows Credential Manager"),
        new(EngineRuntimeComposition.OpenAiOptions.ProviderId, "OpenAI compatible", "LLM · cloud",
            EngineRuntimeComposition.OpenAiOptions.CredentialReference,
            Core.Translation.OpenAiCompatibleProvider.CreateCredentialBinding(
                EngineRuntimeComposition.OpenAiOptions),
            "Custom endpoint · streaming · key in Windows Credential Manager"),
        new("translation.local.madlad", "Local MADLAD-400 3B", "NMT · local", null, null,
            "Runs fully offline · no credential required"),
    ];

    public static CatalogProvider? Find(string idOrDisplayName) => Default.FirstOrDefault(provider =>
        string.Equals(provider.Id, idOrDisplayName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider.DisplayName, idOrDisplayName, StringComparison.OrdinalIgnoreCase));

    public static StatusSeverity SeverityFor(CatalogProvider provider, bool hasCredential) =>
        !provider.RequiresCredential ? StatusSeverity.Neutral
        : hasCredential ? StatusSeverity.Success
        : StatusSeverity.Warning;
}
