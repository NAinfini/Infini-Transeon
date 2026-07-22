using System.Text.Json;

namespace InfiniTranseon.Core.Diagnostics;

public enum StatusEventSeverity
{
    Trace,
    Information,
    Warning,
    Error,
    Critical,
}

public readonly record struct StatusIdentifier
{
    public StatusIdentifier(string value)
    {
        if (!LogRedactor.IsStableIdentifier(value))
            throw new ArgumentException("Status identifier is invalid.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record StatusEvent(
    DateTimeOffset OccurredAtUtc,
    string Category,
    string ErrorCode,
    string MessageKey,
    StatusEventSeverity Severity,
    IReadOnlyDictionary<string, object?> Arguments);

public sealed class StatusEventLog : IAsyncDisposable
{
    private readonly string _directory;
    private readonly long _maximumFileBytes;
    private readonly int _maximumFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public StatusEventLog(string directory, long maximumFileBytes = 4 * 1024 * 1024, int maximumFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        _directory = Path.GetFullPath(directory);
        _maximumFileBytes = maximumFileBytes;
        _maximumFiles = maximumFiles;
    }

    public async ValueTask WriteAsync(StatusEvent statusEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Validate(statusEvent);
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(statusEvent with
        {
            Arguments = LogRedactor.RedactArguments(statusEvent.Arguments),
        });
        if (line.Length > 64 * 1024 || line.Length + 1 > _maximumFileBytes)
            throw new ArgumentOutOfRangeException(
                nameof(statusEvent), "Serialized status event exceeds the configured log file budget.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            string current = Path.Combine(_directory, "status-0.jsonl");
            if (File.Exists(current) && new FileInfo(current).Length + line.Length + 1 > _maximumFileBytes)
                Rotate();
            await using var stream = new FileStream(
                current,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private void Rotate()
    {
        File.Delete(Path.Combine(_directory, $"status-{_maximumFiles - 1}.jsonl"));
        for (int index = _maximumFiles - 2; index >= 0; index--)
        {
            string source = Path.Combine(_directory, $"status-{index}.jsonl");
            if (File.Exists(source)) File.Move(source, Path.Combine(_directory, $"status-{index + 1}.jsonl"));
        }
    }

    private static void Validate(StatusEvent value)
    {
        if (value.OccurredAtUtc.Offset != TimeSpan.Zero || !LogRedactor.IsStableIdentifier(value.Category) ||
            !LogRedactor.IsStableIdentifier(value.ErrorCode) || !LogRedactor.IsStableIdentifier(value.MessageKey) ||
            !Enum.IsDefined(value.Severity) || value.Arguments.Count > 64)
            throw new ArgumentException("Status event metadata is invalid.", nameof(value));
        foreach ((string key, object? argument) in value.Arguments)
        {
            if (!LogRedactor.IsStableIdentifier(key) || LogRedactor.IsSensitiveName(key) || !IsSafeArgument(argument))
                throw new ArgumentException(
                    "Status events accept only bounded typed values and stable identifiers; free-form text is forbidden.",
                    nameof(value));
        }
    }

    private static bool IsSafeArgument(object? value) => value switch
    {
        null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => true,
        float number => float.IsFinite(number),
        double number => double.IsFinite(number),
        Guid or TimeSpan => true,
        DateTimeOffset timestamp => timestamp.Offset == TimeSpan.Zero,
        StatusIdentifier identifier => LogRedactor.IsStableIdentifier(identifier.Value),
        string => false,
        Enum => true,
        _ => false,
    };

}
