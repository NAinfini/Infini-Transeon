using System.Text.Json;
using InfiniTranseon.App.Presentation.Services;

namespace InfiniTranseon.App.Tests;

public sealed class AppCrashReporterTests
{
    [Fact]
    public async Task ReportContainsOnlyLocalRedactedMetadataAndHonorsCountLimit()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "InfiniTranseonCrashTests", Guid.NewGuid().ToString("N"));
        var options = new AppDataOptions(directory);
        using var reporter = new AppCrashReporter(
            options,
            maximumReports: 2,
            retention: TimeSpan.FromDays(30));
        try
        {
            const string privateMessage = "API key secret and C:\\Users\\person\\game.txt";
            for (int index = 0; index < 3; index++)
            {
                await reporter.ReportAsync(
                    new InvalidOperationException(privateMessage),
                    "crash.test.unhandled",
                    isTerminating: true,
                    TestContext.Current.CancellationToken);
            }

            string[] reports = Directory.GetFiles(options.CrashReportDirectory, "crash-*.json");
            Assert.Equal(2, reports.Length);
            string json = await File.ReadAllTextAsync(
                reports[0],
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(privateMessage, json, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\Users\\person", json, StringComparison.OrdinalIgnoreCase);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Assert.False(root.GetProperty("containsMemoryDump").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("uploadEndpoint").ValueKind);
            Assert.Equal("crash.test.unhandled", root.GetProperty("ErrorCode").GetString());
            Assert.True(root.GetProperty("state").GetProperty("isTerminating").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // UI-thread failures reach the reporter through Application.UnhandledException, which is a
    // synchronous callback on a process that is about to die; the write must complete before it
    // returns rather than being left to an awaited continuation that never runs.
    [Fact]
    public void ReportFatalWritesTheReportSynchronously()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "InfiniTranseonCrashTests", Guid.NewGuid().ToString("N"));
        using var reporter = new AppCrashReporter(new AppDataOptions(directory));
        try
        {
            reporter.ReportFatal(new InvalidOperationException("boom"), "crash.ui.unhandled");

            string[] reports = Directory.GetFiles(
                new AppDataOptions(directory).CrashReportDirectory,
                "crash-*.json");
            string json = File.ReadAllText(Assert.Single(reports));
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal(
                "crash.ui.unhandled",
                document.RootElement.GetProperty("ErrorCode").GetString());
            Assert.True(
                document.RootElement.GetProperty("state").GetProperty("isTerminating").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
