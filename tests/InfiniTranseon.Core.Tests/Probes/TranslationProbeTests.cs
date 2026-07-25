using System.Runtime.CompilerServices;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Privacy;
using InfiniTranseon.Core.Probes;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Probes;

public sealed class TranslationProbeTests
{
    private const string ProviderId = "probe-provider";

    private static readonly CredentialBinding Binding = new(
        ProviderId, "translation", "https", "api.example.com", 443, "bearer", ProxyPolicy.None);

    private static readonly TranslationProbeRequest Request =
        new("hello", "en", "zh", Context: null);

    [Fact]
    public async Task ReportsProviderNotRegisteredWhenRegistryLacksProvider()
    {
        var probe = new TranslationProbe(new ProviderRegistry([]), ProviderId);

        TranslationProbeResult result = await probe.TranslateAsync(
            Request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslationProbe.ProviderNotRegisteredCode, result.ErrorCode);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task ReportsCredentialMissingWhenSecretIsAbsent()
    {
        var registry = Registry(new StubProvider(new ProviderDone(0, ProviderUsage.None)));
        var probe = new TranslationProbe(
            registry, ProviderId, credentialStore: new NullSecretStore(),
            credentialBinding: Binding, credentialReference: "ref");

        TranslationProbeResult result = await probe.TranslateAsync(
            Request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslationProbe.CredentialMissingCode, result.ErrorCode);
    }

    [Fact]
    public async Task ReportsRebindRequiredWhenSecretExistsWithoutBindingMetadata()
    {
        var registry = Registry(new StubProvider(new ProviderDone(0, ProviderUsage.None)));
        var inner = new MemoryCredentialStore();
        await inner.WriteSecretAsync(
            "ref", "orphaned-secret", TestContext.Current.CancellationToken);
        var store = new BoundCredentialStore(inner);
        var probe = new TranslationProbe(
            registry, ProviderId, credentialStore: store,
            credentialBinding: Binding, credentialReference: "ref");

        TranslationProbeResult result = await probe.TranslateAsync(
            Request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslationProbe.CredentialRebindCode, result.ErrorCode);
    }

    [Fact]
    public async Task AggregatesProviderDeltasOnSuccess()
    {
        var registry = Registry(new StubProvider(
            new ProviderDelta(0, "Ni"),
            new ProviderDelta(1, "hao"),
            new ProviderDone(1, ProviderUsage.None)));
        var probe = new TranslationProbe(registry, ProviderId);

        TranslationProbeResult result = await probe.TranslateAsync(
            Request, TestContext.Current.CancellationToken);

        Assert.Null(result.ErrorCode);
        Assert.Equal("Nihao", result.Text);
        Assert.Equal(ProviderId, result.ProviderId);
    }

    [Fact]
    public async Task SurfacesProviderWireFailure()
    {
        var registry = Registry(new StubProvider(
            new ProviderWireFailure("provider.rate.limited", true)));
        var probe = new TranslationProbe(registry, ProviderId);

        TranslationProbeResult result = await probe.TranslateAsync(
            Request, TestContext.Current.CancellationToken);

        Assert.Equal("provider.rate.limited", result.ErrorCode);
    }

    [Fact]
    public async Task ReportsNoOutputWhenStreamEndsWithoutDone()
    {
        var registry = Registry(new StubProvider(new ProviderDelta(0, "partial")));
        var probe = new TranslationProbe(registry, ProviderId);

        TranslationProbeResult result = await probe.TranslateAsync(
            Request, TestContext.Current.CancellationToken);

        Assert.Equal(TranslationProbe.NoOutputCode, result.ErrorCode);
    }

    private static ProviderRegistry Registry(ITranslationProvider provider) =>
        new([new ProviderRegistration(
            ProviderDescriptor.Online(ProviderId, ProviderKind.Translation),
            () => provider)]);

    private sealed class StubProvider(params ProviderWireEvent[] events) : ITranslationProvider
    {
        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (ProviderWireEvent wireEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return wireEvent;
                await Task.Yield();
            }
        }
    }

    private sealed class NullSecretStore : IBoundCredentialStore
    {
        public ValueTask WriteAsync(
            string reference, string secret, CredentialBinding binding,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<string?> ReadAsync(
            string reference, CredentialBinding expectedBinding,
            CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

        public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
