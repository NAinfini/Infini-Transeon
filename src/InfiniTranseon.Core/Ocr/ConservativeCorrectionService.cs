namespace InfiniTranseon.Core.Ocr;

public sealed class ConservativeCorrectionService
{
    private readonly IReadOnlyDictionary<string, string> _glossary;
    private readonly IReadOnlySet<string> _protectedNames;

    public ConservativeCorrectionService(
        IReadOnlyDictionary<string, string> glossary,
        IReadOnlySet<string> protectedNames)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        ArgumentNullException.ThrowIfNull(protectedNames);
        _glossary = glossary;
        _protectedNames = protectedNames;
    }

    public string Correct(string token, double confidence, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!enabled || token.Length <= 2 || token.All(char.IsDigit) ||
            !double.IsFinite(confidence) || confidence < 0.75 ||
            _protectedNames.Contains(token))
        {
            return token;
        }
        return _glossary.TryGetValue(token, out string? corrected) &&
            !string.IsNullOrWhiteSpace(corrected)
            ? corrected
            : token;
    }
}
