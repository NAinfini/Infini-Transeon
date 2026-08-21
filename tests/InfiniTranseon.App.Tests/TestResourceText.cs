using System.Xml.Linq;
using InfiniTranseon.App.Presentation;

namespace InfiniTranseon.App.Tests;

/// <summary>
/// A <see cref="ResourceTextLookup"/> over the shipped en-US table. The resource loader is a WinRT
/// activation that is not registered in this host, so the table is read from the .resw the App
/// compiles: the same strings the user sees, without the runtime. An undeclared key throws rather
/// than returning the key, so a row assembled from a missing resource fails the test that builds it.
/// </summary>
internal static class TestResourceText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Strings = new(() =>
        XDocument.Load(AppSourcePaths.ResourcesFile("en-US")).Root!
            .Elements("data")
            .ToDictionary(
                data => (string)data.Attribute("name")!,
                data => data.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal));

    public static ResourceTextLookup Lookup { get; } = key =>
        Strings.Value.TryGetValue(key, out string? value)
            ? value
            : throw new KeyNotFoundException($"Resource '{key}' is not declared in en-US.");
}
