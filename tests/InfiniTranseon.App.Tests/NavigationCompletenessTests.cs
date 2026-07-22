using System.Xml.Linq;
using InfiniTranseon.App.Composition;
using InfiniTranseon.App.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniTranseon.App.Tests;

public sealed class NavigationCompletenessTests
{
    private static IReadOnlySet<string> NavigationViewItemTagsFromXaml()
    {
        XDocument document = XDocument.Load(AppSourcePaths.AppShellXaml);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(element => (string?)element.Attribute("Tag"))
            .Where(tag => tag is not null)
            .Select(tag => tag!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Xaml_nav_item_tags_match_navigation_map_exactly()
    {
        IReadOnlySet<string> xamlTags = NavigationViewItemTagsFromXaml();

        IEnumerable<string> unmapped = xamlTags.Except(NavigationMap.Tags);
        IEnumerable<string> orphaned = NavigationMap.Tags.Except(xamlTags);

        Assert.True(
            xamlTags.SetEquals(NavigationMap.Tags),
            $"Nav items without a mapping: [{string.Join(", ", unmapped)}]; " +
            $"mappings without a nav item: [{string.Join(", ", orphaned)}].");
    }

    [Fact]
    public void Every_navigable_destination_view_model_resolves_from_container()
    {
        using ServiceProvider provider = PresentationComposition.Build();

        foreach (NavigationDestination destination in NavigationMap.Destinations)
        {
            if (destination.ViewModelType is null)
            {
                continue;
            }

            object viewModel = provider.GetRequiredService(destination.ViewModelType);
            Assert.NotNull(viewModel);
        }
    }

    [Fact]
    public void Ensure_consistent_accepts_the_canonical_tag_set()
    {
        NavigationMap.EnsureConsistent(NavigationMap.Tags);
    }

    [Fact]
    public void Ensure_consistent_rejects_a_missing_tag()
    {
        List<string> withoutOne = [.. NavigationMap.Tags.Skip(1)];

        Assert.Throws<InvalidOperationException>(() => NavigationMap.EnsureConsistent(withoutOne));
    }

    [Fact]
    public void Ensure_consistent_rejects_an_orphan_tag()
    {
        List<string> withExtra = [.. NavigationMap.Tags, "not-a-real-page"];

        Assert.Throws<InvalidOperationException>(() => NavigationMap.EnsureConsistent(withExtra));
    }
}
