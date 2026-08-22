using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using InfiniTranseon.App.Presentation;

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

    // The workspace breadcrumb resolves section labels by resource name at runtime rather than by
    // x:Uid, so a renamed or dropped key would silently render an empty crumb instead of failing.
    [Theory]
    [InlineData(BaseCulture)]
    [InlineData(TargetCulture)]
    public void Breadcrumb_section_labels_exist(string culture)
    {
        string[] required =
        [
            "NavWorkspaceOverview.Content",
            "NavWorkspaceCapture.Content",
            "NavWorkspaceChannels.Content",
            "NavWorkspaceOverlay.Content",
            "NavWorkspaceLanguage.Content",
            "NavWorkspaceHistory.Content",
        ];
        IReadOnlyDictionary<string, string> resources = LoadResources(culture);
        List<string> missing = [.. required.Where(key => !resources.ContainsKey(key))];

        Assert.True(missing.Count == 0, $"Missing in {culture}: [{string.Join(", ", missing)}].");
    }

    [Theory]
    [InlineData(BaseCulture)]
    [InlineData(TargetCulture)]
    public void Every_hotkey_enum_value_has_a_localized_label(string culture)
    {
        IReadOnlyDictionary<string, string> resources = LoadResources(culture);
        IEnumerable<string> required = Enum.GetValues<AppHotkeyAction>()
            .Select(action => $"HotkeyAction_{action}")
            .Concat(Enum.GetValues<AppHotkeyScope>()
                .Select(scope => $"HotkeyScope_{scope}"));
        List<string> missing = [.. required.Where(key => !resources.ContainsKey(key))];

        Assert.True(missing.Count == 0, $"Missing hotkey label(s) in {culture}: [{string.Join(", ", missing)}].");
    }

    // Code-side lookups are resolved by MRT, which addresses a property-style resw name
    // ("SetupStep1Caption.Text") as a path with '/'. The dotted spelling that works in x:Uid markup
    // raises NAMED_RESOURCE_NOT_FOUND at runtime, and it did: the setup wizard threw the moment it
    // loaded. A typo has the same effect, so every literal lookup is checked against the resw here.
    //
    // Services below the WinRT boundary take an injected ResourceTextLookup instead of a loader, and
    // spell it `_text`; those keys are the same resw names and need the same guard.
    [Fact]
    public void Every_literal_resource_lookup_resolves()
    {
        IReadOnlyDictionary<string, string> resources = LoadResources(BaseCulture);
        var failures = new List<string>();

        foreach (string file in AppSourcePaths.AllCSharpFiles())
        {
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, @"(?:GetString|_text)\(""([^""]+)""\)"))
            {
                string key = match.Groups[1].Value;
                if (key.Contains('.', StringComparison.Ordinal))
                {
                    failures.Add($"{Path.GetFileName(file)}: '{key}' uses '.' where MRT expects '/'.");
                }
                else if (!resources.ContainsKey(key.Replace('/', '.')))
                {
                    failures.Add($"{Path.GetFileName(file)}: '{key}' is not declared in {BaseCulture}.");
                }
            }
        }

        Assert.Empty(failures);
    }

    // x:Uid="Foo" makes MRT set properties from entries named "Foo.<Property>". A bare "Foo" entry is
    // invisible to markup: the control keeps its unset property, renders empty, and still reserves its
    // layout space — which is exactly how the workspace section pages grew a blank band above their
    // controls. Nothing throws, so only a structural check can catch it.
    [Fact]
    public void Every_markup_uid_has_a_property_entry()
    {
        HashSet<string> declaredUids = LoadResources(BaseCulture)
            .Keys
            .Where(key => key.IndexOf('.', StringComparison.Ordinal) > 0)
            .Select(key => key[..key.IndexOf('.', StringComparison.Ordinal)])
            .ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();
        foreach (string file in AppSourcePaths.AllXamlFiles())
        {
            string markup = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(markup, @"x:Uid=""([^""]+)"""))
            {
                string uid = match.Groups[1].Value;
                if (!declaredUids.Contains(uid))
                {
                    failures.Add(
                        $"{Path.GetFileName(file)}: x:Uid='{uid}' has no '{uid}.<Property>' entry in " +
                        $"{BaseCulture}.");
                }
            }
        }

        Assert.Empty(failures);
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
