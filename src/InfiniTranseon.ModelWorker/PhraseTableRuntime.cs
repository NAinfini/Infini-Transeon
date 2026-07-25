using System.Text;
using System.Text.Json;

namespace InfiniTranseon.ModelWorker;

public sealed record PhraseTableTranslationResult(
    bool Success,
    string? Text,
    string? ErrorCode);

public sealed class PhraseTableRuntime : ILocalModelRuntime
{
    private const long MaximumModelBytes = 64L * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private const int MaximumCachedModels = 8;
    private readonly string _root;
    private readonly Dictionary<string, PhraseTableModel> _cache =
        new(StringComparer.Ordinal);

    public PhraseTableRuntime(string managedModelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedModelDirectory);
        _root = Path.GetFullPath(managedModelDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public PhraseTableTranslationResult Translate(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int maximumOutputCharacters)
    {
        if (!IsIdentifier(modelId) || string.IsNullOrWhiteSpace(sourceLanguage) ||
            string.IsNullOrWhiteSpace(targetLanguage) || string.IsNullOrWhiteSpace(text) ||
            maximumOutputCharacters < 1)
            return new(false, null, "local.invalidRequest");
        PhraseTableModel model;
        try
        {
            model = Load(modelId);
        }
        catch (FileNotFoundException)
        {
            return new(false, null, "local.modelMissing");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException)
        {
            return new(false, null, "local.modelInvalid");
        }
        if (!(sourceLanguage == "auto" ||
                string.Equals(sourceLanguage, model.SourceLanguage, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(targetLanguage, model.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "local.languageUnsupported");

        string translated;
        if (model.Exact.TryGetValue(text, out string? exact))
        {
            translated = exact;
        }
        else
        {
            TranslationScanResult scan = TranslateLongestMatches(
                model, text, maximumOutputCharacters);
            if (scan.OutputLimitExceeded)
                return new(false, null, "local.outputLimit");
            translated = scan.Text;
        }
        if (string.Equals(translated, text, StringComparison.Ordinal))
            return new(false, null, "local.noTranslation");
        if (translated.Length > maximumOutputCharacters)
            return new(false, null, "local.outputLimit");
        return new(true, translated, null);
    }

    private static TranslationScanResult TranslateLongestMatches(
        PhraseTableModel model,
        string text,
        int maximumOutputCharacters)
    {
        var output = new StringBuilder(Math.Min(text.Length, maximumOutputCharacters));
        bool changed = false;
        int offset = 0;
        while (offset < text.Length)
        {
            PhraseTrieNode node = model.Root;
            string? longestTarget = null;
            int longestLength = 0;
            int limit = Math.Min(text.Length, checked(offset + model.MaximumSourceLength));
            for (int cursor = offset; cursor < limit; cursor++)
            {
                if (!node.Children.TryGetValue(text[cursor], out node!)) break;
                if (node.Target is not null)
                {
                    longestTarget = node.Target;
                    longestLength = cursor - offset + 1;
                }
            }

            if (longestTarget is null)
            {
                output.Append(text[offset++]);
                continue;
            }

            output.Append(longestTarget);
            offset += longestLength;
            changed = true;
            if (output.Length > maximumOutputCharacters)
                return new(string.Empty, true);
        }

        return new(changed ? output.ToString() : text, false);
    }

    private PhraseTableModel Load(string modelId)
    {
        if (_cache.TryGetValue(modelId, out PhraseTableModel? cached)) return cached;
        string path = Path.GetFullPath(Path.Combine(
            _root, "phrase-tables", modelId + ".json"));
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Phrase table path escapes the managed model root.");
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Phrase table was not installed.", path);
        if (file.Length is < 2 or > MaximumModelBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Phrase table file is invalid.");
        DirectoryInfo? directory = file.Directory;
        while (directory is not null &&
            directory.FullName.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Phrase table path traverses a reparse point.");
            directory = directory.Parent;
        }
        using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        PhraseTableDocument document = JsonSerializer.Deserialize<PhraseTableDocument>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
            throw new InvalidDataException("Phrase table is empty.");
        if (document.SchemaVersion != 1 || document.ModelId != modelId ||
            string.IsNullOrWhiteSpace(document.SourceLanguage) ||
            string.IsNullOrWhiteSpace(document.TargetLanguage) ||
            document.Entries is null || document.Entries.Count is < 1 or > MaximumEntries)
            throw new InvalidDataException("Phrase table header is invalid.");
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = new PhraseTrieNode();
        int maximumSourceLength = 0;
        foreach (PhraseTableEntry entry in document.Entries)
        {
            if (string.IsNullOrEmpty(entry.Source) || string.IsNullOrEmpty(entry.Target) ||
                entry.Source.Length > 1_024 || entry.Target.Length > 4_096 ||
                entry.Source.Contains('\0') || entry.Target.Contains('\0') ||
                !exact.TryAdd(entry.Source, entry.Target))
                throw new InvalidDataException("Phrase table entry is invalid or duplicated.");
            maximumSourceLength = Math.Max(maximumSourceLength, entry.Source.Length);
            PhraseTrieNode node = root;
            foreach (char character in entry.Source)
            {
                if (!node.Children.TryGetValue(character, out PhraseTrieNode? child))
                {
                    child = new PhraseTrieNode();
                    node.Children.Add(character, child);
                }
                node = child;
            }
            node.Target = entry.Target;
        }
        var model = new PhraseTableModel(
            document.SourceLanguage,
            document.TargetLanguage,
            exact,
            root,
            maximumSourceLength);
        if (_cache.Count >= MaximumCachedModels) _cache.Remove(_cache.Keys.First());
        _cache.Add(modelId, model);
        return model;
    }

    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '.' or '_' or '-');

    public void Dispose()
    {
    }

    private sealed record PhraseTableModel(
        string SourceLanguage,
        string TargetLanguage,
        IReadOnlyDictionary<string, string> Exact,
        PhraseTrieNode Root,
        int MaximumSourceLength);

    private sealed class PhraseTrieNode
    {
        public Dictionary<char, PhraseTrieNode> Children { get; } = [];
        public string? Target { get; set; }
    }

    private readonly record struct TranslationScanResult(
        string Text,
        bool OutputLimitExceeded);

    private sealed record PhraseTableDocument(
        int SchemaVersion,
        string ModelId,
        string SourceLanguage,
        string TargetLanguage,
        IReadOnlyList<PhraseTableEntry>? Entries);

    private sealed record PhraseTableEntry(string Source, string Target);
}
