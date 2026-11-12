using System.Collections.Concurrent;
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
///   1) URL guardada en el REPO (urlsTienda.hltb), o
///   2) la ficha del juego en Wikidata, que enlaza su página de HLTB (ver WikidataScraper), o
///   3) buscador web: "howlongtobeat {título}" → primer resultado howlongtobeat.com/game/…
/// Las páginas encontradas con 2) o 3) se guardan en el REPO al terminar la corrida: la próxima va directo.
/// De la página se leen los tiempos que muestra: historia principal, principal + extras, completista y todos los estilos.
/// </summary>
public sealed partial class HowLongToBeatScraper(ClienteHttpResiliente http, BuscadorWeb buscador, WikidataScraper wikidata, Opciones opt)
{
    public string UrlBase => "https://howlongtobeat.com/";

    /// <summary>Páginas de HLTB descubiertas en esta corrida (id del juego → URL). Program las guarda en el REPO.</summary>
    public ConcurrentDictionary<string, string> UrlsDescubiertas { get; } = new();

    [GeneratedRegex(@"howlongtobeat\.com/game(?:/|\?id=)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexUrlJuego();

    // Respaldo sobre el texto visible: "Main Story 28 Hours", "Main + Sides 34½ Hours"
    [GeneratedRegex(@"(Main Story|Main \+ Sides?|Main \+ Extras?|Completionist|All Styles)\s*([\d½.,]+)\s*(Hours?|Mins?)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexTiempoVisible();

    public async Task<HltbInfo?> ObtenerAsync(Juego juego, CancellationToken ct)
    {
        var url = Canonica(juego.Url("hltb"));
        var enRepo = url is not null;
        var porBuscador = false;

        if (url is null && Canonica(await wikidata.UrlHowLongToBeatAsync(juego, ct)) is { } desdeWikidata)
        {
            url = desdeWikidata;
            // Wikidata ya confirmó que es el mismo juego: se guarda aunque HLTB no responda en esta corrida
            UrlsDescubiertas[juego.Id] = url;
        }
        if (url is null)
        {
            url = Canonica(await buscador.PrimeraUrlAsync($"howlongtobeat {juego.Titulo}", u => RegexUrlJuego().IsMatch(u), ct));
            porBuscador = url is not null;
        }
        if (url is null) return null;

        var html = await http.GetStringAsync(url, ct, new Dictionary<string, string> { ["Referer"] = "https://howlongtobeat.com/" });
        using var doc = Html.Parsear(html);

        var titulo = Html.Texto(doc, "[class*='GameHeader_profile_header'] div", "h1") ?? Html.Meta(doc, "og:title") ?? "";
        titulo = titulo.Replace("How long is", "").Replace("| HowLongToBeat", "").Trim().TrimEnd('?').Trim();

        var info = DesdeEstadoIncrustado(doc, juego.Titulo) ?? DesdeTextoVisible(doc.Body?.TextContent ?? "");
        if (info is null) return null;

        info.Url = url;
        info.TituloEncontrado ??= titulo;
        info.ImagenUrl ??= Html.Meta(doc, "og:image"); // portada (box art) del juego
        if (Texto.SimilitudMismoJuego(juego.Titulo, info.TituloEncontrado) < opt.Ofertas.SimilitudMinimaTitulo)
        {
            Log.Debug($"HLTB: '{info.TituloEncontrado}' no coincide con '{juego.Titulo}'");
            if (!enRepo) UrlsDescubiertas.TryRemove(juego.Id, out _);
            return null;
        }

        // Una página encontrada con el buscador se guarda solo después de confirmar el nombre del juego
        if (porBuscador) UrlsDescubiertas[juego.Id] = url;
        return info;
    }

    /// <summary>howlongtobeat.com/game/62941/reviews · howlongtobeat.com/game?id=62941 → https://howlongtobeat.com/game/62941</summary>
    private static string? Canonica(string? url) =>
        url is not null && RegexUrlJuego().Match(url) is { Success: true } m ? $"https://howlongtobeat.com/game/{m.Groups[1].Value}" : null;

    /// <summary>
    /// La página trae sus datos en &lt;script id="__NEXT_DATA__"&gt;, con los tiempos en segundos (comp_main, comp_plus,
    /// comp_100, comp_all). Ahí hay varios objetos con tiempos: el del juego (pageProps.game.data.game[0], con game_name),
    /// los de sus DLC (relationships, también con nombre) y los promedios de cada plataforma (platformData,
    /// individuality, sin nombre). Se usa el objeto con nombre que más se parece al juego del repo.
    /// </summary>
    private static HltbInfo? DesdeEstadoIncrustado(AngleSharp.Dom.IDocument doc, string tituloRepo)
    {
        if (Html.NextData(doc) is not { } estado) return null;

        JsonElement? delJuego = null;
        var mejorParecido = -1.0;
        foreach (var o in Html.Objetos(estado))
        {
            if (!o.TryGetProperty("comp_main", out _) || Html.Cadena(o, "game_name") is not { Length: > 0 } nombre) continue;
            var parecido = Texto.SimilitudMismoJuego(tituloRepo, nombre);
            if (parecido > mejorParecido)
            {
                mejorParecido = parecido;
                delJuego = o;
            }
        }
        if (delJuego is not { } elegido) return null; // sin objeto con nombre: se usa el texto visible

        return new HltbInfo
        {
            Principal = Horas(elegido, "comp_main"),
            PrincipalExtras = Horas(elegido, "comp_plus"),
            Completista = Horas(elegido, "comp_100"),
            TodosEstilos = Horas(elegido, "comp_all"),
            TituloEncontrado = Html.Cadena(elegido, "game_name"),
            // "game_image": "12345_Hades.jpg" → https://howlongtobeat.com/games/12345_Hades.jpg
            ImagenUrl = Html.Cadena(elegido, "game_image") is { Length: > 0 } img
                ? (img.StartsWith("http") ? img : $"https://howlongtobeat.com/games/{img}") : null,
        };
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
