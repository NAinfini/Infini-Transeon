using InfiniTranseon.Core.Profiles;

namespace InfiniTranseon.Core.Tests.Profiles;

public sealed class LayoutVariantTests
{
    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(1920, 1200, "16:10")]
    [InlineData(3440, 1440, "21:9")]
    [InlineData(1600, 1200, "4:3")]
    public void SelectsFirstMatchingAspectRatioVariant(int width, int height, string expected)
    {
        IReadOnlyList<ProfileLayoutVariant> variants = CreateCommonVariants();

        ProfileLayoutVariant? selected = new LayoutVariantSelector().Select(variants, width, height);

        Assert.NotNull(selected);
        Assert.Equal(expected, selected.Name);
    }

    [Fact]
    public void ResolutionHintsDisambiguateOverlappingAspectRangesInUserOrder()
    {
        ProfileLayoutVariant fourK = Variant("4K", 1.7, 1.8) with { MinimumWidthPixels = 3000 };
        ProfileLayoutVariant hd = Variant("HD", 1.7, 1.8) with { MaximumWidthPixels = 2999 };
        IReadOnlyList<ProfileLayoutVariant> variants = [fourK, hd];

        Assert.Equal("HD", new LayoutVariantSelector().Select(variants, 1920, 1080)?.Name);
        Assert.Equal("4K", new LayoutVariantSelector().Select(variants, 3840, 2160)?.Name);
    }

    [Fact]
    public void ReturnsBaseLayoutWhenNoVariantMatchesAndRejectsInvalidContentSize()
    {
        var selector = new LayoutVariantSelector();

        Assert.Null(selector.Select(CreateCommonVariants(), 1000, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => selector.Select([], 0, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => selector.Select([], 1920, -1));
    }

    private static IReadOnlyList<ProfileLayoutVariant> CreateCommonVariants() =>
    [
        Variant("16:9", 1.76, 1.79),
        Variant("16:10", 1.59, 1.61),
        Variant("21:9", 2.38, 2.40),
        Variant("4:3", 1.32, 1.34),
    ];

    private static ProfileLayoutVariant Variant(string name, double minimum, double maximum) => new()
    {
        Name = name,
        MinimumAspectRatio = minimum,
        MaximumAspectRatio = maximum,
    };
}
