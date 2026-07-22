using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfiniTranseon.Core.Profiles;

public sealed class ProfileArchiveService
{
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumProfileBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> ExcludedPropertyKeys = new(StringComparer.Ordinal)
    {
        "apikey", "apikeys", "secret", "secrets", "credentialvalue",
        "token", "accesstoken", "authorization",
        "historyrecords", "screenshots", "models", "modelpath", "personalpath",
        "nativehandle", "logs", "machinebinding", "targetbinding", "windowhandle",
        "executablepath", "workingdirectory", "installationdirectory",
    };

    public void Export(ProfileDocument document, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Archive destination must be writable.", nameof(destination));
        JsonNode root = JsonNode.Parse(ProfileJson.Serialize(document)) ??
            throw new JsonException("Profile serialization produced no JSON root.");
        RemoveExcludedData(root);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(new
        {
            archiveVersion = 1,
            profileSchemaVersion = ProfileDocument.CurrentVersion,
        }, ProfileJson.Options));
        WriteEntry(archive, "profile.json", root.ToJsonString(ProfileJson.Options));
    }

    public ProfileDocument Import(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Archive source must be readable.", nameof(source));
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        string[] expectedEntries = ["manifest.json", "profile.json"];
        string[] actualEntries = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (actualEntries.Length != expectedEntries.Length ||
            actualEntries.Distinct(StringComparer.Ordinal).Count() != actualEntries.Length ||
            !actualEntries.Order(StringComparer.Ordinal).SequenceEqual(
                expectedEntries.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Profile archives must contain exactly one manifest.json and one profile.json entry.");
        }
        ZipArchiveEntry manifestEntry = archive.Entries.SingleOrDefault(
            entry => entry.FullName == "manifest.json") ??
            throw new InvalidDataException("Profile archive contains no manifest.json entry.");
        if (manifestEntry.Length > MaximumManifestBytes)
            throw new InvalidDataException("Profile archive manifest exceeds the maximum size.");
        using (Stream manifestStream = manifestEntry.Open())
        using (var manifestDocument = JsonDocument.Parse(
            ReadBounded(manifestStream, MaximumManifestBytes),
            new JsonDocumentOptions { MaxDepth = 16 }))
        {
            int archiveVersion = manifestDocument.RootElement.GetProperty("archiveVersion").GetInt32();
            if (archiveVersion != 1)
            {
                throw new InvalidDataException($"Profile archive version {archiveVersion} is not supported.");
            }
        }
        ZipArchiveEntry profile = archive.Entries.SingleOrDefault(entry => entry.FullName == "profile.json") ??
            throw new InvalidDataException("Profile archive contains no profile.json entry.");
        if (profile.Length > MaximumProfileBytes)
        {
            throw new InvalidDataException("Profile archive exceeds the maximum uncompressed profile size.");
        }
        using Stream stream = profile.Open();
        byte[] profileBytes = ReadBounded(stream, MaximumProfileBytes, (int)profile.Length);
        return new ProfileMigrator().Migrate(Encoding.UTF8.GetString(profileBytes));
    }

    private static byte[] ReadBounded(Stream stream, int maximumBytes, int initialCapacity = 0)
    {
        using var buffer = new MemoryStream(Math.Min(initialCapacity, maximumBytes));
        byte[] chunk = new byte[81920];
        while (true)
        {
            int remaining = maximumBytes + 1 - checked((int)buffer.Length);
            if (remaining <= 0)
            {
                throw new InvalidDataException("Profile archive entry exceeds the maximum uncompressed size.");
            }
            int count = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
            if (count == 0) break;
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length > maximumBytes)
            throw new InvalidDataException("Profile archive entry exceeds the maximum uncompressed size.");
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static void RemoveExcludedData(JsonNode node)
    {
        if (node is JsonObject item)
        {
            foreach ((string name, JsonNode? child) in item.ToList())
            {
                if (IsExcludedProperty(name)) item.Remove(name);
                else if (child is JsonValue value &&
                    value.TryGetValue(out string? text) &&
                    ContainsPersonalPath(text))
                    item[name] = "[REDACTED_PATH]";
                else if (child is not null) RemoveExcludedData(child);
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                JsonNode? child = array[index];
                if (child is JsonValue value &&
                    value.TryGetValue(out string? text) &&
                    ContainsPersonalPath(text))
                {
                    array[index] = "[REDACTED_PATH]";
                    continue;
                }
                if (child is not null) RemoveExcludedData(child);
            }
        }
    }

    private static bool IsExcludedProperty(string name)
    {
        string normalized = string.Concat(name.Where(char.IsAsciiLetterOrDigit))
            .ToLowerInvariant();
        return ExcludedPropertyKeys.Contains(normalized);
    }

    private static bool ContainsPersonalPath(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Contains("file:/", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(@"\\", StringComparison.Ordinal))
            return true;
        for (int index = 0; index + 2 < value.Length; index++)
        {
            if (char.IsAsciiLetter(value[index]) && value[index + 1] == ':' &&
                value[index + 2] is '\\' or '/')
                return true;
        }
        return false;
    }
}
