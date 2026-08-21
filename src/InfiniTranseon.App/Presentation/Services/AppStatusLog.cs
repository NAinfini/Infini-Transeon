using System.Threading.Channels;
using InfiniTranseon.Core.Diagnostics;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Bounded single-writer bridge from synchronous app/runtime events to the append-only Core
/// status log. It accepts structured status only: OCR text, translations, screenshots, secrets,
/// free-form paths, and exception messages never enter this channel.
/// </summary>
public sealed class AppStatusLog : IAsyncDisposable
{
    private readonly StatusEventLog _log;
    private readonly Channel<StatusEvent> _events;
    private readonly Task _writer;
    private int _disposed;

    public AppStatusLog(AppDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _log = new StatusEventLog(options.LogDirectory);
        _events = Channel.CreateBounded<StatusEvent>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writer = WriteLoopAsync();
    }

    public void Record(StatusEvent statusEvent)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_writer.IsFaulted)
            _writer.GetAwaiter().GetResult();
        if (!_events.Writer.TryWrite(statusEvent))
            _events.Writer.WriteAsync(statusEvent).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _events.Writer.TryComplete();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        finally
        {
            await _log.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drains the channel on the thread pool. The context matters: this log is constructed while the
    /// dependency graph is resolved on the UI thread, so a captured context would schedule every
    /// resumption of this loop back onto the dispatcher. <see cref="Record"/> blocks its caller once
    /// the channel is full, so a UI-thread caller would then be waiting for a continuation only the
    /// UI thread could run — a permanent freeze with no exception and no log line to show for it.
    /// </summary>
    private async Task WriteLoopAsync()
    {
        await foreach (StatusEvent statusEvent in
            _events.Reader.ReadAllAsync().ConfigureAwait(false))
            await _log.WriteAsync(statusEvent, CancellationToken.None).ConfigureAwait(false);
    }
}
