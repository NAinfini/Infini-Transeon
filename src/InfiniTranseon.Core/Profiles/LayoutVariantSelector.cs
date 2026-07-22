namespace InfiniTranseon.Core.Profiles;

public sealed class LayoutVariantSelector
{
    public ProfileLayoutVariant? Select(
        IReadOnlyList<ProfileLayoutVariant> variants,
        int contentWidthPixels,
        int contentHeightPixels)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (contentWidthPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentWidthPixels));
        if (contentHeightPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentHeightPixels));

        double aspectRatio = (double)contentWidthPixels / contentHeightPixels;
        foreach (ProfileLayoutVariant variant in variants)
        {
            if (aspectRatio < variant.MinimumAspectRatio ||
                aspectRatio > variant.MaximumAspectRatio ||
                contentWidthPixels < variant.MinimumWidthPixels ||
                contentWidthPixels > variant.MaximumWidthPixels)
            {
                continue;
            }
            return variant;
        }
        return null;
    }
}
