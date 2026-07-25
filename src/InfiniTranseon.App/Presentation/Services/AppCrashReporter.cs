using System.Diagnostics;
using System.Reflection;
using InfiniTranseon.Core.Diagnostics;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Installs process-level handlers that write bounded metadata-only reports to local app data.
/// It deliberately excludes exception messages, source/translated text, file paths, screenshots,
/// environment variables and memory dumps, and it has no upload endpoint.
/// </summary>
public sealed class AppCrashReporter : IDisposable
{
    private readonly string _directory;
    private readonly int _maximumReports;
    private readonly TimeSpan _retention;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _installed;
    private int _disposed;

    public AppCrashReporter(
        AppDataOptions options,
        int maximumReports = 20,
        TimeSpan? retention = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumReports, 1);
        TimeSpan effectiveRetention = retention ?? TimeSpan.FromDays(30);
        if (effectiveRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));
        _directory = Path.GetFullPath(options.CrashReportDirectory);
        _maximumReports = maximumReports;
        _retention = effectiveRetention;
    }

    public void Install()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public async ValueTask<CrashReportResult> ReportAsync(
        Exception exception,
        string errorCode,
        bool isTerminating,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset occurredAtUtc = DateTimeOffset.UtcNow;
            string reportPath = Path.Combine(
                _directory,
                $"crash-{occurredAtUtc:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
            var metadata = new CrashMetadata(
                occurredAtUtc,
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0",
                Environment.Version.ToString(),
                errorCode,
                GetStackAddresses(exception),
                GetModules(),
                new Dictionary<string, object?>
                {
                    ["exceptionHResult"] = exception.HResult,
                    ["isTerminating"] = isTerminating,
                    ["managedThreadId"] = Environment.CurrentManagedThreadId,
                    ["osBuild"] = Environment.OSVersion.Version.Build,
                    ["processId"] = Environment.ProcessId,
                });
            CrashReportResult result = await CrashReportBuilder.BuildMetadataOnlyAsync(
                reportPath,
                metadata,
                cancellationToken).ConfigureAwait(false);
            TrimReports(occurredAtUtc);
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Interlocked.Exchange(ref _installed, 0) != 0)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }
        // A handler that entered before unsubscription may still be completing. Keeping this
        // process-lifetime semaphore undisposed avoids racing its final Release().
    }

    /// <summary>
    /// Writes a report for an exception the XAML framework caught on the UI thread. WinUI routes
    /// those to <c>Application.UnhandledException</c> and then fails the process fast, so the
    /// AppDomain handler never runs: without this entry point every crash originating in an
    /// <c>async void</c> event handler would be lost. It does not suppress the crash.
    /// </summary>
    public void ReportFatal(Exception exception, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ReportSafely(exception, errorCode, isTerminating: true);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
            ReportSafely(exception, "crash.process.unhandled", args.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        ReportSafely(args.Exception, "crash.task.unobserved", isTerminating: false);

    private void ReportSafely(Exception exception, string errorCode, bool isTerminating)
    {
        try
        {
            ReportAsync(exception, errorCode, isTerminating).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception reportFailure)
        {
            // A crashing process cannot recover through the UI, but reporting failure must remain
            // observable to an attached debugger and must never replace the original exception.
            Debug.WriteLine(
                $"Infini-Transeon local crash metadata write failed: {reportFailure.GetType().Name}");
        }
    }

    private static IReadOnlyList<string> GetStackAddresses(Exception exception)
    {
        try
        {
            return new StackTrace(exception, fNeedFileInfo: false)
                .GetFrames()
                .Select(frame => frame.GetMethod())
                .Where(method => method is not null)
                .Select(method =>
                    $"{method!.Module.Name}:{method.MetadataToken:X8}")
                .Where(IsStableIdentifier)
                .Take(4096)
                .ToArray();
        }
        catch (Exception failure) when (
            failure is not OutOfMemoryException and not StackOverflowException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CrashModule> GetModules() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName())
            .Select(name => new CrashModule(
                name.Name ?? "unknown",
                name.Version?.ToString() ?? "0.0.0.0"))
            .OrderBy(module => module.Name, StringComparer.Ordinal)
            .Take(1024)
            .ToArray();

    private static bool IsStableIdentifier(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':' or '/');

    private void TrimReports(DateTimeOffset nowUtc)
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            FileInfo[] reports = new DirectoryInfo(_directory)
                .EnumerateFiles("crash-*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            DateTimeOffset cutoff = nowUtc - _retention;
            for (int index = 0; index < reports.Length; index++)
            {
                if (index >= _maximumReports || reports[index].LastWriteTimeUtc < cutoff.UtcDateTime)
                    reports[index].Delete();
            }
        }
        catch (Exception failure) when (
            failure is not OutOfMemoryException and not StackOverflowException)
        {
            Debug.WriteLine(
                $"Infini-Transeon crash report retention cleanup failed: {failure.GetType().Name}");
        }
    }
}
