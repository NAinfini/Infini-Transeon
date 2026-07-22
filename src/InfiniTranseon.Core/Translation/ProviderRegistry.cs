using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation;

public sealed record ProviderDescriptor(
    string Id,
    ProviderKind Kind,
    bool RequiresNetwork,
    bool SupportsStreaming,
    bool SupportsContext,
    bool SupportsGlossary)
{
    public string ModelId { get; init; } = "configured";

    public static ProviderDescriptor Online(string id, ProviderKind kind, string modelId = "configured") =>
        new(id, kind, true, false, false, false) { ModelId = modelId };
}

public sealed record ProviderRegistration(
    ProviderDescriptor Descriptor,
    Func<ITranslationProvider> Factory);

public sealed class ProviderRegistry
{
    private readonly IReadOnlyDictionary<string, ProviderRegistration> _registrations;

    public ProviderRegistry(IEnumerable<ProviderRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _registrations = registrations.ToDictionary(
            item => item.Descriptor.Id,
            StringComparer.Ordinal);
        if (_registrations.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Provider IDs must be non-empty.", nameof(registrations));
        }
    }

    public IReadOnlyCollection<ProviderDescriptor> Descriptors =>
        _registrations.Values.Select(item => item.Descriptor).ToArray();

    public bool TryGet(string providerId, out ProviderRegistration? registration) =>
        _registrations.TryGetValue(providerId, out registration);
}
