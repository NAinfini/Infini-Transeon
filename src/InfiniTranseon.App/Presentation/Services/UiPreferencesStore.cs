using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfiniTranseon.App.Presentation.Services;

// App-only presentation preferences that have no field in the Core ApplicationSettings schema. Stored
// as a small JSON file beside the database so they persist across restarts. Read/write failures other
// than "file absent" propagate (Debug-First): a corrupt preferences file must be visible, not masked.
public sealed record UiPreferences(
    UiThemePreference Theme = UiThemePreference.System,
    bool StrictOffline = false,
    HistoryRetention HistoryRetention = HistoryRetention.Days30);

public sealed class UiPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();
    private readonly string _path;

    public UiPreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public UiPreferences Load()
    {
        if (!File.Exists(_path))
        {
            return new UiPreferences();
        }

        string json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<UiPreferences>(json, SerializerOptions) ??
            throw new InvalidDataException("UI preferences file is empty or malformed.");
    }

    public void Save(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(preferences, SerializerOptions));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
