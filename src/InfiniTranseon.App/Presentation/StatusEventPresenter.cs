namespace InfiniTranseon.App.Presentation;

/// <summary>
/// Maps the status log's machine vocabulary onto resource keys the activity feed can localize. A
/// status event carries two identifiers with different jobs: the category names the subsystem that
/// emitted it, and the message key names what happened. Both are stable storage strings, so the
/// mapping lives here rather than in the log or in a page.
///
/// Unlike <see cref="ProbeErrorPresenter"/> this mapping is exact, not by family: message keys are
/// authored one per emit site, so a new one is a deliberate addition and must get its own sentence
/// instead of inheriting a neighbour's. The parity tests fail the build when one is missing.
///
/// The event's error code is deliberately not mapped. It is the only field that distinguishes
/// "update.check.started" from "update.download.failed" inside one message key, and the pages show
/// it verbatim so a failure stays diagnosable from a screenshot.
/// </summary>
public static class StatusEventPresenter
{
    public const string UnknownCategoryResourceKey = "StatusCategoryUnknown";
    public const string UnknownMessageResourceKey = "StatusMessageUnknown";

    // Null is a caller defect; an empty string is data — a log line written by a newer build with a
    // field this one does not know — and resolves to the "unrecognized" wording like any other
    // unmapped value.
    public static string CategoryResourceKeyFor(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return category switch
        {
            "app.startup" => "StatusCategoryAppStartup",
            "app.lifecycle" => "StatusCategoryAppLifecycle",
            "app.activation" => "StatusCategoryAppActivation",
            "app.model" => "StatusCategoryAppModel",
            "app.update" => "StatusCategoryAppUpdate",
            "hotkey" => "StatusCategoryHotkey",
            "tray" => "StatusCategoryTray",
            "runtime.control" => "StatusCategoryRuntimeControl",
            "runtime.translationGroup" => "StatusCategoryRuntimeTranslationGroup",
            "runtime.configuration" => "StatusCategoryRuntimeConfiguration",
            "runtime.engine" => "StatusCategoryRuntimeEngine",
            "runtime.capture" => "StatusCategoryRuntimeCapture",
            "runtime.performance" => "StatusCategoryRuntimePerformance",
            "runtime.diagnostic" => "StatusCategoryRuntimeDiagnostic",
            "runtime.state" => "StatusCategoryRuntimeState",
            "runtime.pipeline" => "StatusCategoryRuntimePipeline",
            _ => UnknownCategoryResourceKey,
        };
    }

    public static string MessageResourceKeyFor(string messageKey)
    {
        ArgumentNullException.ThrowIfNull(messageKey);
        return messageKey switch
        {
            "status.capture.borderless.authorization" =>
                "StatusMessageCaptureBorderlessAuthorization",
            "status.app.singleInstance.redirectTimedOut" =>
                "StatusMessageAppSingleInstanceRedirectTimedOut",
            "status.app.shutdown.requested" => "StatusMessageAppShutdownRequested",
            "status.app.closeToTray.failed" => "StatusMessageAppCloseToTrayFailed",
            "status.app.activation.unroutable" => "StatusMessageAppActivationUnroutable",
            "status.app.activation.rejected" => "StatusMessageAppActivationRejected",
            "status.app.activation.startFailed" => "StatusMessageAppActivationStartFailed",
            "status.app.model" => "StatusMessageAppModel",
            "status.app.update" => "StatusMessageAppUpdate",
            "status.hotkey.initialization.failed" => "StatusMessageHotkeyInitializationFailed",
            "status.hotkey.executed" => "StatusMessageHotkeyExecuted",
            "status.hotkey.noMatchingTarget" => "StatusMessageHotkeyNoMatchingTarget",
            "status.hotkey.failed" => "StatusMessageHotkeyFailed",
            "status.tray.operation.failed" => "StatusMessageTrayOperationFailed",
            "status.runtime.control.pause" => "StatusMessageRuntimeControlPause",
            "status.runtime.control.overlay" => "StatusMessageRuntimeControlOverlay",
            "status.runtime.control.manualOcr" => "StatusMessageRuntimeControlManualOcr",
            "status.runtime.control.manualOcrRejected" =>
                "StatusMessageRuntimeControlManualOcrRejected",
            "status.runtime.control.manualOcrFailed" => "StatusMessageRuntimeControlManualOcrFailed",
            "status.runtime.control.noMatchingTarget" =>
                "StatusMessageRuntimeControlNoMatchingTarget",
            "status.runtime.control.scopedApplied" => "StatusMessageRuntimeControlScopedApplied",
            "status.runtime.translationGroup" => "StatusMessageRuntimeTranslationGroup",
            "status.runtime.configuration.hotApplied" =>
                "StatusMessageRuntimeConfigurationHotApplied",
            "status.runtime.configuration.restartFallback" =>
                "StatusMessageRuntimeConfigurationRestartFallback",
            "status.runtime.engine.lifecycle" => "StatusMessageRuntimeEngineLifecycle",
            "status.runtime.capture.lifecycle" => "StatusMessageRuntimeCaptureLifecycle",
            "status.runtime.pipeline.failure" => "StatusMessageRuntimePipelineFailure",
            "status.runtime.performance.budget" => "StatusMessageRuntimePerformanceBudget",
            "status.runtime.performance.degradation" =>
                "StatusMessageRuntimePerformanceDegradation",
            "status.runtime.diagnostic" => "StatusMessageRuntimeDiagnostic",
            "status.runtime.state.admissionRejected" => "StatusMessageRuntimeStateAdmissionRejected",
            _ => UnknownMessageResourceKey,
        };
    }
}
