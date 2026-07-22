using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Ocr;

public sealed class OcrRoutingException : Exception
{
    public OcrRoutingException(string code, string message) : base(message)
    {
        if (!IsStableCode(code))
            throw new ArgumentException("OCR routing error code is invalid.", nameof(code));
        Code = code;
    }

    public string Code { get; }

    private static bool IsStableCode(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');
}

public sealed class CloudOcrRouter : IDisposable
{
    private sealed class AttemptState(Guid runId, int attempt)
    {
        public Guid RunId { get; } = runId;
        public int Attempt { get; } = attempt;
        public long LastAcceptedSequence { get; set; }
        public bool Pending { get; set; }
    }

    private sealed class ProviderState(IOcrProvider provider)
    {
        public IOcrProvider Provider { get; } = provider;
        public int ActiveLeases { get; set; }
        public bool DisposeWhenIdle { get; set; }
    }

    private sealed class ProviderLease(
        CloudOcrRouter owner,
        string providerId,
        ProviderState state) : IDisposable
    {
        private int _disposed;

        public IOcrProvider Provider => state.Provider;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseProvider(providerId, state);
        }
    }

    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<string, OcrProviderRegistration> _providers;
    private readonly Dictionary<SourceGenerationToken, AttemptState> _attempts = [];
    private readonly Dictionary<string, ProviderState> _providerInstances = new(StringComparer.Ordinal);
    private int _disposed;

    public CloudOcrRouter(IEnumerable<OcrProviderRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        try
        {
            _providers = registrations.ToDictionary(
                registration => registration.ProviderId,
                StringComparer.Ordinal);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException("OCR provider identifiers must be unique.", nameof(registrations), error);
        }
    }

