namespace InfiniTranseon.Core.Translation.Local;

/// <summary>
/// Splits a translation into the lines the local runtime actually decodes, and remembers what each
/// one produced.
///
/// The runtime gives every line its own segment, lets no context cross a line boundary, and returns
/// exactly one output line per input line. Two consequences follow, and both are exact rather than
/// approximate: a line translated before does not have to be translated again, and lines belonging
/// to different requests can be decoded in one call. The second is what matters most on a CPU — one
/// decoding step reads the whole model out of memory whether it produces one line or five, so five
/// lines decoded together cost little more than one decoded alone.
///
/// The ledger is bounded and in-memory. It answers for the text on screen right now, which is what
/// changes line by line; the durable, whole-block cache in front of the providers is what answers
/// for text a player saw in an earlier session.
/// </summary>
public sealed class LocalTranslationLineLedger
{
    private readonly record struct LineKey(
        string ModelId,
        string SourceLanguage,
        string TargetLanguage,
        string Line);

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<LineKey, string> _translations;
    private readonly Queue<LineKey> _order;

    public LocalTranslationLineLedger(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _translations = new Dictionary<LineKey, string>(capacity);
        _order = new Queue<LineKey>(capacity);
    }

    /// <summary>
    /// Splits exactly as the runtime does, so a line remembered here is the same string the runtime
    /// was asked to translate: on line feeds, with one trailing carriage return removed.
    /// </summary>
    public static string[] SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
            if (lines[index].EndsWith('\r'))
                lines[index] = lines[index][..^1];
        return lines;
    }

    /// <summary>
    /// Whether the runtime would decode this line. Lines the runtime considers empty keep their
    /// place in the result so the translated block stays aligned with the region it covers.
    /// </summary>
    public static bool HasContent(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (char character in line)
            if (character > ' ') return true;
        return false;
    }

    public static string JoinLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return string.Join('\n', lines);
    }

    public string? Find(string modelId, string sourceLanguage, string targetLanguage, string line)
    {
        var key = new LineKey(modelId, sourceLanguage, targetLanguage, line);
        lock (_gate)
            return _translations.TryGetValue(key, out string? translation) ? translation : null;
    }

    public void Store(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string line,
        string translation)
    {
        ArgumentException.ThrowIfNullOrEmpty(translation);
        var key = new LineKey(modelId, sourceLanguage, targetLanguage, line);
        lock (_gate)
        {
            if (!_translations.TryAdd(key, translation))
            {
                _translations[key] = translation;
                return;
            }
            _order.Enqueue(key);
            while (_order.Count > _capacity)
                _translations.Remove(_order.Dequeue());
        }
    }
}
