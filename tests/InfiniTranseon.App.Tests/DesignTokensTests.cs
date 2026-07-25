using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace InfiniTranseon.App.Tests;

// DesignTokens.xaml/ControlStyles.xaml are WinUI resource dictionaries; the App project cannot
// host xUnit headless (see the test csproj comment), so these assertions parse the XAML as plain
// XML the same way LocalizationParityTests and NavigationCompletenessTests do.
public sealed class DesignTokensTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument LoadDesignTokens() => XDocument.Load(AppSourcePaths.DesignTokensXaml);

    private static IReadOnlySet<string> TopLevelResourceKeys(XDocument document) =>
        document.Root!
            .Elements()
            .Where(element => element.Name.LocalName != "ThemeDictionaries")
            .Select(element => (string?)element.Attribute(XamlNs + "Key"))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("SurfaceBackground")]
    [InlineData("SurfaceCard")]
    [InlineData("SurfaceCardHover")]
    [InlineData("SurfaceSunken")]
    [InlineData("SurfaceStroke")]
    [InlineData("AccentDefault")]
    [InlineData("AccentText")]
    [InlineData("MotionFast")]
    [InlineData("MotionNormal")]
    [InlineData("FontMono")]
    [InlineData("FontWeightStrong")]
    [InlineData("ContentMaxWidth")]
    [InlineData("FormMaxWidth")]
    [InlineData("PaneWidthWorkspaceNav")]
    [InlineData("PaneWidthInspector")]
    [InlineData("SettingRowPadding")]
    [InlineData("SettingRowMargin")]
    [InlineData("SectionHeaderMargin")]
    [InlineData("HeaderCommandMargin")]
    public void Design_token_is_declared(string key)
    {
        IReadOnlySet<string> keys = TopLevelResourceKeys(LoadDesignTokens());
        Assert.Contains(key, keys);
    }

    private static IReadOnlyDictionary<string, string?> ThemeDictionaryBrushColors(XDocument document, string themeKey)
    {
        XElement themeDictionaries = document.Root!.Element(document.Root!.Name.Namespace + "ResourceDictionary.ThemeDictionaries")!;
        XElement theme = themeDictionaries
            .Elements()
            .Single(element => (string?)element.Attribute(XamlNs + "Key") == themeKey);

        return theme
            .Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                element => (string)element.Attribute(XamlNs + "Key")!,
                element => (string?)element.Attribute("Color"),
                StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("ThumbPlaceholderBrush")]
    [InlineData("ThumbLetterboxBrush")]
    [InlineData("OverlayPreviewGameBrush")]
    [InlineData("OverlayPreviewPanelBrush")]
    [InlineData("OverlayPreviewTextBrush")]
    public void Light_theme_mock_brush_differs_from_dark_theme_value(string brushKey)
    {
        XDocument document = LoadDesignTokens();
        IReadOnlyDictionary<string, string?> light = ThemeDictionaryBrushColors(document, "Light");
        IReadOnlyDictionary<string, string?> dark = ThemeDictionaryBrushColors(document, "Dark");

        Assert.True(light.TryGetValue(brushKey, out string? lightColor), $"{brushKey} missing from Light dictionary.");
        Assert.True(dark.TryGetValue(brushKey, out string? darkColor), $"{brushKey} missing from Dark dictionary.");
        Assert.NotEqual(darkColor, lightColor);
    }

    [Fact]
    public void Control_styles_setting_row_style_has_no_hardcoded_padding_or_margin()
    {
        XDocument document = XDocument.Load(AppSourcePaths.ControlStylesXaml);
        XNamespace presentation = document.Root!.Name.Namespace;

        XElement settingRowStyle = document.Root!
            .Elements(presentation + "Style")
            .Single(style => (string?)style.Attribute(XamlNs + "Key") == "SettingRowStyle");

        foreach (XElement setter in settingRowStyle.Elements(presentation + "Setter"))
        {
            string property = (string)setter.Attribute("Property")!;
            if (property is "Padding" or "Margin")
            {
                string value = (string)setter.Attribute("Value")!;
                Assert.StartsWith("{StaticResource", value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Card_style_uses_application_surface_aliases()
    {
        XDocument document = XDocument.Load(AppSourcePaths.ControlStylesXaml);
        XNamespace presentation = document.Root!.Name.Namespace;

        XElement cardStyle = document.Root!
            .Elements(presentation + "Style")
            .Single(style => (string?)style.Attribute(XamlNs + "Key") == "CardBorderStyle");
        IReadOnlyDictionary<string, string> setters = cardStyle
            .Elements(presentation + "Setter")
            .ToDictionary(
                setter => (string)setter.Attribute("Property")!,
                setter => (string)setter.Attribute("Value")!,
                StringComparer.Ordinal);

        Assert.Equal("{ThemeResource SurfaceCard}", setters["Background"]);
        Assert.Equal("{ThemeResource SurfaceStroke}", setters["BorderBrush"]);
    }

    [Fact]
    public void Page_shell_declares_loading_error_empty_and_content_states()
    {
        XDocument document = XDocument.Load(AppSourcePaths.PageShellXaml);
        IReadOnlySet<string> names = document
            .Descendants()
            .Select(element => (string?)element.Attribute(XamlNs + "Name"))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("LoadingState", names);
        Assert.Contains("ErrorState", names);
        Assert.Contains("EmptyStateHost", names);
        Assert.Contains("ContentState", names);
    }

    // XAML resource references are resolved at load time, not at compile time: assigning the x:Double
    // token SpaceS to a Thickness property builds cleanly and then throws XamlParseException the first
    // time the page is navigated to, taking the whole page out of reach. Two live instances of exactly
    // that shipped (SettingsPage and OverlaySectionPage), so the invariant is asserted here rather than
    // left to a runtime sweep that only covers the pages someone happens to open.
    [Fact]
    public void Resource_references_match_the_declared_token_type()
    {
        IReadOnlyDictionary<string, string> tokenTypes = DeclaredTokenTypes();
        var violations = new List<string>();

        foreach (string file in AppSourcePaths.AllXamlFiles())
        {
            XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach ((string property, string value) in ResourceAssignments(element))
                {
                    if (ReferencedResourceKey(value) is not string key ||
                        !tokenTypes.TryGetValue(key, out string? declaredType) ||
                        ExpectedTokenType(element, property) is not string expectedType ||
                        string.Equals(declaredType, expectedType, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    violations.Add(
                        $"{Path.GetFileName(file)}({((IXmlLineInfo)element).LineNumber}): " +
                        $"{property} expects {expectedType} but '{key}' is declared as {declaredType}.");
                }
            }
        }

        Assert.Empty(violations);
    }

    // Icon="Name" is parsed as a Symbol enum member at load time. An invalid name is not a build
    // error — it throws XamlParseException when the page is first constructed, which is how
    // Icon="FitPage" and Icon="Down" made the capture section and its region list unreachable.
    [Fact]
    public void Symbol_icon_names_are_valid_enum_members()
    {
        IReadOnlySet<string> symbols = Enum.GetNames<Microsoft.UI.Xaml.Controls.Symbol>()
            .ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (string file in AppSourcePaths.AllXamlFiles())
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (Match match in Regex.Matches(lines[index], @"\bIcon=""([A-Za-z0-9]+)"""))
                {
                    string name = match.Groups[1].Value;
                    if (!symbols.Contains(name))
                    {
                        violations.Add($"{Path.GetFileName(file)}({index + 1}): '{name}' is not a Symbol.");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    private static IReadOnlyDictionary<string, string> DeclaredTokenTypes()
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string dictionary in new[] { AppSourcePaths.DesignTokensXaml, AppSourcePaths.ControlStylesXaml })
        {
            foreach (XElement element in XDocument.Load(dictionary).Root!.Elements())
            {
                if ((string?)element.Attribute(XamlNs + "Key") is string key &&
                    element.Name.LocalName is "Double" or "Thickness" or "CornerRadius" or "GridLength")
                {
                    types[key] = element.Name.LocalName;
                }
            }
        }

        return types;
    }

    // Both spellings reach the same dictionary: an inline attribute (Padding="{StaticResource X}")
    // and a style setter (<Setter Property="Padding" Value="{StaticResource X}" />).
    private static IEnumerable<(string Property, string Value)> ResourceAssignments(XElement element)
    {
        if (element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") is string setterProperty &&
            (string?)element.Attribute("Value") is string setterValue)
        {
            yield return (setterProperty, setterValue);
            yield break;
        }

        foreach (XAttribute attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.None)
            {
                yield return (attribute.Name.LocalName, attribute.Value);
            }
        }
    }

    private static string? ReferencedResourceKey(string value)
    {
        foreach (string prefix in new[] { "{StaticResource ", "{ThemeResource " })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith('}'))
            {
                return value[prefix.Length..^1].Trim();
            }
        }

        return null;
    }

    private static string? ExpectedTokenType(XElement element, string property)
    {
        // Grid definitions redefine Width/Height as GridLength (their Min*/Max* stay Double), so the
        // owner has to be consulted before the property name.
        if (element.Name.LocalName == "ColumnDefinition" && property == "Width")
            return "GridLength";
        if (element.Name.LocalName == "RowDefinition" && property == "Height")
            return "GridLength";

        return property switch
        {
            "Padding" or "Margin" or "BorderThickness" => "Thickness",
            "CornerRadius" => "CornerRadius",
            "Width" or "Height" or "MinWidth" or "MaxWidth" or "MinHeight" or "MaxHeight" or
            "FontSize" or "Opacity" or "Spacing" or "ColumnSpacing" or "RowSpacing" or
            "StrokeThickness" or "Minimum" or "Maximum" or "SmallChange" or "StepFrequency" => "Double",
            _ => null,
        };
    }

    [Fact]
    public void App_shell_enforces_default_and_minimum_window_size()
    {
        string source = File.ReadAllText(AppSourcePaths.AppShellCode);

        Assert.Contains("DefaultWindowWidthEpx = 1280", source, StringComparison.Ordinal);
        Assert.Contains("DefaultWindowHeightEpx = 800", source, StringComparison.Ordinal);
        Assert.Contains("MinimumWindowWidthEpx = 960", source, StringComparison.Ordinal);
        Assert.Contains("MinimumWindowHeightEpx = 600", source, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Resize", source, StringComparison.Ordinal);
    }
}
