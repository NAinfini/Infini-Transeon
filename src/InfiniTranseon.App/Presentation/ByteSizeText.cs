using System.Globalization;

namespace InfiniTranseon.App.Presentation;

/// <summary>
/// One rule for rendering a byte count. The provider row states a package's download size and the
/// progress line counts up towards that same figure, so the two must agree to the digit: reading
/// "1.2 GB / 2.9 GB" under a row that offered 2.8 GB looks like a different download.
///
/// The units are the IEC abbreviations, which are not translated; a localized number format is
/// still applied so the decimal separator follows the user's culture.
/// </summary>
internal static class ByteSizeText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, Units[unit]);
    }
}
