using System.Net;
using System.Text.RegularExpressions;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Fuentes;

/// <summary>
/// Encuentra la página de HowLongToBeat de un juego leyendo dos páginas públicas en HTML (sin APIs):
///
///  1) El artículo del juego en Wikipedia, con su buscador en modo "Ir":
///        https://en.wikipedia.org/wiki/Special:Search?search=Hades (video game)&amp;go=Go
///     Si el artículo existe, su menú lateral trae el enlace "Wikidata item" con el identificador del juego (Q59756366).
///  2) La ficha de ese identificador:  https://www.wikidata.org/wiki/Q59756366
///     En "Identifiers" enlaza la página del juego en HowLongToBeat (howlongtobeat.com/game/62941).
///
/// Solo se acepta una ficha cuyo nombre coincide con el título del repo: así "Persona 5 Royal" no se queda
/// con la ficha de "Persona 5", ni "Mario Kart 8 Deluxe" con la de "Mario Kart 8".
/// Wikipedia pide que los programas se identifiquen: se envía un User-Agent propio con la dirección del repositorio.
/// </summary>
public sealed partial class WikidataScraper(ClienteHttpResiliente http)
{
    private const double SimilitudMinima = 0.7;

    private static readonly Dictionary<string, string> Encabezados = new()
    {
        ["User-Agent"] = "PrecioPixel/1.0 (proyecto academico IC3101; +https://github.com/tavoza2007/proyecto-multi-core)",
    };

    /// <summary>&lt;li id="t-wikibase"&gt;&lt;a href="https://www.wikidata.org/wiki/Special:EntityPage/Q59756366"&gt;Wikidata item&lt;/a&gt;</summary>
    [GeneratedRegex(@"id=""t-wikibase""[\s\S]{0,300}?(Q\d+)")]
    private static partial Regex RegexElemento();

    [GeneratedRegex(@"<title>([^<]+?) - Wikipedia</title>")]
    private static partial Regex RegexTituloArticulo();

    /// <summary>"Hades (video game)" → "Hades" · "DOOM (2016)" → "DOOM"</summary>
    [GeneratedRegex(@"\s*\([^()]*\)\s*$")]
    private static partial Regex RegexParentesisFinal();

    [GeneratedRegex(@"class=""wikibase-title-label""[^>]*>([^<]+)<")]
    private static partial Regex RegexEtiqueta();

    [GeneratedRegex(@"<title>([^<]+?)(?: - Wikidata)?</title>")]
    private static partial Regex RegexTituloFicha();

    [GeneratedRegex(@"howlongtobeat\.com/game(?:/|\?id=)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexHltb();

    /// <summary>URL de la página del juego en HowLongToBeat, o null si Wikidata no la tiene.</summary>
    public async Task<string?> UrlHowLongToBeatAsync(Juego juego, CancellationToken ct)
    {
        var revisados = new HashSet<string>();
        try
        {
            foreach (var termino in Candidatos(juego))
            {
                var (elemento, articulo) = await ArticuloAsync(termino, ct);
                if (elemento is null || !revisados.Add(elemento)) continue;

                var (etiqueta, idHltb) = await FichaAsync(elemento, ct);
                if (idHltb is null) continue; // no es un videojuego o Wikidata aún no tiene su página de HLTB

                var parecido = Math.Max(Texto.SimilitudMismoJuego(juego.Titulo, etiqueta),
                                        Texto.SimilitudMismoJuego(juego.Titulo, articulo));
                if (parecido < SimilitudMinima)
                {
                    Log.Debug($"Wikidata: '{etiqueta}' ({elemento}) no coincide con '{juego.Titulo}'");
                    continue;
                }

                Log.Debug($"Wikidata: '{juego.Titulo}' → {elemento} '{etiqueta}' → HowLongToBeat {idHltb}");
                return $"https://howlongtobeat.com/game/{idHltb}";
            }
        }
        catch (Exception ex) when (ex is FuenteNoDisponibleException or BloqueoDetectadoException)
        {
            // Si Wikipedia o Wikidata no responden, HowLongToBeat prueba con el buscador web
            Log.Debug($"Wikidata/Wikipedia no disponible para '{juego.Titulo}': {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Nombres de artículo a probar, del más específico al más general (los remakes comparten nombre con el original):
    ///   "DOOM (2016 video game)", "DOOM (video game)", "DOOM"
    /// y lo mismo sin "Director's Cut", "Remastered"… Sin la marca ("Marvel's") no se prueba "(video game)":
    /// "Spider-Man (video game)" es otro juego.
    /// </summary>
    private static IEnumerable<string> Candidatos(Juego juego)
    {
        var titulo = RegexParentesisFinal().Replace(juego.Titulo, "").Trim();
        var original = Texto.SinReedicion(titulo);
        var lista = new List<string>();
        foreach (var t in new[] { titulo, original }.Where(x => x.Length > 0))
        {
            if (juego.Anio > 0) lista.Add($"{t} ({juego.Anio} video game)");
            lista.Add($"{t} (video game)");
            lista.Add(t);
        }
        foreach (var t in new[] { Texto.SinMarca(titulo), Texto.SinMarca(original) }.Where(x => x.Length > 0))
        {
            if (juego.Anio > 0) lista.Add($"{t} ({juego.Anio} video game)");
            lista.Add(t);
        }
        return lista.Distinct();
    }

    /// <summary>Paso 1: artículo de Wikipedia → identificador de Wikidata (Q…) y título del artículo.</summary>
    private async Task<(string? elemento, string? articulo)> ArticuloAsync(string termino, CancellationToken ct)
    {
        var url = $"https://en.wikipedia.org/wiki/Special:Search?search={Uri.EscapeDataString(termino)}&go=Go&ns0=1";
        string html;
        try { html = await http.GetStringAsync(url, ct, Encabezados); }
        catch (FuenteNoDisponibleException ex) when (ex.CodigoHttp is 404 or 410) { return (null, null); }

        // Si no existe un artículo con ese nombre, Wikipedia muestra resultados de búsqueda (sin enlace a Wikidata)
        var elemento = RegexElemento().Match(html);
        if (!elemento.Success) return (null, null);

        var titulo = RegexTituloArticulo().Match(html) is { Success: true } t
            ? RegexParentesisFinal().Replace(WebUtility.HtmlDecode(t.Groups[1].Value), "").Trim()
            : null;
        return (elemento.Groups[1].Value, titulo);
    }

    /// <summary>Paso 2: ficha de Wikidata → nombre del juego e identificador de HowLongToBeat.</summary>
    private async Task<(string? etiqueta, string? idHltb)> FichaAsync(string elemento, CancellationToken ct)
    {
        string html;
        try { html = await http.GetStringAsync($"https://www.wikidata.org/wiki/{elemento}", ct, Encabezados); }
        catch (FuenteNoDisponibleException ex) when (ex.CodigoHttp is 404 or 410) { return (null, null); }

        var hltb = RegexHltb().Match(html);
        if (!hltb.Success) return (null, null);

        var etiqueta = RegexEtiqueta().Match(html) is { Success: true } e ? e.Groups[1].Value
                     : RegexTituloFicha().Match(html) is { Success: true } t ? t.Groups[1].Value
                     : null;
        return (etiqueta is null ? null : WebUtility.HtmlDecode(etiqueta).Trim(), hltb.Groups[1].Value);
    }
}
