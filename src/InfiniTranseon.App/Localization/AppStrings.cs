using System.Threading;
using Microsoft.Windows.ApplicationModel.Resources;

namespace InfiniTranseon.App.Localization;

/// <summary>
/// The single <see cref="ResourceLoader"/> every view uses to read localized strings from
/// code-behind.
///
/// A <see cref="ResourceLoader"/> captures its resolution context when it is constructed, so a
/// loader held in a <c>static readonly</c> field keeps answering in the language that was current at
/// class initialization. Seventeen views each held their own such field, which is why changing the
/// UI language left every code-behind string in the launch language no matter what else was
/// refreshed. Routing them all through <see cref="Loader"/> gives the language switch one place to
/// invalidate.
/// </summary>
internal static class AppStrings
{
    private static ResourceLoader _loader = Create();

    /// <summary>
    /// The loader for the language currently in effect. Read it per lookup rather than caching it in
    /// a field, otherwise the caller reintroduces the staleness this type exists to remove.
    /// </summary>
    internal static ResourceLoader Loader => Volatile.Read(ref _loader);

    /// <summary>
    /// Discards the cached loader so the next lookup resolves against the language that is now
    /// current. Call after changing <c>ApplicationLanguages.PrimaryLanguageOverride</c>; the override
    /// alone changes nothing for a loader that already exists.
    /// </summary>
    internal static void Reload() => Volatile.Write(ref _loader, Create());

    private static ResourceLoader Create() =>
        new(ResourceLoader.GetDefaultResourceFilePath(), "Resources");
}
