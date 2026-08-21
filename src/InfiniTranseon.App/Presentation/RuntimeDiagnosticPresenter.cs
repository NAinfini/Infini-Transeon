namespace InfiniTranseon.App.Presentation;

/// <summary>
/// Maps the engine's diagnostic error codes onto resource keys the pages can localize.
///
/// The engine used to hand the UI a sentence of its own next to the code, built from an exception
/// message or an enum name. That sentence reached the user in English whatever the UI language, and
/// it was free-form text on a surface the user is invited to screenshot. The code is the part that
/// is stable, bounded and safe to carry, so the wording is authored here instead.
///
/// Like <see cref="ProbeErrorPresenter"/> the mapping is by family wherever a family shares one
/// remedy — every degradation cause tells a player the same thing — and exact where the remedy
/// differs. The activity feed shows the code beside the sentence so a diagnostic raised by an engine
/// newer than this build stays identifiable rather than merely unrecognized.
/// </summary>
public static class RuntimeDiagnosticPresenter
{
    public const string UnknownResourceKey = "RuntimeDiagnosticUnknown";
    public const string UnknownCategoryResourceKey = "RuntimeDiagnosticCategoryUnknown";

    /// <summary>Names the subsystem that raised the diagnostic, for the feed's scope column.</summary>
    public static string CategoryResourceKeyFor(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return
            errorCode.StartsWith("pipeline.", StringComparison.Ordinal)
                ? "RuntimeDiagnosticCategoryPipeline" :
            errorCode.StartsWith("performance.", StringComparison.Ordinal)
                ? "RuntimeDiagnosticCategoryPerformance" :
            errorCode.StartsWith("engine.", StringComparison.Ordinal)
                ? "RuntimeDiagnosticCategoryEngine" :
            UnknownCategoryResourceKey;
    }

    public static string ResourceKeyFor(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return errorCode switch
        {
            "pipeline.targetNotRegistered" or "pipeline.targetAlreadyRegistered" or
                "pipeline.targetReplacementInvalid" => "RuntimeDiagnosticTargetBinding",
            "pipeline.staleSource" => "RuntimeDiagnosticStaleSource",
            "pipeline.regionInvalid" or "pipeline.regionNotRegistered" =>
                "RuntimeDiagnosticRegionBinding",
            "pipeline.ocrPostProcessingFailed" => "RuntimeDiagnosticOcrPostProcessingFailed",
            "pipeline.channelsInvalid" => "RuntimeDiagnosticChannelsInvalid",
            "pipeline.overlayClearFailed" or "pipeline.overlayReconciliationFailed" =>
                "RuntimeDiagnosticOverlayFailed",
            "pipeline.translationFailed" => "RuntimeDiagnosticTranslationFailed",
            "engine.runtime.restarting" => "RuntimeDiagnosticEngineRestarting",
            "engine.runtime.restartFailed" => "RuntimeDiagnosticEngineRestartFailed",
            "engine.runtime.stopTeardownFailed" => "RuntimeDiagnosticEngineStopTeardownFailed",
            // Recovery is the one degradation outcome that is good news, so it does not share the
            // "translation slowed down" wording with the causes that provoked it.
            "performance.capacityRecovered" or "performance.recovered" =>
                "RuntimeDiagnosticPerformanceRecovered",
            _ => errorCode.StartsWith("performance.", StringComparison.Ordinal)
                ? "RuntimeDiagnosticPerformanceDegraded"
                : UnknownResourceKey,
        };
    }

    /// <summary>Every resource key this presenter can return, for the parity guard.</summary>
    public static IReadOnlyList<string> AllResourceKeys { get; } =
    [
        UnknownResourceKey,
        UnknownCategoryResourceKey,
        "RuntimeDiagnosticCategoryPipeline",
        "RuntimeDiagnosticCategoryPerformance",
        "RuntimeDiagnosticCategoryEngine",
        "RuntimeDiagnosticTargetBinding",
        "RuntimeDiagnosticStaleSource",
        "RuntimeDiagnosticRegionBinding",
        "RuntimeDiagnosticOcrPostProcessingFailed",
        "RuntimeDiagnosticChannelsInvalid",
        "RuntimeDiagnosticOverlayFailed",
        "RuntimeDiagnosticTranslationFailed",
        "RuntimeDiagnosticEngineRestarting",
        "RuntimeDiagnosticEngineRestartFailed",
        "RuntimeDiagnosticEngineStopTeardownFailed",
        "RuntimeDiagnosticPerformanceRecovered",
        "RuntimeDiagnosticPerformanceDegraded",
    ];
}
