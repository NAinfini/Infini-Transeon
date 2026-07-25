using System.Text.Json;

namespace InfiniTranseon.ModelWorker;

public interface ILocalModelRuntime : IDisposable
{
    PhraseTableTranslationResult Translate(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int maximumOutputCharacters);
}

public static class LocalModelRuntimeFactory
{
    private const string MarkerFileName = "installation.json";

    public static ILocalModelRuntime Create(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        string root = Path.GetFullPath(packageDirectory);
        var markerFile = new FileInfo(Path.Combine(root, MarkerFileName));
        if (!markerFile.Exists ||
            markerFile.Length is < 2 or > 256 * 1024 ||
            (markerFile.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The local model installation marker is invalid.");

        InstallationMarker marker;
        using (FileStream stream = new(
            markerFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan))
        {
            marker = JsonSerializer.Deserialize<InstallationMarker>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
                throw new InvalidDataException("The local model installation marker is empty.");
        }
        if (marker.SchemaVersion != 2 ||
            !IsIdentifier(marker.ModelId) ||
            string.IsNullOrWhiteSpace(marker.Runtime))
            throw new InvalidDataException("The local model installation marker is unsupported.");

        return marker.Runtime switch
        {
            "phrase-table-v1" => new PhraseTableRuntime(root),
            "ctranslate2-madlad-v1" => new CTranslate2MadladRuntime(root, marker.ModelId),
            _ => throw new InvalidDataException(
                $"The local model runtime '{marker.Runtime}' is not supported."),
        };
    }

    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');

    private sealed record InstallationMarker(
        int SchemaVersion,
        string ModelId,
        string Runtime);
}
