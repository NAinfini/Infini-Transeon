using InfiniTranseon.Contracts.Translation;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Translation;

public static class ContextBuilder
{
    private const int MaximumHistoryItems = 8;
    private const int MaximumContextCharacters = 16_384;

    public static TranslationContext ApplyPolicy(
        TranslationContext context,
        ContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        IReadOnlyList<string> recentSource = policy.IncludeRecentHistory
            ? TakeBoundedTail(context.RecentSource)
            : [];
        IReadOnlyList<string> recentTranslation = policy.IncludeRecentHistory
            ? TakeBoundedTail(context.RecentTranslation)
            : [];
        return new TranslationContext(
            policy.IncludeGame ? Bound(context.GameName) : null,
            policy.IncludeGame ? Bound(context.GameDescription) : null,
            policy.IncludeScene ? Bound(context.Scene) : null,
            policy.IncludeScene ? Bound(context.Speaker) : null,
            recentSource,
            recentTranslation);
    }

    private static IReadOnlyList<string> TakeBoundedTail(IReadOnlyList<string> values)
    {
        var result = new LinkedList<string>();
        int characters = 0;
        for (int index = values.Count - 1; index >= 0 && result.Count < MaximumHistoryItems; index--)
        {
            string value = Bound(values[index]) ?? string.Empty;
            if (characters + value.Length > MaximumContextCharacters) break;
            result.AddFirst(value);
            characters += value.Length;
        }
        return result.ToArray();
    }

    private static string? Bound(string? value) => string.IsNullOrEmpty(value)
        ? value
        : value[..Math.Min(value.Length, MaximumContextCharacters)];
}
