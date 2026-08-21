using InfiniTranseon.App.Presentation;
using InfiniTranseon.App.Presentation.Services;
using InfiniTranseon.Core.Diagnostics;

namespace InfiniTranseon.App.Tests;

public sealed class AppStatusLogTests
{
    [Fact]
    public async Task StructuredStatusIsPersistedAndVisibleToDiagnostics()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "InfiniTranseonStatusTests", Guid.NewGuid().ToString("N"));
        var options = new AppDataOptions(directory);
        var log = new AppStatusLog(options);
        try
        {
            log.Record(new StatusEvent(
                DateTimeOffset.UtcNow,
                "app.startup",
                "capture.borderless.deniedByUser",
                "status.capture.borderless.authorization",
                StatusEventSeverity.Warning,
                new Dictionary<string, StatusArgument>
                {
                    ["accessState"] = 1,
                }));
            await log.DisposeAsync();

            IReadOnlyList<DiagnosticEvent> events =
                await new RealDiagnosticsService(options).GetEventsAsync(
                    TestContext.Current.CancellationToken);

            DiagnosticEvent item = Assert.Single(events);
            Assert.Equal("app.startup", item.Category);
            Assert.Equal("capture.borderless.deniedByUser", item.ErrorCode);
            Assert.Equal("status.capture.borderless.authorization", item.MessageKey);
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The log is built while the dependency graph is resolved on the UI thread. If its drain loop
    /// captured that context it would only resume on the dispatcher, and the first
    /// <see cref="AppStatusLog.Record"/> past the channel's capacity would block its caller waiting
    /// for a continuation that thread itself had to run. The context here is never pumped, so a
    /// captured one can never make progress.
    /// </summary>
    [Fact]
    public async Task DrainingDoesNotDependOnTheContextThatConstructedTheLog()
    {
        const int Capacity = 1024;
        string directory = Path.Combine(
            Path.GetTempPath(), "InfiniTranseonStatusTests", Guid.NewGuid().ToString("N"));
        var options = new AppDataOptions(directory);
        AppStatusLog log = CreateOnUnpumpedContext(options);
        try
        {
            Task recording = Task.Run(
                () =>
                {
                    for (int index = 0; index < (Capacity * 2); index++)
                    {
                        log.Record(new StatusEvent(
                            DateTimeOffset.UtcNow,
                            "app.startup",
                            "app.singleInstance.redirectTimedOut",
                            "status.app.singleInstance.redirectTimedOut",
                            StatusEventSeverity.Warning,
                            new Dictionary<string, StatusArgument>()));
                    }
                },
                TestContext.Current.CancellationToken);

            try
            {
                await recording.WaitAsync(
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    "Record blocked past the channel capacity: the drain loop only resumes on the " +
                    "context that constructed the log.");
            }

            await log.DisposeAsync();
            IReadOnlyList<DiagnosticEvent> events =
                await new RealDiagnosticsService(options).GetEventsAsync(
                    TestContext.Current.CancellationToken);
            Assert.NotEmpty(events);
        }
        finally
        {
            // A regression leaves the drain loop parked on the unpumped context, so DisposeAsync —
            // which awaits that loop — would never return and would hang the run instead of failing
            // it. The assertion above is what reports the defect; cleanup only has to give up.
            try
            {
                await log.DisposeAsync().AsTask().WaitAsync(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
            {
            }
        }
    }

    private static AppStatusLog CreateOnUnpumpedContext(AppDataOptions options)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new UnpumpedSynchronizationContext());
        try
        {
            return new AppStatusLog(options);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Accepts continuations and never runs them, standing in for a dispatcher whose
    /// thread is busy — or blocked inside <see cref="AppStatusLog.Record"/>.</summary>
    private sealed class UnpumpedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new NotSupportedException();
    }
}
