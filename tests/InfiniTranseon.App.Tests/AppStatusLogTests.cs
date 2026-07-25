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
                new Dictionary<string, object?>
                {
                    ["accessState"] = 1,
                }));
            await log.DisposeAsync();

            IReadOnlyList<DiagnosticEvent> events =
                await new RealDiagnosticsService(options).GetEventsAsync(
                    TestContext.Current.CancellationToken);

            DiagnosticEvent item = Assert.Single(events);
            Assert.Equal("app.startup", item.Scope);
            Assert.Equal("capture.borderless.deniedByUser", item.Title);
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
