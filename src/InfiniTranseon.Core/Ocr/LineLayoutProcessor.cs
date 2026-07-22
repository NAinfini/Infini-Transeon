using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Ocr;

public sealed record LineBreakPolicy
{
    public LineBreakPolicy(
        LineBreakMode mode,
        int maximumLines,
        string? customSeparator = null,
        LineAlignment alignment = LineAlignment.Auto)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLines, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumLines,
            RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult);
        if (mode == LineBreakMode.CustomSeparator && customSeparator is null)
            throw new ArgumentNullException(nameof(customSeparator));
        Mode = mode;
        MaximumLines = maximumLines;
        CustomSeparator = customSeparator;
        Alignment = alignment;
    }

    public LineBreakMode Mode { get; }
    public int MaximumLines { get; }
    public string? CustomSeparator { get; }
    public LineAlignment Alignment { get; }

    public static LineBreakPolicy FromProfile(ProfileRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return new LineBreakPolicy(
            region.LineBreakMode,
            region.MaximumLines,
            region.CustomLineSeparator,
            region.LineAlignment);
    }
}

public sealed record ProcessedOcrLine(
    string OriginalText,
    string NormalizedText,
    NormalizedRect Bounds,
    double Confidence);

public sealed record ProcessedOcrText(
    SensitiveSourceText OriginalText,
    string NormalizedText,
    IReadOnlyList<ProcessedOcrLine> Lines,
    IReadOnlyList<string> TranslationSegments,
    string? Speaker,
    TextDiagnosticSummary Diagnostic);

public sealed class LineLayoutProcessor
{
    public ProcessedOcrText Process(IReadOnlyList<TextLine> sourceLines, LineBreakPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        ArgumentNullException.ThrowIfNull(policy);
        string exactSource = string.Join('\n', sourceLines.Select(line => line.Text));
        var sensitive = new SensitiveSourceText(exactSource);
        List<TextLine> ordered = OrderLines(sourceLines).Take(policy.MaximumLines).ToList();
        var processed = new List<ProcessedOcrLine>(ordered.Count);
        foreach (TextLine line in ordered)
        {
            string normalized = TextNormalizer.Normalize(line.Text);
            if (!TextNormalizer.ContainsMeaningfulText(normalized)) continue;
            if (processed.Any(existing =>
                string.Equals(existing.NormalizedText, normalized, StringComparison.Ordinal) &&
                IntersectionOverUnion(existing.Bounds, line.Bounds) >= 0.5))
            {
                continue;
            }
            processed.Add(new ProcessedOcrLine(line.Text, normalized, line.Bounds, line.Confidence));
        }

        string? speaker = null;
        if (policy.Mode != LineBreakMode.KeyValueRows && processed.Count > 0 &&
            TrySeparateSpeaker(processed[0].NormalizedText, out string name, out string dialogue))
        {
            speaker = name;
            processed[0] = processed[0] with { NormalizedText = dialogue };
        }

        IReadOnlyList<string> segments = policy.Mode is LineBreakMode.PerBox or LineBreakMode.KeyValueRows
            ? processed.Select(line => line.NormalizedText).Where(text => text.Length > 0).ToArray()
            : processed.Count == 0 ? [] : [Join(processed, policy)];
        string normalizedText = policy.Mode switch
        {
            LineBreakMode.JoinParagraph => JoinParagraph(processed),
            LineBreakMode.CustomSeparator => string.Join(policy.CustomSeparator!, processed.Select(line => line.NormalizedText)),
            _ => string.Join('\n', processed.Select(line => line.NormalizedText)),
        };
        if (policy.Mode is LineBreakMode.PerBox or LineBreakMode.KeyValueRows)
            normalizedText = string.Join('\n', segments);

        return new ProcessedOcrText(
            sensitive,
            normalizedText,
            processed.AsReadOnly(),
            segments,
            speaker,
            new TextDiagnosticSummary(sensitive.Length, sensitive.Sha256));
    }

    private static IReadOnlyList<TextLine> OrderLines(IReadOnlyList<TextLine> lines)
    {
        var remaining = lines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ToList();
        var ordered = new List<TextLine>(remaining.Count);
        while (remaining.Count > 0)
        {
            TextLine first = remaining[0];
            double tolerance = first.Bounds.Height * 0.5;
            List<TextLine> row = remaining.Where(line => Math.Abs(line.Bounds.Y - first.Bounds.Y) <= tolerance)
                .OrderBy(line => line.Bounds.X)
                .ToList();
            ordered.AddRange(row);
            foreach (TextLine item in row) remaining.Remove(item);
        }
        return ordered;
    }

    private static string Join(IReadOnlyList<ProcessedOcrLine> lines, LineBreakPolicy policy) =>
        policy.Mode switch
        {
            LineBreakMode.JoinParagraph => JoinParagraph(lines),
            LineBreakMode.CustomSeparator => string.Join(policy.CustomSeparator!, lines.Select(line => line.NormalizedText)),
            _ => string.Join('\n', lines.Select(line => line.NormalizedText)),
        };

    private static string JoinParagraph(IReadOnlyList<ProcessedOcrLine> lines)
    {
        if (lines.Count == 0) return string.Empty;
        var result = new System.Text.StringBuilder(lines[0].NormalizedText);
        for (int index = 1; index < lines.Count; index++)
        {
            ProcessedOcrLine previous = lines[index - 1];
            ProcessedOcrLine current = lines[index];
            double gap = current.Bounds.Y - (previous.Bounds.Y + previous.Bounds.Height);
            if (gap > Math.Max(previous.Bounds.Height, current.Bounds.Height) * 1.8)
                result.Append("\n\n");
            else if (NeedsSpace(previous.NormalizedText, current.NormalizedText))
                result.Append(' ');
            result.Append(current.NormalizedText);
        }
        return result.ToString();
    }

    private static bool NeedsSpace(string left, string right) =>
        left.Length > 0 && right.Length > 0 &&
        left[^1] <= 0x7f && right[0] <= 0x7f &&
        char.IsLetterOrDigit(left[^1]) && char.IsLetterOrDigit(right[0]);

    private static bool TrySeparateSpeaker(string text, out string speaker, out string dialogue)
    {
        int colon = text.IndexOfAny([':', '：']);
        if (colon is <= 0 or > 32)
        {
            speaker = string.Empty;
            dialogue = text;
            return false;
        }
        string candidate = text[..colon].Trim();
        if (candidate.Any(char.IsDigit) || !candidate.Any(char.IsLetter))
        {
            speaker = string.Empty;
            dialogue = text;
            return false;
        }
        speaker = candidate;
        dialogue = text[(colon + 1)..].TrimStart();
        return dialogue.Any(char.IsLetter);
    }

    private static double IntersectionOverUnion(NormalizedRect left, NormalizedRect right)
    {
        double x = Math.Max(left.X, right.X);
        double y = Math.Max(left.Y, right.Y);
        double edgeX = Math.Min(left.X + left.Width, right.X + right.Width);
        double edgeY = Math.Min(left.Y + left.Height, right.Y + right.Height);
        double intersection = Math.Max(0, edgeX - x) * Math.Max(0, edgeY - y);
        double union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }
}
