using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Translation;

public sealed class TranslationOrchestratorTests
{
    [Fact]
    public async Task ThreeChannelsRunIndependentlyIntoFixedSlotsWithoutWinnerSelection()
    {
        using OnlineProviderService providers = CreateProviders(new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
        {
            ["one"] = _ => Success("一"),
            ["two"] = _ => Success("二"),
            ["three"] = _ => Success("三"),
        });
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(providers));
        TranslationChannelDefinition[] channels = [CreateChannel("one", 0), CreateChannel("two", 1), CreateChannel("three", 2)];

        IReadOnlyList<TranslationOutput> output = await CollectAsync(orchestrator.RunAsync(
            CreateSource(), channels, CreateOptions(), TestContext.Current.CancellationToken));

        TranslationOutput[] completed = output.Where(item => item.StreamCompleted).ToArray();
        Assert.Equal(3, completed.Length);
        Assert.Equal(["一", "三", "二"], completed.Select(item => item.Text).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, completed.Select(item => item.ImmutableSlotId).Distinct().Count());
        Assert.All(completed, item => Assert.Equal(TranslationStage.Initial, item.Stage));
    }

    [Fact]
    public async Task RetryThenFallbackAndRefinementReplaceOnlyTheSameSlot()
    {
        int primaryCalls = 0;
        string? refinementInput = null;
        var retryPolicy = new RecordingRetryPolicy();
        using OnlineProviderService providers = CreateProviders(new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
        {
            ["primary"] = _ =>
            {
                primaryCalls++;
                return [new ProviderWireFailure("provider.http429", true)];
            },
            ["fallback"] = _ => Success("基础译文"),
            ["refine"] = request =>
            {
                refinementInput = request.SourceText;
                return Success("润色译文");
            },
        });
        var channel = CreateChannel("primary", 0) with
        {
            FallbackProviderIds = ["fallback"],
            RefinementSteps = [new RefinementStepDefinition(Guid.NewGuid(), "refine", "style")],
        };
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(
            providers,
            retryPolicy: retryPolicy));

        IReadOnlyList<TranslationOutput> output = await CollectAsync(orchestrator.RunAsync(
            CreateSource(), [channel], CreateOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(2, primaryCalls);
        Assert.Equal([1], retryPolicy.FailedAttempts);
        Assert.Equal("基础译文", refinementInput);
        TranslationOutput fallback = Assert.Single(output, item =>
            item.StreamCompleted && item.Stage == TranslationStage.Fallback);
        TranslationOutput refinement = Assert.Single(output, item =>
            item.StreamCompleted && item.Stage == TranslationStage.Refinement);
        Assert.Equal("primary", fallback.FallbackFromProviderId);
        Assert.Equal(fallback.ImmutableSlotId, refinement.ImmutableSlotId);
        Assert.Equal("润色译文", refinement.Text);
    }

    [Fact]
    public void ExponentialRetryDelayIsBoundedAndJittered()
    {
        var options = new ProviderRetryOptions(
            MaximumAttempts: 4,
            BaseDelay: TimeSpan.FromMilliseconds(100),
            MaximumDelay: TimeSpan.FromMilliseconds(350),
            JitterRatio: 0.20);

        Assert.Equal(TimeSpan.FromMilliseconds(80),
            ExponentialJitterRetryPolicy.ComputeDelay(options, 1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(240),
            ExponentialJitterRetryPolicy.ComputeDelay(options, 2, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(350),
            ExponentialJitterRetryPolicy.ComputeDelay(options, 3, 1));
    }

    [Fact]
    public async Task ChannelCanDisableRetriesWithoutDisablingFallback()
    {
        int primaryCalls = 0;
        using OnlineProviderService providers = CreateProviders(new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
        {
            ["primary"] = _ =>
            {
                primaryCalls++;
                return [new ProviderWireFailure("provider.http429", true)];
            },
            ["fallback"] = _ => Success("fallback"),
        });
        TranslationChannelDefinition channel = CreateChannel("primary", 0) with
        {
            RetryCount = 0,
            FallbackProviderIds = ["fallback"],
        };
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(providers));

        IReadOnlyList<TranslationOutput> output = await CollectAsync(orchestrator.RunAsync(
            CreateSource(), [channel], CreateOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(1, primaryCalls);
        Assert.Contains(output, item => item.StreamCompleted &&
            item.Stage == TranslationStage.Fallback && item.Text == "fallback");
    }

    [Fact]
    public async Task ContextPolicyDeniesGameAndHistoryBeforeProviderDispatch()
    {
        TranslationRequest? captured = null;
        using OnlineProviderService providers = CreateProviders(new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
        {
            ["capture"] = request =>
            {
                captured = request;
                return Success("ok");
            },
        });
        TranslationChannelDefinition channel = CreateChannel("capture", 0) with
        {
            Context = new ContextPolicy(false, true, false),
        };
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(providers));

        await CollectAsync(orchestrator.RunAsync(
            CreateSource(), [channel], CreateOptions(), TestContext.Current.CancellationToken));

        Assert.NotNull(captured);
        Assert.Null(captured.Context.GameName);
        Assert.Null(captured.Context.GameDescription);
        Assert.Equal("scene", captured.Context.Scene);
        Assert.Empty(captured.Context.RecentSource);
    }

    [Fact]
    public async Task ChannelAttemptTimeoutOverridesTheProfileDefaultBeforeProviderDispatch()
    {
        TranslationRequest? captured = null;
        using OnlineProviderService providers = CreateProviders(
            new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
            {
                ["capture"] = request =>
                {
                    captured = request;
                    return Success("ok");
                },
            });
        TranslationChannelDefinition channel = CreateChannel("capture", 0) with
        {
            AttemptTimeout = TimeSpan.FromSeconds(11),
        };
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(providers));

        await CollectAsync(orchestrator.RunAsync(
            CreateSource(),
            [channel],
            CreateOptions() with { AttemptTimeout = TimeSpan.FromSeconds(2) },
            TestContext.Current.CancellationToken));

        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromSeconds(11), captured.Timeout);
    }

    [Fact]
    public async Task WorstCaseCostIsReservedBeforeDispatchAndBudgetBlocksTheNextCall()
    {
        int calls = 0;
        using OnlineProviderService providers = CreateProviders(new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
        {
            ["paid"] = _ =>
            {
                calls++;
                return Success("paid result");
            },
        });
        Guid profileId = Guid.NewGuid();
        using var dispatch = new ProviderDispatchCoordinator();
        dispatch.ConfigureBudget(profileId, "paid", "characters", "USD", 0.01m);
        var runner = new TranslationChannelRunner(providers, dispatch);
        TranslationRunOptions options = CreateOptions() with
        {
            ProfileId = profileId,
            MaximumCostPerAttempt = 0.01m,
            Currency = "USD",
        };

        await CollectAsync(runner.RunAsync(
            CreateSource(), CreateChannel("paid", 0), options, TestContext.Current.CancellationToken));
        IReadOnlyList<TranslationOutput> blocked = await CollectAsync(runner.RunAsync(
            CreateSource(), CreateChannel("paid", 1), options, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.Contains(blocked, item => item.TerminalErrorCode == "provider.costBudget");
    }

    [Fact]
    public async Task DisposingDispatchCoordinatorLetsExistingLeaseReleaseSafely()
    {
        var dispatch = new ProviderDispatchCoordinator();
        Guid profileId = Guid.NewGuid();
        var cost = new ProviderCostReservation("characters", 1, null, null);
        ProviderDispatchCoordinator.ProviderDispatchLease lease = await dispatch.AcquireAsync(
            profileId,
            "provider",
            cost,
            TestContext.Current.CancellationToken);

        dispatch.Dispose();

        await lease.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatch.AcquireAsync(
            profileId,
            "provider",
            cost,
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ExactTranslationMemoryHitSkipsProviderAndKeepsTheConfiguredSlot()
    {
        int calls = 0;
        using OnlineProviderService providers = CreateProviders(new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
        {
            ["cached"] = _ => { calls++; return Success("cache me"); },
        });
        var memory = new TranslationMemory(new TranslationMemoryOptions());
        var runner = new TranslationChannelRunner(providers, memory: memory);
        TranslationChannelDefinition channel = CreateChannel("cached", 0);
        TranslationRunOptions options = CreateOptions() with
        {
            MaximumCostPerAttempt = 0.05m,
            Currency = "USD",
        };

        await CollectAsync(runner.RunAsync(CreateSource(), channel, options, TestContext.Current.CancellationToken));
        IReadOnlyList<TranslationOutput> second = await CollectAsync(
            runner.RunAsync(CreateSource(), channel, options, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        TranslationOutput hit = Assert.Single(second);
        Assert.True(hit.CacheHit);
        Assert.True(hit.StreamCompleted);
        Assert.Equal(channel.DisplaySlot.SlotId, hit.ImmutableSlotId);
        Assert.Equal("cache me", hit.Text);
        Assert.Equal(0m, hit.EstimatedCost);
        Assert.Null(hit.CostCurrency);
        Assert.False(hit.EstimateOnly);
    }

    [Fact]
    public async Task ExactManualCorrectionBypassesProvider()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "InfiniTranseonTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            int providerCalls = 0;
            using OnlineProviderService providers = CreateProviders(
                new Dictionary<string, Func<TranslationRequest, ProviderWireEvent[]>>
                {
                    ["primary"] = _ =>
                    {
                        providerCalls++;
                        return Success("provider result");
                    },
                });
            TextGeneration source = CreateSource();
            TranslationRunOptions options = CreateOptions();
            var corrections = new CorrectionStore(Path.Combine(directory, "corrections.db"));
            await corrections.AddAsync(
                new CorrectionScope(
                    options.ProfileId,
                    source.SourceToken.Area.UserRegionId!.Value,
                    options.SourceLanguage,
                    options.TargetLanguage,
                    options.GlossaryVersion),
                source.SourceText,
                "manual result",
                TestContext.Current.CancellationToken);
            var runner = new TranslationChannelRunner(providers, corrections: corrections);

            IReadOnlyList<TranslationOutput> outputs = await CollectAsync(runner.RunAsync(
                source,
                CreateChannel("primary", 0),
                options,
                TestContext.Current.CancellationToken));

            TranslationOutput result = Assert.Single(outputs);
            Assert.Equal("manual result", result.Text);
            Assert.Equal("correction.manual", result.ProviderId);
            Assert.True(result.CacheHit);
            Assert.Equal(0, providerCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OnlineProviderService CreateProviders(
        IReadOnlyDictionary<string, Func<TranslationRequest, ProviderWireEvent[]>> scripts)
    {
        ProviderRegistration[] registrations = scripts.Select(item => new ProviderRegistration(
            ProviderDescriptor.Online(item.Key, ProviderKind.Translation),
            () => new ScriptProvider(item.Value))).ToArray();
        return new OnlineProviderService(new ProviderRegistry(registrations), new ProviderServiceLimits());
    }

    private static TranslationChannelDefinition CreateChannel(string providerId, int order) => new(
        new TranslationChannelId(Guid.NewGuid()),
        providerId,
        [],
        [],
        new ContextPolicy(true, true, true),
        new CachePolicy(true, false, false),
        new DisplaySlotDefinition(Guid.NewGuid(), order, providerId));

    private static TranslationRunOptions CreateOptions() => new(
        Guid.NewGuid(),
        new TranslationContext(
            "game", "description", "scene", "speaker", ["old source"], ["old translation"]),
        [],
        TimeSpan.FromSeconds(2),
        1000,
        500,
        false,
        SourceLanguage: "en",
        TargetLanguage: "zh-Hans");

    private static TextGeneration CreateSource()
    {
        var sourceToken = new SourceGenerationToken(
            Guid.NewGuid(),
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.UserRegion(new RegionId(Guid.NewGuid())),
            new TextTrackId(Guid.NewGuid()),
            1,
            1);
        return new TextGeneration(
            new CaptureTargetId(Guid.NewGuid()),
            sourceToken,
            new SourceEventId(Guid.NewGuid()),
            new NormalizedRect(0, 0, 0.5, 0.2),
            "source",
            [],
            DateTimeOffset.UtcNow,
            1,
            1);
    }

    private static ProviderWireEvent[] Success(string text) =>
        [new ProviderDelta(1, text), new ProviderDone(1, ProviderUsage.None)];

    private static async Task<IReadOnlyList<TranslationOutput>> CollectAsync(
        IAsyncEnumerable<TranslationOutput> source)
    {
        var output = new List<TranslationOutput>();
        await foreach (TranslationOutput item in source) output.Add(item);
        return output;
    }

    private sealed class ScriptProvider(Func<TranslationRequest, ProviderWireEvent[]> script) : ITranslationProvider
    {
        public async IAsyncEnumerable<ProviderWireEvent> StreamAsync(
            TranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (ProviderWireEvent item in script(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingRetryPolicy : IProviderRetryPolicy
    {
        public int MaximumAttempts => 2;
        public List<int> FailedAttempts { get; } = [];

        public ValueTask WaitBeforeRetryAsync(int failedAttempt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FailedAttempts.Add(failedAttempt);
            return ValueTask.CompletedTask;
        }
    }
}
