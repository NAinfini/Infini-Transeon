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

/// <summary>
/// One value a status event may carry.
///
/// The log holds bounded typed values and stable identifiers only — prose is how recognized text,
/// translations and user paths would leak into a file the user is asked to share. Expressing that
/// rule as a type puts the violation on the offending line at compile time. Checking it when the
/// line is written cannot: the writer drains its queue on another thread long after the caller
/// returned, so one rejected argument ends all logging and then surfaces at an unrelated caller.
///
/// There is deliberately no conversion from <see cref="string"/>. A caller holding a machine token
/// says so with <see cref="Id"/>, which validates it; a caller holding prose has nothing to pass.
/// </summary>
public readonly record struct StatusArgument
{
    private StatusArgument(object? value) => Value = value;

    public object? Value { get; }

    /// <summary>The absent value, written to the log as null.</summary>
    public static StatusArgument None => default;

    /// <summary>Carries a stable machine token: an error code, a provider id, an operation name.</summary>
    public static StatusArgument Id(string? value) =>
        value is null ? None : new StatusIdentifier(value);

    public static implicit operator StatusArgument(StatusIdentifier value) => new(value);

    public static implicit operator StatusArgument(bool value) => new(value);

    public static implicit operator StatusArgument(bool? value) => value is null ? None : new(value.Value);

    public static implicit operator StatusArgument(int value) => new(value);

    public static implicit operator StatusArgument(int? value) => value is null ? None : new(value.Value);

    public static implicit operator StatusArgument(long value) => new(value);

    public static implicit operator StatusArgument(long? value) => value is null ? None : new(value.Value);

    // Non-finite doubles serialize as JSON the reader cannot parse, so they are rejected where the
    // caller can still see which measurement produced them.
    public static implicit operator StatusArgument(double value) => double.IsFinite(value)
        ? new(value)
        : throw new ArgumentOutOfRangeException(nameof(value), "Status arguments require a finite number.");

    public static implicit operator StatusArgument(double? value) => value is null ? None : (double)value;

    public static implicit operator StatusArgument(Guid value) => new(value);

    public static implicit operator StatusArgument(Guid? value) => value is null ? None : new(value.Value);

    public static implicit operator StatusArgument(TimeSpan value) => new(value);

    // A local timestamp reveals the user's time zone and reads as UTC once serialized.
    public static implicit operator StatusArgument(DateTimeOffset value) => value.Offset == TimeSpan.Zero
        ? new(value)
        : throw new ArgumentException("Status arguments require a UTC timestamp.", nameof(value));

    public static implicit operator StatusArgument(Enum value) => new(value);
}

public sealed record StatusEvent(
    DateTimeOffset OccurredAtUtc,
    string Category,
    string ErrorCode,
    string MessageKey,
    StatusEventSeverity Severity,
    IReadOnlyDictionary<string, StatusArgument> Arguments);

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
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(new StatusEventLine(
            statusEvent.OccurredAtUtc,
            statusEvent.Category,
            statusEvent.ErrorCode,
            statusEvent.MessageKey,
            statusEvent.Severity,
            LogRedactor.RedactArguments(statusEvent.Arguments.ToDictionary(
                argument => argument.Key,
                argument => argument.Value.Value,
                StringComparer.Ordinal))));
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

    // Argument values need no check: StatusArgument admits nothing the log may not hold. What remains
    // is the part the type system cannot express — the names, which are plain strings.
    private static void Validate(StatusEvent value)
    {
        if (value.OccurredAtUtc.Offset != TimeSpan.Zero || !LogRedactor.IsStableIdentifier(value.Category) ||
            !LogRedactor.IsStableIdentifier(value.ErrorCode) || !LogRedactor.IsStableIdentifier(value.MessageKey) ||
            !Enum.IsDefined(value.Severity) || value.Arguments.Count > 64)
            throw new ArgumentException("Status event metadata is invalid.", nameof(value));
        foreach (string key in value.Arguments.Keys)
        {
            if (!LogRedactor.IsStableIdentifier(key) || LogRedactor.IsSensitiveName(key))
                throw new ArgumentException(
                    $"Status argument name '{key}' must be a stable identifier and must not name a secret.",
                    nameof(value));
        }
    }

    // The serialized shape. Redaction is shared with the crash reporter, which collects arbitrary
    // host metadata and so has no compile-time argument contract to lean on; it stays object?-based.
    private sealed record StatusEventLine(
        DateTimeOffset OccurredAtUtc,
        string Category,
        string ErrorCode,
        string MessageKey,
        StatusEventSeverity Severity,
        IReadOnlyDictionary<string, object?> Arguments);
}
