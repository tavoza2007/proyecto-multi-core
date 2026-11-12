using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PrecioPixel.Util;

/// <summary>Normalización de títulos, comparación difusa y parseo de precios.</summary>
public static partial class Texto
{
    // Palabras que indican que el resultado NO es el juego base (DLC, banda sonora, etc.)
    private static readonly string[] PalabrasRuido =
    {
        "dlc", "soundtrack", "ost", "season pass", "expansion pass", "pass", "expansion", "add on", "addon", "booster", "costume", "skin", "pack", "bundle",
        "demo", "artbook", "upgrade", "currency", "coins", "points", "vbucks", "amiibo", "guide", "strategy guide",
        "controller", "case", "poster", "figure", "funko", "t-shirt", "keychain", "steelbook only"
    };

    private static readonly Dictionary<string, string> Romanos = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7",
        ["viii"] = "8", ["ix"] = "9", ["x"] = "10", ["xi"] = "11", ["xii"] = "12", ["xiii"] = "13",
        ["xiv"] = "14", ["xv"] = "15", ["xvi"] = "16",
    };

    private static readonly HashSet<string> Vacias = new() { "the", "a", "an", "of", "and", "edition", "game" };

    [GeneratedRegex(@"[^a-z0-9 ]+")] private static partial Regex NoAlfanumerico();
    [GeneratedRegex(@"\s+")] private static partial Regex Espacios();
    [GeneratedRegex(@"[\d.,]+")] private static partial Regex Numero();

    public static string Normalizar(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sinAcentos = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sinAcentos.Append(c);

        var t = sinAcentos.ToString().ToLowerInvariant()
            .Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("&", " and ").Replace("'", "").Replace("’", "");
        t = NoAlfanumerico().Replace(t, " ");
        t = Espacios().Replace(t, " ").Trim();
        return t;
    }

    private static List<string> Tokens(string s) =>
        Normalizar(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Romanos.TryGetValue(w, out var n) ? n : w)
            .Where(w => !Vacias.Contains(w))
            .ToList();

    /// <summary>
    /// Similitud 0..1 entre el título del repo y el título encontrado en una tienda.
    /// 1 = idénticos. Penaliza tokens extra y resultados que son DLC/soundtrack/etc.
    /// </summary>
    public static double Similitud(string tituloRepo, string tituloEncontrado)
    {
        var a = Tokens(tituloRepo);
        var b = Tokens(tituloEncontrado);
        if (a.Count == 0 || b.Count == 0) return 0;
        if (a.SequenceEqual(b)) return 1;
        // "FARCRY 6" vs "Far Cry 6", "Persona®5" vs "Persona 5": iguales sin espacios
        if (string.Concat(a) == string.Concat(b)) return 0.97;

        var sa = a.ToHashSet();
        var sb = b.ToHashSet();
        // Un año que solo está en uno de los dos títulos no cuenta ("DOOM (2016)" vs "DOOM")
        sa.RemoveWhere(t => EsAnio(t) && !sb.Contains(t));
        sb.RemoveWhere(t => EsAnio(t) && !sa.Contains(t));
        if (sa.Count == 0 || sb.Count == 0) return 0;
        var comunes = sa.Intersect(sb).Count();
        double puntaje;

        if (sa.IsSubsetOf(sb))
        {
            // El encontrado contiene todo el título y algo más ("Celeste" vs "Celeste Deluxe Edition")
            var extra = sb.Count - sa.Count;
            puntaje = Math.Max(0.55, 0.95 - 0.07 * extra);
        }
        else
        {
            puntaje = (double)comunes / sa.Union(sb).Count(); // Jaccard
            // El orden importa poco, pero premiamos que empiecen igual
            if (a[0] == b[0]) puntaje += 0.1;
            // Si al encontrado le faltan palabras del título del repo, probablemente es otro juego:
            // "Prince of Persia: The Lost Crown" ≠ "Prince of Persia", "Mass Effect Legendary" ≠ "Mass Effect Andromeda"
            puntaje *= (double)comunes / sa.Count;
        }

        // Un número de entrega que sobra o que falta casi siempre es otro juego:
        // "Portal" vs "Portal 2", "Alan Wake 2" vs "Alan Wake". Los años ("DOOM (2016)") no cuentan.
        if (sb.Except(sa).Any(EsNumeroDeEntrega) || sa.Except(sb).Any(EsNumeroDeEntrega))
            puntaje *= 0.5;

        var normEncontrado = " " + Normalizar(tituloEncontrado) + " ";
        var normRepo = " " + Normalizar(tituloRepo) + " ";
        foreach (var ruido in PalabrasRuido)
            if (normEncontrado.Contains($" {ruido} ") && !normRepo.Contains($" {ruido} "))
            {
                puntaje *= 0.4;
                break;
            }

        return Math.Clamp(puntaje, 0, 1);
    }

    private static bool EsAnio(string token) =>
        token.Length == 4 && token.All(char.IsDigit) && int.Parse(token) is >= 1970 and <= 2039;

    private static bool EsNumeroDeEntrega(string token) =>
        token.All(char.IsDigit) && !(token.Length == 4 && int.Parse(token) is >= 1970 and <= 2039);

    [GeneratedRegex(@"\b(CRC|MXN|COP|CLP|PEN|ARS|BRL|EUR|GBP|CAD|AUD|JPY|CNY|KRW|INR)\b|₡|€|£|¥|₩|R\$|CA\$|MX\$|A\$|COL\$", RegexOptions.IgnoreCase)]
    private static partial Regex OtraMoneda();

    /// <summary>
    /// Como <see cref="ParsearPrecio"/> pero solo acepta dólares estadounidenses.
    /// Si el texto trae otra moneda (ej. "CRC 24,332.50" cuando el sitio detecta que estás en Costa Rica) devuelve null,
    /// para nunca mostrar colones como si fueran dólares.
    /// </summary>
    public static double? ParsearPrecioUsd(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || OtraMoneda().IsMatch(s)) return null;
        return ParsearPrecio(s);
    }

    /// <summary>"$1,299.99" → 1299.99 ; "Free" → 0 ; texto sin número → null.</summary>
    public static double? ParsearPrecio(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Contains("free", StringComparison.OrdinalIgnoreCase) || s.Contains("gratis", StringComparison.OrdinalIgnoreCase))
            return 0;
        var m = Numero().Match(s);
        if (!m.Success) return null;
        var num = m.Value.Replace(",", "");
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static readonly Dictionary<string, string> ArabigoARomano =
        Romanos.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// Direcciones posibles de la página de un juego, en orden de probabilidad. Cada sitio escribe distinto
    /// los números de las secuelas: playstation.com usa "armored-core-6-…", Metacritic "alan-wake-ii".
    ///   título completo → con números arábigos → con números romanos → sin el subtítulo (lo que va después de ":")
    /// </summary>
    public static IEnumerable<string> VariantesSlug(params string?[] titulos)
    {
        var vistos = new HashSet<string>();
        foreach (var titulo in titulos)
        {
            if (string.IsNullOrWhiteSpace(titulo)) continue;
            // Metacritic y Nintendo escriben "+" como "plus": "mario-plus-rabbids-sparks-of-hope"
            var conPlus = titulo.Contains('+') ? titulo.Replace("+", " plus ") : null;
            foreach (var texto in new[] { titulo, conPlus, titulo.Split(':')[0] }.OfType<string>())
            {
                var slug = Slug(texto);
                foreach (var v in new[] { slug, CambiarNumeros(slug, Romanos), CambiarNumeros(slug, ArabigoARomano) })
                    if (v.Length > 0 && vistos.Add(v)) yield return v;
            }
        }
    }

    private static string CambiarNumeros(string slug, Dictionary<string, string> tabla) =>
        string.Join('-', slug.Split('-').Select(t => tabla.TryGetValue(t, out var n) ? n : t));

    /// <summary>"The Legend of Zelda: Breath of the Wild" → "the-legend-of-zelda-breath-of-the-wild"</summary>
    public static string Slug(string s)
    {
        var n = Normalizar(s.Replace("&", "and"));
        return n.Replace(' ', '-');
    }
}
