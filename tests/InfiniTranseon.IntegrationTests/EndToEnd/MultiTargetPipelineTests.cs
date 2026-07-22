using System.Runtime.CompilerServices;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.IntegrationTests.EndToEnd;

public sealed class MultiTargetPipelineTests
{
    [Fact]
    public async Task TwoTargetsEachConvergeToFourIndependentFixedSlots()
    {
        ProviderRegistration[] registrations = Enumerable.Range(0, 4)
            .Select(index => new ProviderRegistration(
                new ProviderDescriptor(
                    $"provider.{index}",
                    ProviderKind.Translation,
                    RequiresNetwork: false,
                    SupportsStreaming: true,
                    SupportsContext: false,
                    SupportsGlossary: false),
                () => new DeterministicProvider($"译{index}")))
            .ToArray();
        var service = new OnlineProviderService(
            new ProviderRegistry(registrations),
            new ProviderServiceLimits(MaximumConcurrentCalls: 8));
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(service));
        Guid epoch = Guid.NewGuid();

        OverlayDesiredState[] results = await Task.WhenAll(
            RunTargetAsync(orchestrator, epoch, new TargetInstanceId(Guid.NewGuid()), "attack 100"),
            RunTargetAsync(orchestrator, epoch, new TargetInstanceId(Guid.NewGuid()), "health 200"));

