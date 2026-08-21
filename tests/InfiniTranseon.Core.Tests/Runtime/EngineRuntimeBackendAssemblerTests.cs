using System.Runtime.CompilerServices;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Profiles;
using InfiniTranseon.Core.Runtime;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class EngineRuntimeBackendAssemblerTests
{
    [Fact]
    public async Task BuildFailureDisposesLocalOcrProbeCreatedBeforeOwnershipTransfer()
    {
        var probe = new DisposableProbe();
        using var providers = new OnlineProviderService(
            new ProviderRegistry([]),
            new ProviderServiceLimits());
        RuntimeProfileBinding binding = Binding();
        EngineRuntimeBackendFactory factory = EngineRuntimeBackendAssembler.CreateFactory(
            new EngineRuntimeBackendOptions(
                binding,
                providers,
                TimeSpan.FromSeconds(1),
                new TextStabilizerOptions(0, TimeSpan.Zero, TimeSpan.FromSeconds(1)))
            {
                LocalOcrProbeFactory = () => probe,
            });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await factory(
                new StubSession(),
                new StubPublisher(),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, probe.DisposeCount);
    }

    private static RuntimeProfileBinding Binding()
    {
        ProfileDocument profile = ProfileDocument.Create("Game", "ja", "en");
        ProfileTarget target = ProfileTarget.Create("Window", CaptureTargetKind.Window);
        target.Regions.Add(ProfileRegion.Create(
            "Dialogue", new NormalizedRect(0.1, 0.7, 0.8, 0.2)));
        profile.Targets.Add(target);
        var targetBinding = new RuntimeTargetBinding(
            target,
            new TargetInstanceId(Guid.NewGuid()),
            nativeHandle: 1,
            desktopRegion: null,
            targetPixelWidth: 1920,
            targetPixelHeight: 1080,
            commandRevision: 1,
            configurationRevision: 1,
            new TranslationRunOptions(
                profile.ProfileId,
                new TranslationContext(null, null, null, null, [], []),
                [],
                TimeSpan.FromSeconds(1),
                1024,
                512,
                false));
        return new RuntimeProfileBinding(profile, 1, [targetBinding]);
    }

    private sealed class DisposableProbe : IOcrProbe, IDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask<OcrProbeResult> RecognizeAsync(
            OcrProbeRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose() => DisposeCount++;
    }

    private sealed class StubPublisher : IEngineRuntimeEventPublisher
    {
        public void PublishOcrResult(OcrResultSnapshot result) { }
        public void PublishTranslationOutput(Guid profileId, TranslationOutput output) { }
        public void PublishTargetLifecycle(TargetLifecycleEvent lifecycle) { }
        public void PublishBudget(RuntimeBudgetSnapshot snapshot) { }
        public void PublishDiagnostic(EngineDiagnostic diagnostic) { }
    }

    private sealed class StubSession : IRuntimeEngineHostSession
    {
        public Guid RuntimeEpoch { get; } = Guid.NewGuid();

        public async IAsyncEnumerable<RuntimeEngineEvent> ReadEventsAsync(
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<RuntimeCaptureTargetAcknowledgement> ApplyCaptureTargetAsync(
            RuntimeCaptureTargetCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RuntimeOverlayAcknowledgement> ApplyOverlayAsync(
            OverlayDesiredState state,
            TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PolicyAcknowledgement> ApplyPolicyAsync(
            PolicyRevision revision,
            TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RuntimeProcessingConfigurationAcknowledgement>
            ApplyProcessingConfigurationAsync(
                RuntimeProcessingConfiguration configuration,
                TimeSpan timeout,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RuntimeManualOcrAcknowledgement> RequestManualOcrAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RuntimeOcrResultAcknowledgement> SubmitOcrResultAsync(
            OcrResultSnapshot result,
            TimeSpan timeout,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
