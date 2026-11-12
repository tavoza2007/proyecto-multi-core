using System.Text.Json;
using System.Text.RegularExpressions;
using PrecioPixel.Config;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Fuentes;

/// <summary>
/// HowLongToBeat — web scraping de la página del juego (howlongtobeat.com/game/{id}).
/// Como HLTB identifica los juegos con un número, la página se encuentra así:
///   1) URL fijada en el repo (urlsTienda.hltb), o
///   2) buscador web: "howlongtobeat {título}" → primer resultado howlongtobeat.com/game/…
/// De la página se leen los tiempos que muestra: historia principal, principal + extras, completista y todos los estilos.
/// </summary>
public sealed partial class HowLongToBeatScraper(ClienteHttpResiliente http, BuscadorWeb buscador, Opciones opt)
{
    public string UrlBase => "https://howlongtobeat.com/";

    [GeneratedRegex(@"howlongtobeat\.com/game/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex RegexUrlJuego();

    // Respaldo sobre el texto visible: "Main Story 28 Hours", "Main + Sides 34½ Hours"
    [GeneratedRegex(@"(Main Story|Main \+ Sides?|Main \+ Extras?|Completionist|All Styles)\s*([\d½.,]+)\s*(Hours?|Mins?)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexTiempoVisible();

    public async Task<HltbInfo?> ObtenerAsync(Juego juego, CancellationToken ct)
    {
        var url = juego.Url("hltb")
                  ?? await buscador.PrimeraUrlAsync($"howlongtobeat {juego.Titulo}", u => RegexUrlJuego().IsMatch(u), ct);
        if (url is null) return null;
        url = "https://" + RegexUrlJuego().Match(url).Value; // limpia /reviews, /lists, parámetros…

        var html = await http.GetStringAsync(url, ct, new Dictionary<string, string> { ["Referer"] = "https://howlongtobeat.com/" });
        using var doc = Html.Parsear(html);

        var titulo = Html.Texto(doc, "[class*='GameHeader_profile_header'] div", "h1") ?? Html.Meta(doc, "og:title") ?? "";
        titulo = titulo.Replace("How long is", "").Replace("| HowLongToBeat", "").Trim().TrimEnd('?').Trim();

        var info = DesdeEstadoIncrustado(doc) ?? DesdeTextoVisible(doc.Body?.TextContent ?? "");
        if (info is null) return null;

        info.Url = url;
        info.TituloEncontrado ??= titulo;
        info.ImagenUrl ??= Html.Meta(doc, "og:image"); // portada (box art) del juego
        if (Texto.Similitud(juego.Titulo, info.TituloEncontrado ?? "") < opt.Ofertas.SimilitudMinimaTitulo)
        {
            Log.Debug($"HLTB: '{info.TituloEncontrado}' no coincide con '{juego.Titulo}'");
            return null;
        }
        return info;
    }

    /// <summary>La página trae sus datos en &lt;script id="__NEXT_DATA__"&gt;: comp_main, comp_plus, comp_100, comp_all (segundos).</summary>
    private static HltbInfo? DesdeEstadoIncrustado(AngleSharp.Dom.IDocument doc)
    {
        if (Html.NextData(doc) is not { } estado) return null;
        foreach (var o in Html.Objetos(estado))
        {
            if (!o.TryGetProperty("comp_main", out _)) continue;
            return new HltbInfo
            {
                Principal = Horas(o, "comp_main"),
                PrincipalExtras = Horas(o, "comp_plus"),
                Completista = Horas(o, "comp_100"),
                TodosEstilos = Horas(o, "comp_all"),
                TituloEncontrado = Html.Cadena(o, "game_name"),
                // "game_image": "12345_Hades.jpg" → https://howlongtobeat.com/games/12345_Hades.jpg
                ImagenUrl = Html.Cadena(o, "game_image") is { Length: > 0 } img
                    ? (img.StartsWith("http") ? img : $"https://howlongtobeat.com/games/{img}") : null,
            };
        }
        return null;
    }

    private static double? Horas(JsonElement o, string prop)
    {
        var seg = Html.Numero(o, prop);
        return seg is null or <= 0 ? null : Math.Round(seg.Value / 3600.0, 1);
    }

    private static HltbInfo? DesdeTextoVisible(string texto)
    {
        var info = new HltbInfo();
        foreach (Match m in RegexTiempoVisible().Matches(texto))
        {
            var numero = double.TryParse(m.Groups[2].Value.Replace("½", ".5").Replace(",", ""),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (numero <= 0) continue;
            var horas = m.Groups[3].Value.StartsWith("min", StringComparison.OrdinalIgnoreCase) ? Math.Round(numero / 60, 1) : numero;
            var etiqueta = m.Groups[1].Value.ToLowerInvariant();
            if (etiqueta.StartsWith("main story")) info.Principal ??= horas;
            else if (etiqueta.StartsWith("main +")) info.PrincipalExtras ??= horas;
            else if (etiqueta.StartsWith("completionist")) info.Completista ??= horas;
            else if (etiqueta.StartsWith("all styles")) info.TodosEstilos ??= horas;
        }
        return info.Principal is null && info.PrincipalExtras is null && info.Completista is null ? null : info;
    }
}
