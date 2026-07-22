using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Ocr;
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
        var orchestrator = new TranslationOrchestrator(new TranslationChannelRunner(options.Providers));

        return (session, publisher, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    records),
                publisher);
            var coordinator = new RuntimeBackendCoordinator(
                session,
                [options.ProfileBinding],
                pipeline,
                // Protocol v1 ships no app-side cloud OCR handler; a request from the engine is a
                // configuration fault and is surfaced as an explicit error diagnostic.
                (runtimeEvent, _) =>
                {
                    publisher.PublishDiagnostic(new EngineDiagnostic(
                        "engine.runtime.cloudOcr.unsupported",
                        "engine.runtime.cloudOcr.unsupported",
                        RuntimeDiagnosticSeverity.Error,
                        DateTimeOffset.UtcNow));
                    return ValueTask.CompletedTask;
                },
                (lifecycle, _) =>
                {
                    publisher.PublishTargetLifecycle(lifecycle);
                    return ValueTask.CompletedTask;
                },
                options.CommandTimeout,
                budgetSnapshot: (snapshot, _) =>
                {
                    publisher.PublishBudget(snapshot);
                    return ValueTask.CompletedTask;
                });
            var control = new DefaultEngineRuntimeControl(pipeline, overlayGate);
            return ValueTask.FromResult(new EngineRuntimeBackendSession(coordinator, control));
        };
    }
}