    public OcrExecutionToken BeginAttempt(SourceGenerationToken source, int attempt)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_attempts.TryGetValue(source, out AttemptState? current) && attempt <= current.Attempt)
                throw new OcrRoutingException("ocr.sequence.attemptNotIncreasing", "OCR attempt must increase.");
            var state = new AttemptState(Guid.NewGuid(), attempt);
            _attempts[source] = state;
            return new OcrExecutionToken(source, state.RunId, attempt, 1);
        }
    }

    public OcrExecutionToken NextToken(OcrExecutionToken previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        lock (_gate)
        {
            ThrowIfDisposed();
            AttemptState state = GetCurrentState(previous);
            if (state.Pending || state.LastAcceptedSequence != previous.ResultSequence)
                throw new OcrRoutingException("ocr.sequence.previousNotAccepted", "Previous OCR result is not accepted.");
            return new OcrExecutionToken(
                previous.Source,
                previous.OcrRunId,
                previous.Attempt,
                checked(previous.ResultSequence + 1));
        }
    }

    public async ValueTask<OcrResultSnapshot> RouteAsync(
        string providerId,
        CloudOcrRouteRequest request,
        bool strictOffline,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_providers.TryGetValue(providerId, out OcrProviderRegistration? registration))
            throw new OcrRoutingException("ocr.provider.unknown", $"OCR provider '{providerId}' is not registered.");
        if (strictOffline && registration.RequiresNetwork)
            throw new OcrRoutingException("ocr.policy.strictOffline", "Network OCR is disabled by strict-offline policy.");
        if (!request.ExplicitCloudConsent)
            throw new OcrRoutingException("ocr.policy.cloudConsentRequired", "Cloud OCR crop consent is required.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (request.DeadlineUtc.Offset != TimeSpan.Zero || now >= request.DeadlineUtc ||
            request.DeadlineUtc - now > TimeSpan.FromMinutes(5))
            throw new OcrRoutingException("ocr.deadline.expired", "Cloud OCR crop deadline has expired.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.DeadlineUtc - now);
        ReserveSequence(request.ExecutionToken);
        bool committed = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var authorized = new CloudOcrCropRequest(
                request.ExecutionToken,
                request.MimeType,
                request.EncodedCrop.Span,
                request.PixelWidth,
                request.PixelHeight,
                explicitCloudConsent: true,
                request.ConsentPolicyRevision,
                request.EncodedByteCeiling,
                request.DeadlineUtc,
                providerId);
            using ProviderLease providerLease = AcquireProvider(registration);
            var providerRequest = new CloudOcrProviderRequest(
                authorized.ExecutionToken,
                authorized.MimeType,
                authorized.EncodedCrop,
                authorized.PixelWidth,
                authorized.PixelHeight);
            OcrResultSnapshot result = await providerLease.Provider
                .RecognizeAsync(providerRequest, deadline.Token)
                .ConfigureAwait(false);
            ValidateResult(request.ExecutionToken, result);
            CommitSequence(request.ExecutionToken);
            committed = true;
            return result;
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new OcrRoutingException(
                "ocr.deadline.expired", "Cloud OCR provider exceeded the crop deadline.");
        }
        finally
        {
            if (!committed) ReleaseSequence(request.ExecutionToken);
        }
    }

    public OcrResultSnapshot CompleteFailure(
        OcrExecutionToken executionToken,
        string errorCode)
    {
        ArgumentNullException.ThrowIfNull(executionToken);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _ = new OcrRoutingException(errorCode, "Cloud OCR attempt failed.");
        ReserveSequence(executionToken);
        try
        {
            var result = new OcrResultSnapshot(
                executionToken,
                Array.Empty<TextLine>(),
                "cloud-ocr-router",
                "1",
                false,
                errorCode);
            CommitSequence(executionToken);
            return result;
        }
        catch
        {
            ReleaseSequence(executionToken);
            throw;
        }
    }

    public void Dispose()
    {
        List<IOcrProvider> providers = [];
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach ((string providerId, ProviderState state) in _providerInstances.ToArray())
            {
                if (state.ActiveLeases == 0)
                {
                    _providerInstances.Remove(providerId);
                    providers.Add(state.Provider);
                }
                else
                {
                    state.DisposeWhenIdle = true;
                }
            }
        }
        foreach (IOcrProvider provider in providers) DisposeProvider(provider);
    }

    private ProviderLease AcquireProvider(OcrProviderRegistration registration)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_providerInstances.TryGetValue(registration.ProviderId, out ProviderState? state))
            {
                IOcrProvider provider;
                try
                {
                    provider = registration.Factory() ?? throw new InvalidOperationException();
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not StackOverflowException)
                {
                    throw new OcrRoutingException(
                        "ocr.provider.factoryFailed", "OCR provider factory failed.");
                }
                state = new ProviderState(provider);
                _providerInstances.Add(registration.ProviderId, state);
            }
            state.ActiveLeases = checked(state.ActiveLeases + 1);
            return new ProviderLease(this, registration.ProviderId, state);
        }
    }

    private void ReleaseProvider(string providerId, ProviderState state)
    {
        IOcrProvider? provider = null;
        lock (_gate)
        {
            if (state.ActiveLeases <= 0)
                throw new InvalidOperationException("OCR provider lease underflow.");
            state.ActiveLeases--;
            if (state.ActiveLeases == 0 && state.DisposeWhenIdle)
            {
                _providerInstances.Remove(providerId);
                provider = state.Provider;
            }
        }
        if (provider is not null) DisposeProvider(provider);
    }

    private static void DisposeProvider(IOcrProvider provider)
    {
        if (provider is IDisposable disposable) disposable.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ReserveSequence(OcrExecutionToken token)
    {
        lock (_gate)
        {
            AttemptState state = GetCurrentState(token);
            if (state.Pending || token.ResultSequence != state.LastAcceptedSequence + 1)
                throw new OcrRoutingException("ocr.sequence.outOfOrder", "OCR result sequence is not continuous.");
            state.Pending = true;
        }
    }

    private void CommitSequence(OcrExecutionToken token)
    {
        lock (_gate)
        {
            AttemptState state = GetCurrentState(token);
            if (!state.Pending)
                throw new OcrRoutingException("ocr.sequence.notReserved", "OCR sequence was not reserved.");
            state.LastAcceptedSequence = token.ResultSequence;
            state.Pending = false;
        }
    }

    private void ReleaseSequence(OcrExecutionToken token)
    {
        lock (_gate)
        {
            if (_attempts.TryGetValue(token.Source, out AttemptState? state) &&
                state.RunId == token.OcrRunId && state.Attempt == token.Attempt)
            {
                state.Pending = false;
            }
        }
    }

    private AttemptState GetCurrentState(OcrExecutionToken token)
    {
        if (!_attempts.TryGetValue(token.Source, out AttemptState? state) ||
            state.RunId != token.OcrRunId || state.Attempt != token.Attempt)
        {
            throw new OcrRoutingException("ocr.sequence.staleAttempt", "OCR attempt is no longer current.");
        }
        return state;
    }

    private static void ValidateResult(OcrExecutionToken expectedToken, OcrResultSnapshot result)
    {
        if (result is null)
            throw new OcrRoutingException("ocr.provider.emptyResponse", "OCR provider returned no result.");
        if (result.ExecutionToken != expectedToken)
            throw new OcrRoutingException("ocr.provider.tokenMismatch", "OCR provider returned a mismatched token.");
        if (result.Lines.Count > RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult)
            throw new OcrRoutingException("ocr.provider.tooManyLines", "OCR provider returned too many lines.");
        if (string.IsNullOrWhiteSpace(result.ModelId) || string.IsNullOrWhiteSpace(result.ModelVersion))
            throw new OcrRoutingException("ocr.provider.modelMetadataMissing", "OCR model metadata is required.");
        foreach (TextLine line in result.Lines)
        {
            if (line.Text.Length > RuntimeCapabilities.VersionOne.MaxSourceChars ||
                !double.IsFinite(line.Confidence) || line.Confidence is < 0 or > 1)
            {
                throw new OcrRoutingException("ocr.provider.malformedLine", "OCR provider returned a malformed line.");
            }
        }
    }
}