        Assert.Equal(2, results.Select(item => item.TargetInstanceId).Distinct().Count());
        foreach (OverlayDesiredState result in results)
        {
            OverlayRegionSnapshot region = Assert.Single(result.Regions);
            Assert.Equal(4, region.OrderedSlots.Count);
            Assert.Equal([0, 1, 2, 3], region.OrderedSlots.Select(item => item.Order));
            Assert.All(region.OrderedSlots, slot => Assert.Equal(OverlaySlotState.Success, slot.State));
            Assert.All(region.OrderedSlots, slot => Assert.NotEmpty(slot.Text));
        }
    }

    [Fact]
    public async Task FourChannelsEachApplyTwoRefinementsAndRejectAnOlderSourceGeneration()
    {
        var registrations = new List<ProviderRegistration>();
        for (int channel = 0; channel < 4; channel++)
        {
            registrations.Add(Registration($"initial.{channel}", $"I{channel}"));
            registrations.Add(Registration($"refine-one.{channel}", $"R1-{channel}"));
            registrations.Add(Registration($"refine-two.{channel}", $"R2-{channel}"));
        }
        var service = new OnlineProviderService(
            new ProviderRegistry(registrations),
            new ProviderServiceLimits(MaximumConcurrentCalls: 16));
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(service));
        Guid epoch = Guid.NewGuid();
        var target = new TargetInstanceId(Guid.NewGuid());
        Guid regionId = Guid.NewGuid();
        var currentSource = Source(epoch, target, regionId, generation: 2, "attack 100");
        TranslationChannelDefinition[] channels = Enumerable.Range(0, 4)
            .Select(channel => new TranslationChannelDefinition(
                new TranslationChannelId(Guid.NewGuid()),
                $"initial.{channel}",
                [],
                [
                    new RefinementStepDefinition(
                        Guid.NewGuid(), $"refine-one.{channel}", "polish-one"),
                    new RefinementStepDefinition(
                        Guid.NewGuid(), $"refine-two.{channel}", "polish-two"),
                ],
                new ContextPolicy(false, false, false),
                new CachePolicy(false, false, false),
                new DisplaySlotDefinition(Guid.NewGuid(), channel, $"T{channel}")))
            .ToArray();
        var coordinator = new OverlayStateCoordinator(epoch, target);
        coordinator.BeginRegion(
            regionId,
            currentSource.SourceToken,
            new OverlayPixelRect(100, 700, 800, 200),
            Style(),
            channels);

        await foreach (TranslationOutput output in orchestrator.RunAsync(
                           currentSource,
                           channels,
                           Options(),
                           TestContext.Current.CancellationToken))
            coordinator.TryApply(output, out _);

        OverlayDesiredState completed = coordinator.Snapshot();
        OverlayRegionSnapshot region = Assert.Single(completed.Regions);
        Assert.Equal(4, region.OrderedSlots.Count);
        Assert.All(region.OrderedSlots, slot =>
        {
            Assert.Equal(OverlaySlotState.Success, slot.State);
            Assert.Equal(3, slot.StageIndex);
            Assert.StartsWith("R2-", slot.Text, StringComparison.Ordinal);
        });
        long completedRevision = completed.OverlayRevision;
        TextGeneration oldSource = Source(epoch, target, regionId, generation: 1, "old text");
        var oldChannel = new ChannelExecutionToken(
            oldSource.SourceToken,
            channels[0].Id,
            Guid.NewGuid(),
            channels[0].DisplaySlot.SlotId);
        var stale = new TranslationOutput(
            channels[0].Id,
            new StageExecutionToken(oldChannel, Guid.NewGuid(), 1, 1, 1),
            channels[0].DisplaySlot.SlotId,
            Guid.NewGuid(),
            1,
            1,
            TranslationStage.Initial,
            "stale",
            "initial.0",
            TimeSpan.Zero,
            null,
            null,
            false,
            true,
            null,
            null,
            null);

        Assert.False(coordinator.TryApply(stale, out _));
        Assert.Equal(completedRevision, coordinator.Snapshot().OverlayRevision);

        static ProviderRegistration Registration(string id, string prefix) => new(
            new ProviderDescriptor(
                id,
                ProviderKind.Translation,
                RequiresNetwork: false,
                SupportsStreaming: true,
                SupportsContext: false,
                SupportsGlossary: false),
            () => new DeterministicProvider(prefix));
    }

    private static async Task<OverlayDesiredState> RunTargetAsync(
        TranslationOrchestrator orchestrator,
        Guid epoch,
        TargetInstanceId target,
        string text)
    {
        Guid regionId = Guid.NewGuid();
        TextGeneration source = Source(epoch, target, regionId, generation: 1, text);
        TranslationChannelDefinition[] channels = Enumerable.Range(0, 4)
            .Select(index => new TranslationChannelDefinition(
                new TranslationChannelId(Guid.NewGuid()),
                $"provider.{index}",
                [],
                [],
                new ContextPolicy(false, false, false),
                new CachePolicy(false, false, false),
                new DisplaySlotDefinition(Guid.NewGuid(), index, $"T{index}")))
            .ToArray();
        var coordinator = new OverlayStateCoordinator(epoch, target);
        coordinator.BeginRegion(
            regionId,
            source.SourceToken,
            new OverlayPixelRect(100, 700, 800, 200),
            Style(),
            channels);
        await foreach (TranslationOutput output in orchestrator.RunAsync(
                           source, channels, Options(), TestContext.Current.CancellationToken))
            coordinator.TryApply(output, out _);
        return coordinator.Snapshot();
    }

    private static TextGeneration Source(
        Guid epoch,
        TargetInstanceId target,
        Guid regionId,
        long generation,
        string text)
    {
        var bounds = new NormalizedRect(0.1, 0.7, 0.8, 0.2);
        var sourceToken = new SourceGenerationToken(
            epoch,
            target,
            CaptureAreaKey.UserRegion(new RegionId(regionId)),
            new TextTrackId(Guid.NewGuid()),
            generation,
            1);
        return new TextGeneration(
            new CaptureTargetId(Guid.NewGuid()),
            sourceToken,
            new SourceEventId(Guid.NewGuid()),
            bounds,
            text,
            [new TextLine(text, bounds, 0.99)],
            DateTimeOffset.UtcNow,
            generation,
            generation);
    }

    private static OverlayRegionStyleSnapshot Style() => new(
        OverlayBackgroundTreatment.Translucent,
        OverlayTextAlignment.Left,
        "#CC000000",
        "#FFFFFFFF",
        0.8,
        12,
        8,
        24,
        12,
        4);

    private static TranslationRunOptions Options() => new(
        Guid.NewGuid(),
        new TranslationContext("Game", null, null, null, [], []),
        [],
        TimeSpan.FromSeconds(2),
        200,
        100,
        StrictOffline: true,
        SourceLanguage: "en",
        TargetLanguage: "zh-Hans");

    private sealed class DeterministicProvider(string prefix) : ITranslationProvider
    {
        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            string result = prefix + ":" + request.SourceText;
            yield return new ProviderDelta(1, result);
            yield return new ProviderDone(1, new ProviderUsage(
                request.SourceText.Length, result.Length, "characters"));
        }
    }
}
