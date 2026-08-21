using System.Text.RegularExpressions;
using System.Xml.Linq;
using InfiniTranseon.App.Presentation;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// The activity feed renders a status event entirely through <see cref="StatusEventPresenter"/>. An
/// emit site added without a matching mapping does not fail — it renders the "unrecognized" wording,
/// which reads like a runtime problem instead of the missing translation it is. These tests read the
/// emit sites out of the source so the omission fails the build instead.
/// </summary>
public sealed class StatusEventPresenterTests
{
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

    /// <summary>Message keys as the emitters spell them. Every one is a literal argument, so the
    /// source is the inventory; nothing has to be kept in sync by hand.</summary>
    private static IReadOnlyList<string> EmittedMessageKeys() =>
        [.. AppSourcePaths.AllSourceTreeCSharpFiles()
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"""(status\.[A-Za-z0-9.]+)"""))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>The category is the second constructor argument of every StatusEvent, and is always
    /// a literal because it names the emitting subsystem rather than the occasion.</summary>
    private static IReadOnlyList<string> EmittedCategories() =>
        [.. AppSourcePaths.AllSourceTreeCSharpFiles()
            .SelectMany(file => Regex.Matches(
                File.ReadAllText(file),
                @"new StatusEvent\(\s*[^,]+,\s*""([^""]+)"""))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_emitted_message_key_has_its_own_sentence()
    {
        IReadOnlyList<string> keys = EmittedMessageKeys();
        Assert.NotEmpty(keys);

        List<string> unmapped =
        [
            .. keys.Where(key =>
                StatusEventPresenter.MessageResourceKeyFor(key) ==
                StatusEventPresenter.UnknownMessageResourceKey),
        ];

        Assert.True(
            unmapped.Count == 0,
            $"StatusEventPresenter has no sentence for: [{string.Join(", ", unmapped)}].");
    }

    [Fact]
    public void Every_emitted_category_has_its_own_label()
    {
        IReadOnlyList<string> categories = EmittedCategories();
        Assert.NotEmpty(categories);

        List<string> unmapped =
        [
            .. categories.Where(category =>
                StatusEventPresenter.CategoryResourceKeyFor(category) ==
                StatusEventPresenter.UnknownCategoryResourceKey),
        ];

        Assert.True(
            unmapped.Count == 0,
            $"StatusEventPresenter has no label for: [{string.Join(", ", unmapped)}].");
    }

    // The presenter returns resource names rather than calling GetString, so the lookup-literal guard
    // in LocalizationParityTests cannot see them; a typo would surface as an empty row at runtime.
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void Every_mapped_resource_key_is_declared(string culture)
    {
        IReadOnlyDictionary<string, string> resources = Resources(culture);
        List<string> required =
        [
            StatusEventPresenter.UnknownCategoryResourceKey,
            StatusEventPresenter.UnknownMessageResourceKey,
            .. EmittedCategories().Select(StatusEventPresenter.CategoryResourceKeyFor),
            .. EmittedMessageKeys().Select(StatusEventPresenter.MessageResourceKeyFor),
        ];

        List<string> missing = [.. required.Distinct(StringComparer.Ordinal)
            .Where(key => !resources.ContainsKey(key))];

        Assert.True(missing.Count == 0, $"Missing in {culture}: [{string.Join(", ", missing)}].");
    }

    // A shared sentence would make two different events indistinguishable in the feed, which is the
    // failure this whole mapping exists to prevent.
    [Fact]
    public void Mapped_message_keys_do_not_share_a_resource_key()
    {
        List<string> duplicated =
        [
            .. EmittedMessageKeys()
                .GroupBy(StatusEventPresenter.MessageResourceKeyFor, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key} ← [{string.Join(", ", group)}]"),
        ];

        Assert.True(duplicated.Count == 0, $"Shared sentences: [{string.Join("; ", duplicated)}].");
    }
}
