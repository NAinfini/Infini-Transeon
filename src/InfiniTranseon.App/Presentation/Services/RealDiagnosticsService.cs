using System.Text.Json;
using InfiniTranseon.App.Controls;

namespace InfiniTranseon.App.Presentation.Services;

/// <summary>
/// Real diagnostics service. Reads the JSONL status events the Core runtime status reporter writes
/// under the app log directory and maps them to presentation diagnostic events, newest first. Until
/// the runtime is wired (WP6) and produces events, the log is absent or empty and the page shows an
/// intentional empty state rather than fabricated rows.
/// </summary>
public sealed class RealDiagnosticsService : IDiagnosticsService
{
    private const int MaxEvents = 200;
    private readonly string _logDirectory;

    public RealDiagnosticsService(AppDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logDirectory = options.LogDirectory;
    }

    // Matches the fields the Core status log serializes (default PascalCase JSON, numeric severity).
    private sealed record StatusEventDto(
        DateTimeOffset OccurredAtUtc,
        string? Category,
        string? ErrorCode,
        string? MessageKey,
        int Severity);

    public Task<IReadOnlyList<DiagnosticEvent>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_logDirectory))
        {
            return Task.FromResult<IReadOnlyList<DiagnosticEvent>>([]);
        }

        var events = new List<(DateTimeOffset When, DiagnosticEvent Event)>();
        foreach (string file in Directory.EnumerateFiles(_logDirectory, "status-*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                StatusEventDto? dto = TryParse(line);
                if (dto is not null)
                {
                    events.Add((dto.OccurredAtUtc, ToDiagnostic(dto)));
                }
            }
        }

        IReadOnlyList<DiagnosticEvent> ordered = events
            .OrderByDescending(item => item.When)
            .Take(MaxEvents)
            .Select(item => item.Event)
            .ToArray();
        return Task.FromResult(ordered);
    }

    private static StatusEventDto? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<StatusEventDto>(line);
        }
        catch (JsonException)
        {
            // Append-tolerant JSONL: skip a torn or partial trailing line rather than fail the page.
            return null;
        }
    }

    private static DiagnosticEvent ToDiagnostic(StatusEventDto dto) => new(
        dto.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss"),
        dto.Category ?? "runtime",
        dto.ErrorCode ?? "unknown",
        dto.MessageKey ?? string.Empty,
        "—",
        ToSeverity(dto.Severity));

    private static StatusSeverity ToSeverity(int severity) => severity switch
    {
        1 => StatusSeverity.Info,
        2 => StatusSeverity.Warning,
        3 => StatusSeverity.Critical,
        4 => StatusSeverity.Critical,
        _ => StatusSeverity.Neutral,
    };
}
