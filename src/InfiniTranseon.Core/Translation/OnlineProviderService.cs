using System.Runtime.CompilerServices;
using System.Text;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation;

public sealed record ProviderServiceLimits(
    int MaximumConcurrentCalls = 8,
    int MaximumEventsPerCall = 4096,
    int MaximumErrorCodeLength = 128);

public sealed class OnlineProviderService : IDisposable
{
    private readonly ProviderRegistry _registry;
    private readonly ProviderServiceLimits _limits;
    private readonly SemaphoreSlim _concurrency;
    private int _disposed;

    public OnlineProviderService(ProviderRegistry registry, ProviderServiceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumConcurrentCalls, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.MaximumConcurrentCalls, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumEventsPerCall, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.MaximumEventsPerCall, 65_536);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumErrorCodeLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.MaximumErrorCodeLength, 128);
        _registry = registry;
        _limits = limits;
        _concurrency = new SemaphoreSlim(limits.MaximumConcurrentCalls, limits.MaximumConcurrentCalls);
    }

    public bool TryGetDescriptor(string providerId, out ProviderDescriptor? descriptor)
    {
        if (_registry.TryGet(providerId, out ProviderRegistration? registration) && registration is not null)
        {
            descriptor = registration.Descriptor;
            return true;
        }
        descriptor = null;
        return false;
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        string providerId,
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        if (!_registry.TryGet(providerId, out ProviderRegistration? registration) || registration is null)
        {
            yield return Failed(request.Execution, 1, "provider.unknown", false);
            yield break;
        }
        if (request.StrictOffline && registration.Descriptor.RequiresNetwork)
        {
            yield return Failed(request.Execution, 1, "provider.offlineBlocked", false);
            yield break;
        }

        bool entered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        ProviderEvent? waitFailure = null;
        try
        {
            await _concurrency.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
        }
        catch (OperationCanceledException)
        {
            waitFailure = cancellationToken.IsCancellationRequested
                ? Cancelled(request.Execution, 1, "provider.cancelled")
                : Failed(request.Execution, 1, "provider.deadline", true);
        }
        if (waitFailure is not null)
        {
            yield return waitFailure;
            yield break;
        }

        long revision = 0;
        long expectedDelta = 1;
        int eventCount = 0;
        var cumulative = new StringBuilder(Math.Min(request.MaximumOutputCharacters, 4096));
        ProviderWireEvent? terminal = null;
        string? protocolFailure = null;
        ITranslationProvider? provider;
        try
        {
            provider = registration.Factory() ??
                throw new InvalidOperationException("Provider factory returned null.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            protocolFailure = "provider.adapterFault";
            provider = null;
        }

        try
        {
            if (provider is not null)
            {
                await using IAsyncEnumerator<ProviderWireEvent> enumerator = provider
                    .StreamAsync(request, deadline.Token)
                    .GetAsyncEnumerator(deadline.Token);
                while (protocolFailure is null)
                {
                    bool moved = false;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        terminal = cancellationToken.IsCancellationRequested
                            ? new ProviderWireCancelled("provider.cancelled")
                            : new ProviderWireFailure("provider.deadline", true);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        terminal = new ProviderWireFailure("provider.adapterFault", false);
                    }
                    if (!moved) break;

                    ProviderWireEvent wireEvent = enumerator.Current;
                    eventCount++;
                    if (eventCount > _limits.MaximumEventsPerCall)
                    {
                        protocolFailure = "provider.eventLimit";
                        break;
                    }
                    if (terminal is not null)
                    {
                        protocolFailure = "provider.duplicateTerminal";
                        continue;
                    }

                    switch (wireEvent)
                    {
                        case ProviderDelta delta:
                            if (delta.ProviderDeltaSequence != expectedDelta)
                            {
                                protocolFailure = "provider.deltaGap";
                                break;
                            }
                            expectedDelta++;
                            if (delta.Text is null || cumulative.Length + delta.Text.Length > request.MaximumOutputCharacters)
                            {
                                protocolFailure = "provider.outputLimit";
                                break;
                            }
                            cumulative.Append(delta.Text);
                            revision++;
                            yield return new ProviderSnapshot(
                                WithRevision(request.Execution, revision),
                                delta.ProviderDeltaSequence,
                                cumulative.ToString());
                            break;
                        case ProviderDone done:
                            if (done.LastProviderDeltaSequence != expectedDelta - 1)
                            {
                                protocolFailure = "provider.deltaTerminalMismatch";
                            }
                            terminal = done;
                            break;
                        case ProviderWireFailure failure:
                            terminal = NormalizeFailure(failure);
                            break;
                        case ProviderWireCancelled cancelled:
                            terminal = cancelled;
                            break;
                        default:
                            protocolFailure = "provider.unknownEvent";
                            break;
                    }
                }
            }
        }
        finally
        {
            if (provider is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (provider is IDisposable disposable)
                disposable.Dispose();
            if (entered) _concurrency.Release();
        }

        revision++;
        StageExecutionToken terminalExecution = WithRevision(request.Execution, revision);
        if (protocolFailure is not null)
        {
            yield return new ProviderFailed(terminalExecution, protocolFailure, false);
            yield break;
        }
        switch (terminal)
        {
            case ProviderDone done:
                yield return new ProviderCompleted(
                    terminalExecution,
                    done.LastProviderDeltaSequence,
                    cumulative.ToString(),
                    done.Usage);
                break;
            case ProviderWireFailure failure:
                yield return new ProviderFailed(terminalExecution, failure.ErrorCode, failure.Retryable);
                break;
            case ProviderWireCancelled cancelled:
                yield return new ProviderCancelled(terminalExecution, cancelled.ReasonCode);
                break;
            default:
                yield return new ProviderFailed(terminalExecution, "provider.missingTerminal", false);
                break;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private ProviderWireFailure NormalizeFailure(ProviderWireFailure failure)
    {
        string code = string.IsNullOrWhiteSpace(failure.ErrorCode) ||
            failure.ErrorCode.Length > _limits.MaximumErrorCodeLength
            ? "provider.invalidErrorCode"
            : failure.ErrorCode;
        return failure with { ErrorCode = code };
    }

    private static void ValidateRequest(TranslationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Timeout, TimeSpan.FromMinutes(5));
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumOutputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MaximumOutputCharacters, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumOutputTokens, 1);
    }

    private static StageExecutionToken WithRevision(StageExecutionToken token, long revision) => new(
        token.Channel,
        token.StageId,
        token.StageSequence,
        token.Attempt,
        revision);

    private static ProviderFailed Failed(
        StageExecutionToken token,
        long revision,
        string code,
        bool retryable) => new(WithRevision(token, revision), code, retryable);

    private static ProviderCancelled Cancelled(
        StageExecutionToken token,
        long revision,
        string code) => new(WithRevision(token, revision), code);
}
