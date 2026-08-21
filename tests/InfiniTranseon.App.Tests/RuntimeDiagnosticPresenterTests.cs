using System.Text.RegularExpressions;
using System.Xml.Linq;
using InfiniTranseon.App.Presentation;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// The activity feed and the home card render an engine diagnostic entirely through
/// <see cref="RuntimeDiagnosticPresenter"/>: the engine supplies a machine error code and nothing a
/// user can read. A code without a sentence therefore shows the "unrecognized" wording, which reads
/// like a runtime fault instead of the missing translation it is.
/// </summary>
public sealed class RuntimeDiagnosticPresenterTests
{
    /// <summary>
    /// The pipeline and degradation codes as their emit sites spell them. Both families reach the UI
    /// wholesale — every pipeline failure and every degradation change is published as a diagnostic —
    /// so the source is the inventory and nothing has to be listed twice.
    /// </summary>
    private static IReadOnlyList<string> EmittedCodes() =>
        [.. AppSourcePaths.AllSourceTreeCSharpFiles()
            .SelectMany(file => Regex.Matches(
                File.ReadAllText(file), @"""((?:pipeline|performance)\.[A-Za-z0-9.]+)"""))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The engine's own diagnostics, which cannot be harvested: <c>engine.runtime.*</c> codes name
    /// lifecycle states too, and most of those are set as status rather than raised as diagnostics.
    /// Only the two the supervisor publishes belong here.
    /// </summary>
    private static readonly string[] EngineDiagnosticCodes =
    [
        "engine.runtime.restarting",
        "engine.runtime.restartFailed",
    ];

    private static IReadOnlyDictionary<string, string> Resources(string culture)
    {
        XDocument document = XDocument.Load(AppSourcePaths.ResourcesFile(culture));
        return document.Root!
            .Elements("data")
            .ToDictionary(
                data => (string)data.Attribute("name")!,
                data => data.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    [Fact]
    public void Every_emitted_code_has_a_sentence_and_a_subsystem()
    {
        string[] codes = [.. EmittedCodes(), .. EngineDiagnosticCodes];
        Assert.NotEmpty(codes);

        List<string> unmapped =
        [
            .. codes.Where(code =>
                RuntimeDiagnosticPresenter.ResourceKeyFor(code) ==
                    RuntimeDiagnosticPresenter.UnknownResourceKey ||
                RuntimeDiagnosticPresenter.CategoryResourceKeyFor(code) ==
                    RuntimeDiagnosticPresenter.UnknownCategoryResourceKey),
        ];

        Assert.True(
            unmapped.Count == 0,
            $"RuntimeDiagnosticPresenter has no wording for: [{string.Join(", ", unmapped)}].");
    }

    // The presenter returns resource names rather than calling GetString, so the lookup-literal guard
    // in LocalizationParityTests cannot see them; a typo would surface as an empty row at runtime.
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void Every_mapped_resource_key_is_declared(string culture)
    {
        IReadOnlyDictionary<string, string> resources = Resources(culture);
        List<string> missing =
        [
            .. RuntimeDiagnosticPresenter.AllResourceKeys
                .Where(key => !resources.ContainsKey(key)),
        ];

        Assert.True(missing.Count == 0, $"Missing in {culture}: [{string.Join(", ", missing)}].");
    }

    /// <summary>
    /// <see cref="RuntimeDiagnosticPresenter.AllResourceKeys"/> is what the parity guard above checks,
    /// so a mapping added without listing it there would go unchecked in both cultures.
    /// </summary>
    [Fact]
    public void Declared_key_list_covers_every_reachable_mapping()
    {
        string[] codes = [.. EmittedCodes(), .. EngineDiagnosticCodes];
        List<string> reachable =
        [
            .. codes.Select(RuntimeDiagnosticPresenter.ResourceKeyFor),
            .. codes.Select(RuntimeDiagnosticPresenter.CategoryResourceKeyFor),
        ];

        List<string> unlisted =
        [
            .. reachable.Distinct(StringComparer.Ordinal)
                .Where(key => !RuntimeDiagnosticPresenter.AllResourceKeys.Contains(key)),
        ];

        Assert.True(unlisted.Count == 0, $"Not listed: [{string.Join(", ", unlisted)}].");
    }
}
