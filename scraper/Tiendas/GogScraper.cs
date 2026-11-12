using PrecioPixel.Config;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Tiendas;

/// <summary>
/// GOG.com (PC) — segunda tienda de PC, web scraping de la ficha del juego:
///   gog.com/en/game/{slug}  → precio final y precio base (.product-actions-price__…) y JSON-LD offers.price
/// Nota: GOG cobra precios regionales según el país de la conexión (desde Costa Rica salen más bajos que en EE. UU.).
/// </summary>
public sealed class GogScraper(ClienteHttpResiliente http, BuscadorWeb buscador, Opciones opt) : ITiendaScraper
{
    private const string Raiz = "https://www.gog.com";

    // Preferencia de idioma inglés y moneda USD (la misma cookie que guarda el sitio).
    // El precio sigue siendo el de la región de la conexión: GOG lo decide por la IP.
    private static readonly Dictionary<string, string> Region = new() { ["Cookie"] = "gog_lc=US_USD_en-US" };

    public string Nombre => "GOG";
    public string UrlBase => Raiz + "/";

    public bool AplicaA(Juego juego) => juego.Tiene("pc");

    public async Task<List<PrecioTienda>> BuscarAsync(Juego juego, CancellationToken ct)
    {
        if (juego.Url("gog") is { } directa)
            return Filas.De(await IntentarFichaAsync(juego, directa, ct));

        // La ficha de GOG usa el título con guiones bajos: "The Witcher 3: Wild Hunt" → the_witcher_3_wild_hunt.
        // (El catálogo /games?query= no sirve para scraping: el filtro de búsqueda se aplica con JavaScript.)
        foreach (var slug in Texto.VariantesSlug(juego.Titulo, juego.Busqueda).Take(6).Select(v => v.Replace('-', '_')))
            if (await IntentarFichaAsync(juego, $"{Raiz}/en/game/{slug}", ct) is { } f)
                return Filas.De(f);

        // Último recurso: buscar la ficha con el buscador web
        var encontrada = await buscador.PrimeraUrlAsync($"gog.com {juego.Titulo}", u => u.Contains("gog.com/") && u.Contains("/game/"), ct);
        return encontrada is null ? new() : Filas.De(await IntentarFichaAsync(juego, encontrada, ct));
    }

    private async Task<PrecioTienda?> IntentarFichaAsync(Juego juego, string url, CancellationToken ct)
    {
        try { return await FichaAsync(juego, url, ct); }
        catch (FuenteNoDisponibleException ex) when (ex.CodigoHttp is 404 or 410) { return null; } // GOG no vende ese juego
    }

    private async Task<PrecioTienda?> FichaAsync(Juego juego, string url, CancellationToken ct)
    {
        using var doc = Html.Parsear(await http.GetStringAsync(url, ct, Region));
        var titulo = Html.Texto(doc, "h1.productcard-basics__title", "h1") ?? Html.Meta(doc, "og:title") ?? "";
        var sim = Texto.Similitud(juego.Titulo, titulo);
        if (sim < opt.Ofertas.SimilitudMinimaTitulo) return null;

        var final = Texto.ParsearPrecioUsd(Html.Texto(doc, ".product-actions-price__final-amount", "[selenium-id='ProductFinalPrice']"))
                    ?? Html.PrecioJsonLd(doc);
        if (final is null) return null;
        var basePrecio = Texto.ParsearPrecioUsd(Html.Texto(doc, ".product-actions-price__base-amount", "[selenium-id='ProductBasePrice']"));

        return Fila(juego, titulo, sim, final.Value, basePrecio, url, Html.Meta(doc, "og:image"));
    }

    private PrecioTienda Fila(Juego juego, string titulo, double sim, double final, double? basePrecio, string url, string? imagen) => new()
    {
        Tienda = Nombre,
        Plataforma = "pc",
        Formato = "digital",
        PrecioActual = final,
        PrecioRegular = basePrecio is > 0 && basePrecio > final ? basePrecio.Value : final,
        Url = url,
        TituloEncontrado = titulo,
        ImagenUrl = imagen,
        Similitud = sim,
    };
}
