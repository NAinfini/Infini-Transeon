using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using InfiniTranseon.Core.Diagnostics;
using InfiniTranseon.Core.Storage;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Diagnostics;

public sealed class DiagnosticsStorageTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(365001)]
    public void EnabledHistoryRejectsInvalidRetentionDays(int days)
    {
        using var temp = new TempDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryRepository(
            Path.Combine(temp.Path, "history.db"),
            new HistoryOptions(Enabled: true, Retention: TimeSpan.FromDays(days))));
    }

    [Fact]
    public async Task DisabledHistoryWritesNoDatabaseWhileRecentBufferStillWorksAndClears()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "history.db");
        var history = new HistoryRepository(database, new HistoryOptions(Enabled: false));
        Guid profile = Guid.NewGuid();
        var record = CreateRecord(profile, DateTimeOffset.UtcNow);
        using var recent = new RecentTranslationBuffer();

        await history.SaveAsync(record, TestContext.Current.CancellationToken);
        recent.Add(profile, new RecentTranslationEntry(
            record.SourceEventId, record.SourceText, record.Results.Select(item => item.Text).ToArray(), record.CapturedAtUtc));

        Assert.False(File.Exists(database));
        Assert.Single(recent.Snapshot(profile));
        recent.Clear(profile);
        Assert.Empty(recent.Snapshot(profile));
    }

    [Fact]
    public async Task HistoryUsesStableKeysetPaginationAndProfileIsolation()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "history.db");
        var history = new HistoryRepository(database, new HistoryOptions(Enabled: true));
        Guid profile = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < 5; index++)
            await history.SaveAsync(CreateRecord(profile, now.AddMinutes(-index)), TestContext.Current.CancellationToken);
        await history.SaveAsync(CreateRecord(Guid.NewGuid(), now.AddMinutes(1)), TestContext.Current.CancellationToken);

        HistoryPage first = await history.ReadPageAsync(profile, 2, null, TestContext.Current.CancellationToken);
        HistoryPage second = await history.ReadPageAsync(profile, 2, first.NextCursor, TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.NotNull(second.NextCursor);
        Assert.Empty(first.Items.Select(item => item.SourceEventId)
            .Intersect(second.Items.Select(item => item.SourceEventId)));
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal(profile, item.ProfileId));
    }

    [Fact]
    public async Task HistoryByteLimitEvictsOldRowsInOneNewestFirstBoundary()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "history.db");
        var history = new HistoryRepository(
            database,
            new HistoryOptions(Enabled: true, MaximumBytes: 1024));
        Guid profile = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 8)
            .Select(index => CreateRecord(profile, now.AddSeconds(index)) with
            {
                SourceText = new string((char)('a' + index), 300),
            })
            .ToArray();

        foreach (HistoryRecord record in records)
            await history.SaveAsync(record, TestContext.Current.CancellationToken);

        HistoryPage page = await history.ReadPageAsync(
            profile, 20, null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(page.Items);
        Assert.True(page.Items.Count < records.Length);
        Assert.Equal(records[^1].SourceEventId, page.Items[0].SourceEventId);
        Assert.DoesNotContain(page.Items, item => item.SourceEventId == records[0].SourceEventId);
    }

    [Fact]
    public async Task HistoryExportIsVersionedAtomicCancellableAndProfileClearable()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "history.db");
        string export = Path.Combine(temp.Path, "history.jsonl");
        var history = new HistoryRepository(database, new HistoryOptions(Enabled: true));
        Guid profile = Guid.NewGuid();
        await history.SaveAsync(
            CreateRecord(profile, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await history.ExportJsonLinesAsync(
            profile, export, TestContext.Current.CancellationToken);
        string[] lines = await File.ReadAllLinesAsync(
            export, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"schemaVersion\":1", lines[0], StringComparison.Ordinal);
        Assert.Contains("private source", lines[1], StringComparison.Ordinal);

        string cancelled = Path.Combine(temp.Path, "cancelled.jsonl");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            history.ExportJsonLinesAsync(profile, cancelled, cancellation.Token).AsTask());
        Assert.False(File.Exists(cancelled));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "cancelled.jsonl.partial-*"));

        Assert.Equal(1, await history.ClearProfileAsync(
            profile, TestContext.Current.CancellationToken));
        Assert.Empty((await history.ReadPageAsync(
            profile, 20, null, TestContext.Current.CancellationToken)).Items);
    }

    [Fact]
    public async Task StatusLogRejectsTextFieldsAndFreeFormStringSmuggling()
    {
        using var temp = new TempDirectory();
        await using var log = new StatusEventLog(temp.Path, maximumFileBytes: 1024, maximumFiles: 2);
        await Assert.ThrowsAsync<ArgumentException>(() => log.WriteAsync(new StatusEvent(
            DateTimeOffset.UtcNow,
            "provider",
            "provider.failed",
            "status.provider.failed",
            StatusEventSeverity.Error,
            new Dictionary<string, object?> { ["sourceText"] = "private" }),
            TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(() => log.WriteAsync(new StatusEvent(
            DateTimeOffset.UtcNow,
            "provider",
            "provider.failed",
            "status.provider.failed",
            StatusEventSeverity.Error,
            new Dictionary<string, object?>
            {
                ["detail"] = "private OCR text can otherwise be smuggled here",
            }), TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(() => log.WriteAsync(new StatusEvent(
            DateTimeOffset.UtcNow,
            "provider",
            "provider.failed",
            "status.provider.failed",
            StatusEventSeverity.Error,
            new Dictionary<string, object?> { ["detail"] = "Attack" }),
            TestContext.Current.CancellationToken).AsTask());

        await log.WriteAsync(new StatusEvent(
            DateTimeOffset.UtcNow,
            "provider",
            "provider.failed",
            "status.provider.failed",
            StatusEventSeverity.Error,
            new Dictionary<string, object?>
            {
                ["providerId"] = new StatusIdentifier("llm.openai"),
                ["attempt"] = 2,
            }), TestContext.Current.CancellationToken);

        string contents = await File.ReadAllTextAsync(
            Path.Combine(temp.Path, "status-0.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("llm.openai", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("private OCR text", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusLogRejectsAnEventLargerThanItsConfiguredFileBudget()
    {
        using var temp = new TempDirectory();
        await using var log = new StatusEventLog(temp.Path, maximumFileBytes: 1024, maximumFiles: 2);
        IReadOnlyDictionary<string, object?> arguments = Enumerable.Range(0, 32)
            .ToDictionary(
                index => $"field{index}",
                index => (object?)new StatusIdentifier(
                    new string((char)('a' + index % 26), 128)),
                StringComparer.Ordinal);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => log.WriteAsync(
            new StatusEvent(
                DateTimeOffset.UtcNow,
                "runtime",
                "runtime.largeEvent",
                "status.runtime.largeEvent",
                StatusEventSeverity.Warning,
                arguments),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task RuntimeReporterPersistsStateWithoutExceptionOrRecognizedText()
    {
        using var temp = new TempDirectory();
        await using var log = new StatusEventLog(temp.Path);
        var reporter = new RuntimeStatusReporter(log);
        var target = new TargetInstanceId(Guid.NewGuid());
        await reporter.ReportPipelineFailureAsync(
            new RuntimePipelineFailure(
                "pipeline.translationFailed",
                target,
                new TextTrackId(Guid.NewGuid()),
                new InvalidOperationException("private OCR and translation text")),
            TestContext.Current.CancellationToken);
        await reporter.ReportTargetLifecycleAsync(
            new TargetLifecycleEvent(
                new TargetSnapshot(
                    target,
                    new CaptureTargetId(Guid.NewGuid()),
                    TargetLifecycleState.Running,
                    1920,
                    1080,
                    144),
                1,
                DateTimeOffset.UtcNow,
                null),
            TestContext.Current.CancellationToken);

        string contents = await File.ReadAllTextAsync(
            Path.Combine(temp.Path, "status-0.jsonl"),
            TestContext.Current.CancellationToken);
        Assert.Contains("pipeline.translationFailed", contents, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", contents, StringComparison.Ordinal);
        Assert.Contains("Running", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("private OCR", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureBorderDegradationIsLoggedAsAVisibleWarning()
    {
        using var temp = new TempDirectory();
        await using var log = new StatusEventLog(temp.Path);
        var reporter = new RuntimeStatusReporter(log);
        var target = new TargetInstanceId(Guid.NewGuid());

        await reporter.ReportTargetLifecycleAsync(
            new TargetLifecycleEvent(
                new TargetSnapshot(
                    target,
                    new CaptureTargetId(Guid.NewGuid()),
                    TargetLifecycleState.RunningWithCaptureBorder,
                    1920,
                    1080,
                    144),
                1,
                DateTimeOffset.UtcNow,
                null),
            TestContext.Current.CancellationToken);

        string line = Assert.Single(await File.ReadAllLinesAsync(
            Path.Combine(temp.Path, "status-0.jsonl"),
            TestContext.Current.CancellationToken));
        using JsonDocument document = JsonDocument.Parse(line);
        Assert.Equal(
            "capture.state.RunningWithCaptureBorder",
            document.RootElement.GetProperty("ErrorCode").GetString());
        Assert.Equal(
            (int)StatusEventSeverity.Warning,
            document.RootElement.GetProperty("Severity").GetInt32());
    }

    [Fact]
    public async Task CrashReportIsMetadataOnlyLocalAndProvidesRedactionPreview()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "crash.json");
        CrashReportResult result = await CrashReportBuilder.BuildMetadataOnlyAsync(
            path,
            new CrashMetadata(
                DateTimeOffset.UtcNow,
                "1.0.0",
                ".NET 10",
                "runtime.crash",
                ["0x1234", "private dialogue copied into a stack field"],
                [new CrashModule("C:\\Users\\person\\secret-module.dll", "1")],
                new Dictionary<string, object?>
                {
                    ["apiKey"] = "secret-key",
                    ["database"] = "C:\\Users\\person\\data.db",
                    ["detail"] = "private OCR dialogue that must not enter a crash report",
                    ["label"] = "Attack",
                }),
            TestContext.Current.CancellationToken);

        string contents = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.False(result.ContainsMemoryDump);
        Assert.False(result.UploadAvailable);
        Assert.Contains("apiKey", result.RedactionSummary);
        Assert.Contains("detail", result.RedactionSummary);
        Assert.DoesNotContain("secret-key", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private OCR dialogue", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("Attack", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("private dialogue copied", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MemoryDumpRequiresFreshExplicitConsentPrivateAclAndShortRetention()
    {
        using var temp = new TempDirectory();
        var writer = new RecordingDumpWriter();
        var acl = new RecordingPrivateAcl();
        var service = new DiagnosticMemoryDumpService(
            new DiagnosticMemoryDumpOptions(temp.Path, TimeSpan.FromHours(6)),
            writer,
            acl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            Environment.ProcessId,
            new MemoryDumpConsent(false, DateTimeOffset.UtcNow, MemoryDumpConsent.CurrentWarningVersion),
            TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, writer.Calls);

        DiagnosticMemoryDumpResult result = await service.CreateAsync(
            Environment.ProcessId,
            new MemoryDumpConsent(true, DateTimeOffset.UtcNow, MemoryDumpConsent.CurrentWarningVersion),
            TestContext.Current.CancellationToken);

        Assert.True(result.ContainsSensitiveProcessMemory);
        Assert.False(result.UploadAvailable);
        Assert.Equal(1, writer.Calls);
        Assert.True(File.Exists(result.DumpPath));
        Assert.Contains(Path.GetFullPath(temp.Path), result.DumpPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetDirectoryName(result.DumpPath)!, acl.Paths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(result.DumpPath, acl.Paths, StringComparer.OrdinalIgnoreCase);

        File.SetLastWriteTimeUtc(result.DumpPath, DateTime.UtcNow.AddHours(-7));
        int deleted = await service.DeleteExpiredAsync(
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(result.DumpPath));
    }

    private static HistoryRecord CreateRecord(Guid profile, DateTimeOffset captured) => new(
        profile,
        Guid.NewGuid(),
        captured,
        "private source",
        [new HistoryTranslationResult(
            Guid.NewGuid(), "provider", "private translation", "initial", 12, null, null, false, null)]);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InfiniTranseon.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class RecordingDumpWriter : IMemoryDumpWriter
    {
        public int Calls { get; private set; }

        public bool Write(int processId, SafeFileHandle destination, out int nativeError)
        {
            Calls++;
            RandomAccess.Write(destination, "sensitive-dump"u8, 0);
            nativeError = 0;
            return true;
        }
    }

    private sealed class RecordingPrivateAcl : IPrivatePathAcl
    {
        public List<string> Paths { get; } = [];

        public void Apply(string path) => Paths.Add(path);
    }
}
