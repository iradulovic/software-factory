using System.Globalization;

namespace Factory.Core;

/// <summary>Formats a ratio as a compact percentage for logs.</summary>
public static class PercentageFormatter
{
    public static string Format(double ratio) =>
        (ratio * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
