using System.Text;
using System.Text.Json;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Ocr;

internal static class CloudOcrResponseParser
{
    public static void EnsureCapacity(int count, string provider)
    {
        if (count >= RuntimeCapabilities.VersionOne.MaxOcrBoxesPerResult)
            throw new OcrRoutingException("ocr.provider.tooManyLines", provider + " returned too many lines.");
    }

    public static string RequiredText(JsonElement element, string property, string provider)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new OcrRoutingException("ocr.malformedResponse", provider + " line text is missing.");
        string text = value.GetString()!;
        if (text.Length > RuntimeCapabilities.VersionOne.MaxSourceChars)
            throw new OcrRoutingException("ocr.provider.outputLimit", provider + " line is too long.");
        return text;
    }

    public static NormalizedRect Polygon(JsonElement vertices, int width, int height)
    {
        if (vertices.ValueKind != JsonValueKind.Array)
            throw new OcrRoutingException("ocr.malformedResponse", "OCR polygon is malformed.");
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        int count = 0;
        foreach (JsonElement vertex in vertices.EnumerateArray())
        {
            double x = vertex.TryGetProperty("x", out JsonElement xElement) && xElement.TryGetDouble(out double xv)
                ? xv : 0;
            double y = vertex.TryGetProperty("y", out JsonElement yElement) && yElement.TryGetDouble(out double yv)
                ? yv : 0;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new OcrRoutingException("ocr.malformedResponse", "OCR polygon is non-finite.");
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            count++;
        }
        if (count < 2 || maxX <= minX || maxY <= minY)
            throw new OcrRoutingException("ocr.malformedResponse", "OCR polygon is empty.");
        double left = Math.Clamp(minX / width, 0, 1);
        double top = Math.Clamp(minY / height, 0, 1);
        double right = Math.Clamp(maxX / width, left, 1);
        double bottom = Math.Clamp(maxY / height, top, 1);
        if (right <= left || bottom <= top)
            throw new OcrRoutingException("ocr.malformedResponse", "OCR polygon lies outside the crop.");
        return new NormalizedRect(left, top, right - left, bottom - top);
    }

    public static string GoogleParagraphText(JsonElement paragraph)
    {
        if (!paragraph.TryGetProperty("words", out JsonElement words) || words.ValueKind != JsonValueKind.Array)
            throw new OcrRoutingException("ocr.malformedResponse", "Google Vision paragraph words are missing.");
        var result = new StringBuilder();
        foreach (JsonElement word in words.EnumerateArray())
        {
            if (!word.TryGetProperty("symbols", out JsonElement symbols) || symbols.ValueKind != JsonValueKind.Array)
                throw new OcrRoutingException("ocr.malformedResponse", "Google Vision symbols are missing.");
            foreach (JsonElement symbol in symbols.EnumerateArray())
            {
                result.Append(RequiredText(symbol, "text", "Google Vision"));
                if (symbol.TryGetProperty("property", out JsonElement property) &&
                    property.TryGetProperty("detectedBreak", out JsonElement detectedBreak) &&
                    detectedBreak.TryGetProperty("type", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    string breakType = type.GetString()!;
                    if (breakType is "SPACE" or "SURE_SPACE" or "EOL_SURE_SPACE") result.Append(' ');
                    else if (breakType is "LINE_BREAK") result.Append('\n');
                    else if (breakType is "HYPHEN") result.Append('-');
                }
            }
        }
        string text = result.ToString().TrimEnd();
        if (text.Length > RuntimeCapabilities.VersionOne.MaxSourceChars)
            throw new OcrRoutingException("ocr.provider.outputLimit", "Google Vision paragraph is too long.");
        return text;
    }
}
