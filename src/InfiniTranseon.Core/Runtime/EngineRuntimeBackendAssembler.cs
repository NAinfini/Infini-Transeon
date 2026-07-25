using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
using InfiniTranseon.Core.Scheduling;
using InfiniTranseon.Core.Settings;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Runtime;

/// <summary>Inputs for assembling the real EngineHost backend over a launched session.</summary>
public sealed record EngineRuntimeBackendOptions(
    RuntimeProfileBinding ProfileBinding,
    OnlineProviderService Providers,
    TimeSpan CommandTimeout,
    TextStabilizerOptions Stabilizer)
{
    /// <summary>Optional persistent sink (history store); outputs always reach the publisher.</summary>
    public IRuntimeTranslationRecordSink? HistorySink { get; init; }

    /// <summary>
    /// Cloud OCR adapters available to EngineHost crop requests. An empty collection remains a
    /// valid, explicit configuration: the router returns <c>ocr.provider.unknown</c> to EngineHost.
    /// </summary>
    public IReadOnlyList<OcrProviderRegistration> CloudOcrProviders { get; init; } = [];

    public TranslationMemory? TranslationMemory { get; init; }

    public CorrectionStore? Corrections { get; init; }

    public PerformanceRuntimeSettings? PerformanceSettings { get; init; }

    public bool ReducedMotion { get; init; }
}

/// <summary>
/// Assembles the concrete backend the <see cref="EngineRuntimeService"/> facade drives: the
/// real translation pipeline (with pause gating), the EngineHost overlay sink (with
/// visibility gating), the record sink fan-out, and the backend coordinator that applies
/// capture/processing configuration and pumps engine events. Every wired piece surfaces
/// failures through the facade publisher; nothing degrades silently.
/// </summary>
public static class EngineRuntimeBackendAssembler
{
    public static EngineRuntimeBackendFactory CreateFactory(EngineRuntimeBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(
            options.Providers,
            memory: options.TranslationMemory,
            corrections: options.Corrections));

        return (session, publisher, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimePerformanceController? performanceController = null;
            if (options.PerformanceSettings is { } performanceSettings &&
                session.MonitoringProcessId > 0 &&
                RuntimePerformanceFactory.CreatePolicies(
                    options.ProfileBinding.Profile).Length > 0)
            {
                PerformanceThresholds thresholds = performanceSettings.Preset ==
                    PerformancePreset.Custom
                    ? performanceSettings.CustomThresholds!
                    : PerformanceThresholds.ForPreset(performanceSettings.Preset);
                var performanceSource = new ProcessPerformanceSnapshotSource(
                    [session.MonitoringProcessId],
                    checked(thresholds.MaximumWorkingSetBytes * 2));
                performanceController = RuntimePerformanceFactory.Create(
                    session,
                    options.ProfileBinding.Profile,
                    options.ProfileBinding.ProfileRevision,
                    performanceSettings,
                    performanceSource,
                    (change, _) =>
                    {
                        publisher.PublishDiagnostic(new EngineDiagnostic(
                            change.CauseCode,
                            $"{change.Kind}; level {change.BeforeLevel} → {change.AfterLevel}; " +
                            $"{change.Changes.Count} region change(s)",
                            change.Kind == DegradationEventKind.Recovered
                                ? RuntimeDiagnosticSeverity.Information
                                : RuntimeDiagnosticSeverity.Warning,
                            DateTimeOffset.UtcNow));
                        return ValueTask.CompletedTask;
                    },
                    options.CommandTimeout);
            }
            var cloudOcrRouter = new CloudOcrRouter(options.CloudOcrProviders);
            HashSet<TargetInstanceId> boundTargets = options.ProfileBinding.Targets
                .Select(target => target.TargetInstanceId)
                .ToHashSet();
            var cloudOcr = new RuntimeCloudOcrDispatcher(
                cloudOcrRouter,
                new RuntimeEngineHostOcrResultSink(session, options.CommandTimeout),
                targetInstanceId => !boundTargets.Contains(targetInstanceId) ||
                    options.ProfileBinding.Profile.StrictOffline);
            var overlayGate = new VisibilityGatingOverlaySink(
                new RuntimeEngineHostOverlaySink(session, options.CommandTimeout));
            var records = new EngineRuntimeTranslationRecordSink(publisher, options.HistorySink);
            var pipeline = new PausableRuntimeTranslationPipeline(
                new RuntimeTranslationPipeline(
                    orchestrator,
                    overlayGate,
                    new OcrTextGenerationGate(options.Stabilizer),
                    (failure, _) =>
                    {
                        publisher.PublishDiagnostic(new EngineDiagnostic(
                            failure.ErrorCode,
                            failure.Exception.Message,
                            RuntimeDiagnosticSeverity.Error,
                            DateTimeOffset.UtcNow));
                        return ValueTask.CompletedTask;
                    },
                    records,
                    degradationPolicy: performanceController?.DegradationPolicy),
                publisher);
            var coordinator = new RuntimeBackendCoordinator(
                session: session,
                profiles: [options.ProfileBinding],
                pipeline: pipeline,
                cloudOcr: cloudOcr.DispatchAsync,
                targetLifecycle: (lifecycle, _) =>
                {
                    publisher.PublishTargetLifecycle(lifecycle);
                    return ValueTask.CompletedTask;
                },
                commandTimeout: options.CommandTimeout,
                performanceControllers: performanceController is null
                    ? []
                    : [performanceController],
                reducedMotion: options.ReducedMotion,
                budgetSnapshot: (snapshot, _) =>
                {
                    publisher.PublishBudget(snapshot);
                    return ValueTask.CompletedTask;
                });
            var control = new DefaultEngineRuntimeControl(
                pipeline,
                overlayGate,
                session,
                options.CommandTimeout,
                coordinator);
            return ValueTask.FromResult(new EngineRuntimeBackendSession(
                new ResourceOwningRuntimeBackend(coordinator, cloudOcrRouter),
                control));
        };
    }

    private sealed class ResourceOwningRuntimeBackend(
        IRecoverableRuntimeBackend inner,
        IDisposable resource) : IRecoverableRuntimeBackend
    {
        private int _disposed;

        public Task Completion => inner.Completion;

        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            inner.StartAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Exception? failure = null;
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
            if (failure is not null) throw failure;
        }
    }
}
