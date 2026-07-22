using System.Text.Json;

namespace InfiniTranseon.Core.Diagnostics;

public sealed record CrashModule(string Name, string Version);

public sealed record CrashMetadata(
    DateTimeOffset OccurredAtUtc,
    string ApplicationVersion,
    string RuntimeVersion,
    string ErrorCode,
    IReadOnlyList<string> StackAddresses,
    IReadOnlyList<CrashModule> Modules,
    IReadOnlyDictionary<string, object?> State);

public sealed record CrashReportResult(
    string ReportPath,
    bool ContainsMemoryDump,
    bool UploadAvailable,
    IReadOnlyList<string> RedactionSummary);

public static class CrashReportBuilder
{
    public static async ValueTask<CrashReportResult> BuildMetadataOnlyAsync(
        string reportPath,
        CrashMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.OccurredAtUtc.Offset != TimeSpan.Zero ||
            metadata.ApplicationVersion.Length is < 1 or > 128 ||
            metadata.RuntimeVersion.Length is < 1 or > 128 ||
            !LogRedactor.IsStableIdentifier(metadata.ErrorCode) ||
            metadata.State.Count > 256)
            throw new ArgumentException("Crash metadata fields are invalid.", nameof(metadata));
        string path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        IReadOnlyDictionary<string, object?> redacted = LogRedactor.RedactArguments(metadata.State);
        var document = new
        {
            schemaVersion = 1,
            metadata.OccurredAtUtc,
            applicationVersion = SafeVersion(metadata.ApplicationVersion),
            runtimeVersion = SafeVersion(metadata.RuntimeVersion),
            metadata.ErrorCode,
            stackAddresses = metadata.StackAddresses.Take(4096).Select(address =>
                LogRedactor.IsStableIdentifier(address) ? address : "[REDACTED_STACK_ENTRY]"),
            modules = metadata.Modules.Take(1024).Select(module => new CrashModule(
                SafeModuleName(module.Name),
                LogRedactor.IsStableIdentifier(module.Version)
                    ? module.Version
                    : "[REDACTED_VERSION]")),
            state = redacted,
            containsMemoryDump = false,
            uploadEndpoint = (string?)null,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        string temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        string[] summary = metadata.State
            .Where(item => LogRedactor.IsSensitiveName(item.Key) ||
                !Equals(item.Value, redacted[item.Key]))
            .Select(item => item.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new CrashReportResult(path, false, false, summary);
    }

    private static string SafeModuleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[REDACTED_MODULE]";
        string name = Path.GetFileName(value);
        return LogRedactor.IsStableIdentifier(name) ? name : "[REDACTED_MODULE]";
    }

    private static string SafeVersion(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-' or '_' or '+' or ' ')
            ? value
            : "[REDACTED_VERSION]";
}
