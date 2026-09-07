using System.Xml.Linq;

namespace InfiniTranseon.App.Tests;

public sealed class FrontendUsabilityTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Settings_hotkeys_use_a_stacked_editor_and_content_width_drives_the_workspace_layout()
    {
        XDocument document = LoadXaml("SettingsPage.xaml");
        XElement workspace = NamedElement(document, "SettingsWorkspaceGrid");
        XElement hotkeyRows = NamedElement(document, "HotkeyRows");
        string code = LoadCode("SettingsPage.xaml.cs");

        Assert.Equal("OnSettingsWorkspaceSizeChanged", (string?)workspace.Attribute("SizeChanged"));
        Assert.DoesNotContain(
            hotkeyRows.Descendants(),
            element => element.Name.LocalName == "ColumnDefinition");
        Assert.Contains("e.NewSize.Width < SettingsWorkspaceStackThresholdEpx", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(SettingsContent, stacked ? 1 : 0)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_regions_stack_from_the_region_editors_available_width()
    {
        XDocument document = LoadXaml("SetupWizardPage.xaml");
        XElement step3 = NamedElement(document, "Step3Grid");
        string code = LoadCode("SetupWizardPage.xaml.cs");

        Assert.Equal("OnStep3GridSizeChanged", (string?)step3.Attribute("SizeChanged"));
        Assert.Contains("e.NewSize.Width < Step3StackThresholdEpx", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(Step3CanvasPane, stacked ? 2 : 1)", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(Step3InspectorPane, stacked ? 3 : 1)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_region_bounds_expose_localized_ratio_semantics()
    {
        XDocument document = LoadXaml("CaptureSectionPage.xaml");
        IReadOnlyDictionary<string, string> english = LoadResources("en-US");
        IReadOnlyDictionary<string, string> chinese = LoadResources("zh-CN");

        foreach (string name in new[] { "RegionXBox", "RegionYBox", "RegionWidthBox", "RegionHeightBox" })
        {
            XElement box = NamedElement(document, name);
            string uid = (string)box.Attribute(XamlNs + "Uid")!;

            Assert.Equal("Compact", (string?)box.Attribute("SpinButtonPlacementMode"));
            Assert.Contains("0–1", english[$"{uid}.Header"], StringComparison.Ordinal);
            Assert.Contains("0–1", chinese[$"{uid}.Header"], StringComparison.Ordinal);
            Assert.Contains(
                $"{uid}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                english.Keys);
            Assert.Contains(
                $"{uid}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                chinese.Keys);
        }

        Assert.Contains("ratio", english["WorkbenchRegionBoundsHint.Text"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("比例", chinese["WorkbenchRegionBoundsHint.Text"], StringComparison.Ordinal);
    }

    private static XDocument LoadXaml(string fileName) => XDocument.Load(
        AppSourcePaths.AllXamlFiles().Single(path => Path.GetFileName(path) == fileName));

    private static string LoadCode(string fileName) => File.ReadAllText(
        AppSourcePaths.AllCSharpFiles().Single(path => Path.GetFileName(path) == fileName));

    private static XElement NamedElement(XDocument document, string name) => document
        .Descendants()
        .Single(element => (string?)element.Attribute(XamlNs + "Name") == name);

    private static IReadOnlyDictionary<string, string> LoadResources(string culture) => XDocument
        .Load(AppSourcePaths.ResourcesFile(culture))
        .Root!
        .Elements("data")
        .ToDictionary(
            element => (string)element.Attribute("name")!,
            element => element.Element("value")?.Value ?? string.Empty,
            StringComparer.Ordinal);
}
