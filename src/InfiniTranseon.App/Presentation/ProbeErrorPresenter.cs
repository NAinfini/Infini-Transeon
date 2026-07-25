namespace InfiniTranseon.App.Presentation;

/// <summary>
/// Maps the machine error codes that probes and translation providers return onto resource keys the
/// pages can localize. Codes reach the UI from many provider families ("provider.deepl.authorization",
/// "provider.yandex.authorization", "provider.http401" …), so the mapping is by family rather than by
/// exact string: a provider added later inherits a correct message instead of falling through to the
/// raw code.
///
/// Callers must still show the original code alongside the message
/// (<see cref="Describe"/> does this). Replacing a code with prose would remove the only thing that
/// makes a provider failure diagnosable from a screenshot.
/// </summary>
public static class ProbeErrorPresenter
{
    public const string UnknownResourceKey = "ProbeErrorUnknown";

    /// <summary>The probe threw instead of returning a result. The exception text is carried
    /// separately so the user sees a sentence and the real message, not one or the other.</summary>
    public const string UnexpectedExceptionCode = "translation.probe.unexpectedException";

    public static string ResourceKeyFor(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return errorCode switch
        {
            "translation.probe.providerNotSelected" => "ProbeErrorProviderNotSelected",
            "translation.probe.providerUnknown" => "ProbeErrorProviderUnknown",
            "translation.probe.providerNotRegistered" => "ProbeErrorProviderNotRegistered",
            "translation.probe.noOutput" => "ProbeErrorNoOutput",
            UnexpectedExceptionCode => "ProbeErrorUnexpected",
            "translation.probe.credentialMissing" or "provider.credentialMissing" =>
                "ProbeErrorCredentialMissing",
            "translation.probe.credentialRebindRequired" or
                "provider.credentialReconfirmationRequired" => "ProbeErrorCredentialRebind",
            "provider.policy.strictOffline" or "provider.offlineBlocked" => "ProbeErrorOffline",
            "provider.costBudget" => "ProbeErrorCostBudget",
            "provider.cancelled" => "ProbeErrorCancelled",
            "provider.network" or "provider.streamingDisconnect" => "ProbeErrorNetwork",
            "provider.redirectRejected" => "ProbeErrorRedirectRejected",
            _ => FamilyKeyFor(errorCode),
        };
    }

    /// <summary>Localized sentence with the machine code appended, ready to place in an InfoBar.</summary>
    public static string Describe(string errorCode, Func<string, string> localize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentNullException.ThrowIfNull(localize);
        string message = localize(ResourceKeyFor(errorCode));
        return string.IsNullOrWhiteSpace(message)
            ? errorCode
            : $"{message} ({errorCode})";
    }

    private static string FamilyKeyFor(string errorCode)
    {
        bool Ends(string suffix) => errorCode.EndsWith(suffix, StringComparison.Ordinal);
        return
            Ends(".authorization") || Ends(".forbidden") || Ends("http401") || Ends("http403")
                ? "ProbeErrorAuthorization" :
            Ends(".rateLimited") || Ends("http429") ? "ProbeErrorRateLimited" :
            Ends(".quotaExceeded") ? "ProbeErrorQuotaExceeded" :
            Ends(".requestTooLarge") ? "ProbeErrorRequestTooLarge" :
            Ends(".badRequest") || Ends("http400") ? "ProbeErrorBadRequest" :
            Ends(".unavailable") ? "ProbeErrorUnavailable" :
            Ends(".timeout") || Ends(".deadline") ? "ProbeErrorTimeout" :
            Ends(".server") || Ends("http5xx") || Ends(".remoteError") || Ends(".httpError") ||
                Ends(".error") ? "ProbeErrorServer" :
            errorCode.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
                errorCode.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                ? "ProbeErrorMalformedResponse" :
            errorCode.EndsWith("Limit", StringComparison.Ordinal) ? "ProbeErrorLimitExceeded" :
            UnknownResourceKey;
    }
}
