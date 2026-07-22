using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.IntegrationTests.Privacy;

public sealed class StrictOfflineTests
{
    [Fact]
    public async Task StrictOfflineRejectsBeforeOnlineProviderFactoryRuns()
    {
        int factoryCalls = 0;
        var registry = new ProviderRegistry([
            new ProviderRegistration(
                new ProviderDescriptor(
                    "online",
                    ProviderKind.Translation,
                    RequiresNetwork: true,
                    SupportsStreaming: false,
                    SupportsContext: false,
                    SupportsGlossary: false),
                () =>
                {
                    factoryCalls++;
                    throw new InvalidOperationException("Network provider must not be constructed.");
                }),
        ]);
        var service = new OnlineProviderService(registry, new ProviderServiceLimits(1));

        IReadOnlyList<ProviderEvent> events = await CollectAsync(service.StreamAsync(
            "online", Request(strictOffline: true), TestContext.Current.CancellationToken));

        Assert.Equal(0, factoryCalls);
        Assert.Equal("provider.offlineBlocked", Assert.IsType<ProviderFailed>(Assert.Single(events)).ErrorCode);
    }

    private static TranslationRequest Request(bool strictOffline)
    {
        var source = new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.UserRegion(new RegionId(Guid.NewGuid())),
            new TextTrackId(Guid.NewGuid()),
            1,
            1);
        var channel = new ChannelExecutionToken(
            source, new TranslationChannelId(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid());
        return new TranslationRequest(
            "hello", "en", "zh-Hans",
            new TranslationContext(null, null, null, null, [], []),
            [],
            new StageExecutionToken(channel, Guid.NewGuid(), 1, 1, 1),
            TimeSpan.FromSeconds(1),
            "idempotency",
            100,
            100,
            new ProviderCostReservation("characters", 5, null, null),
            strictOffline);
    }

    private static async Task<IReadOnlyList<ProviderEvent>> CollectAsync(
        IAsyncEnumerable<ProviderEvent> source)
    {
        var result = new List<ProviderEvent>();
        await foreach (ProviderEvent item in source) result.Add(item);
        return result;
    }
}
