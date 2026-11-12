using System.Globalization;
using System.Text.RegularExpressions;

namespace PrecioPixel.Util;

/// <summary>
/// Fechas de lanzamiento. Cada sitio las escribe distinto:
///   Metacritic   "2020-09-17"                 (JSON-LD datePublished)
///   eShop        "2020-09-17T00:00:00.000Z"   (estado incrustado de la ficha)
///   Steam        "Sep 17, 2020" o "17 Sep, 2020"
/// Todas se guardan como "yyyy-MM-dd" (ISO 8601): se ordenan bien como texto y la web las muestra en español.
/// </summary>
public static partial class Fechas
{
    private static readonly string[] Formatos =
    {
        "MMM d, yyyy", "MMMM d, yyyy", "d MMM, yyyy", "d MMMM, yyyy", "d MMM yyyy", "d MMMM yyyy",
        "MMM d yyyy", "MMMM d yyyy", "M/d/yyyy",
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")] private static partial Regex Iso();
    [GeneratedRegex(@"(?<=[A-Za-z])\.")] private static partial Regex PuntoDeAbreviatura();
    [GeneratedRegex(@"\bSept\b", RegexOptions.IgnoreCase)] private static partial Regex Sept();

    /// <summary>"yyyy-MM-dd", o null si el texto no es una fecha completa ("Coming soon", "2025", "Q3 2026"…).</summary>
    public static string? Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = texto.Trim();

        if (Iso().Match(t) is { Success: true } iso)
            return DateTime.TryParseExact(iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1) && Valida(d1)
                ? iso.Value
                : null;

        t = Sept().Replace(PuntoDeAbreviatura().Replace(t, ""), "Sep"); // "Sept. 4, 2025" → "Sep 4, 2025"
        return DateTime.TryParseExact(t, Formatos, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d2) && Valida(d2)
            ? d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Año de una fecha "yyyy-MM-dd".</summary>
    public static int? Anio(string? iso) =>
        iso is { Length: >= 4 } && int.TryParse(iso[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : null;

    private static bool Valida(DateTime d) => d.Year is >= 1970 and <= 2100;
}
