using System.Text;
using System.Xml.Linq;

namespace InfiniTranseon.App.Tests;

public sealed class LocalizationParityTests
{
    private const string BaseCulture = "en-US";
    private const string TargetCulture = "zh-CN";

    private static IReadOnlyDictionary<string, string> LoadResources(string culture)
    {
        XDocument document = XDocument.Load(AppSourcePaths.ResourcesFile(culture));
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (XElement data in document.Root!.Elements("data"))
        {
            string name = (string)data.Attribute("name")!;
            map[name] = data.Element("value")?.Value ?? string.Empty;
        }

        return map;
    }

    // A misdecoded UTF-8 multibyte sequence surfaces as a Latin-1 lead code point (U+00C0-U+00FF)
    // immediately followed by a continuation-range code point (U+0080-U+00BF), or as the Unicode
    // replacement character (U+FFFD). Legitimate en-US and zh-CN values use only ASCII, CJK, and
    // CJK punctuation, so this signature flags Mojibake without false-positives on real content.
    private static bool LooksLikeMojibake(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == (char)0xFFFD)
            {
                return true;
            }

            if (index + 1 < value.Length)
            {
                int lead = value[index];
                int next = value[index + 1];
                if (lead is >= 0x00C0 and <= 0x00FF && next is >= 0x0080 and <= 0x00BF)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void Key_sets_are_identical_across_cultures()
    {
        HashSet<string> baseKeys = LoadResources(BaseCulture).Keys.ToHashSet(StringComparer.Ordinal);
        HashSet<string> targetKeys = LoadResources(TargetCulture).Keys.ToHashSet(StringComparer.Ordinal);

        IEnumerable<string> missingInTarget = baseKeys.Except(targetKeys);
        IEnumerable<string> missingInBase = targetKeys.Except(baseKeys);

        Assert.True(
            baseKeys.SetEquals(targetKeys),
            $"Missing in {TargetCulture}: [{string.Join(", ", missingInTarget)}]; " +
            $"missing in {BaseCulture}: [{string.Join(", ", missingInBase)}].");
    }

    [Theory]
    [InlineData(BaseCulture)]
    [InlineData(TargetCulture)]
    public void No_resource_value_is_empty(string culture)
    {
        List<string> empties =
        [
            .. LoadResources(culture)
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key),
        ];

        Assert.True(empties.Count == 0, $"Empty value(s) in {culture}: [{string.Join(", ", empties)}].");
    }

    [Fact]
    public void Target_culture_file_is_valid_utf8()
    {
        byte[] bytes = File.ReadAllBytes(AppSourcePaths.ResourcesFile(TargetCulture));
        UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Throws DecoderFallbackException if any byte sequence is not valid UTF-8.
        _ = strict.GetString(bytes);
    }

    [Fact]
    public void Target_culture_values_have_no_mojibake()
    {
        List<string> suspect =
        [
            .. LoadResources(TargetCulture)
                .Where(pair => LooksLikeMojibake(pair.Value))
                .Select(pair => $"{pair.Key}='{pair.Value}'"),
        ];

        Assert.True(suspect.Count == 0, $"Mojibake in {TargetCulture}: [{string.Join("; ", suspect)}].");
    }
}
