using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using PrecioPixel.Config;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Tiendas;

/// <summary>
/// PlayStation Store (PS4 / PS5) — web scraping en dos pasos, solo HTML:
///
///  1) Página oficial del juego en playstation.com (su dirección sale del título):
///        https://www.playstation.com/en-us/games/{slug}/
///     Esa página trae el "conceptId" del juego en la tienda.
///
///  2) Ficha de la tienda:  https://store.playstation.com/en-us/concept/{conceptId}
///     Viene renderizada desde el servidor con atributos data-qa:
///        mfe-game-title#name                → nombre
///        mfe-game-title#productTag0..N      → "PS4", "PS5"
///        mfeCtaMain#offer0..N#finalPrice    → precio (una oferta puede ser la de PS Plus o "Included")
///        mfeCtaMain#offer0..N#originalPrice → precio tachado cuando hay descuento
///     y un bloque JSON-LD (schema.org Product) con nombre, imagen y precio.
///
/// (La página de búsqueda de la tienda ya no sirve para scraping: carga los resultados con JavaScript.)
/// </summary>
public sealed partial class PlayStationStoreScraper(ClienteHttpResiliente http, BuscadorWeb buscador, Opciones opt) : ITiendaScraper
{
    public string Nombre => "PlayStation Store";
    public string UrlBase => "https://store.playstation.com/";

    [GeneratedRegex(@"""conceptId""\s*:\s*""?(\d{5,})")]
    private static partial Regex RegexConcepto();

    [GeneratedRegex(@"\b([A-Z]{2}\d{4}-(?:PPSA|CUSA)\d{5}_00-[A-Z0-9]{16})\b")]
    private static partial Regex RegexProducto();

    [GeneratedRegex(@"store\.playstation\.com/[a-z]{2}-[a-z]{2}/((?:product|concept)/[A-Za-z0-9_\-]+)")]
    private static partial Regex RegexFichaTienda();

    public bool AplicaA(Juego juego) => juego.Tiene("ps5", "ps4");

    public async Task<List<PrecioTienda>> BuscarAsync(Juego juego, CancellationToken ct)
    {
        if (juego.Url("playstation") is { } fijada)
            return await LeerFichaAsync(juego, fijada, ct);

        // La página oficial puede llevar a varios productos (el concepto, la edición PS5, un paquete…):
        // se prueba cada uno hasta encontrar el que corresponde al juego del repo
        foreach (var ficha in await FichasDesdePaginaOficialAsync(juego, ct))
            if (await LeerFichaAsync(juego, ficha, ct) is { Count: > 0 } filas)
                return filas;

        var porBuscador = await FichaDesdeBuscadorAsync(juego, ct);
        return porBuscador is null ? new() : await LeerFichaAsync(juego, porBuscador, ct);
    }

    /// <summary>Paso 1: playstation.com/en-us/games/{slug}/ → conceptId y códigos de producto.</summary>
    private async Task<List<string>> FichasDesdePaginaOficialAsync(Juego juego, CancellationToken ct)
    {
        // Variantes: playstation.com escribe "armored-core-6-fires-of-rubicon" (con número arábigo)
        foreach (var slug in Texto.VariantesSlug(juego.Titulo, juego.Busqueda).Take(6))
        {
            string html;
            try { html = await http.GetStringAsync($"https://www.playstation.com/{opt.Red.Idioma}/games/{slug}/", ct); }
            catch (FuenteNoDisponibleException ex) when (ex.CodigoHttp is 404 or 410) { continue; }

            var fichas = new List<string>();
            if (RegexConcepto().Match(html) is { Success: true } c)
                fichas.Add($"https://store.playstation.com/{opt.Red.Idioma}/concept/{c.Groups[1].Value}");
            fichas.AddRange(RegexProducto().Matches(html).Select(m => m.Groups[1].Value)
                .Where(id => !id.Contains("UPGRADE")).Distinct().Take(3)
                .Select(id => $"https://store.playstation.com/{opt.Red.Idioma}/product/{id}"));
            if (fichas.Count > 0) return fichas;
        }
        return new();
    }

    /// <summary>Respaldo: buscar la ficha de la tienda con el buscador web.</summary>
    private async Task<string?> FichaDesdeBuscadorAsync(Juego juego, CancellationToken ct)
    {
        var u = await buscador.PrimeraUrlAsync($"{juego.Titulo} PlayStation Store", x => RegexFichaTienda().IsMatch(x), ct);
        return u is null ? null : $"https://store.playstation.com/{opt.Red.Idioma}/{RegexFichaTienda().Match(u).Groups[1].Value}";
    }

    /// <summary>Paso 2: ficha de la tienda (concepto o producto).</summary>
    private async Task<List<PrecioTienda>> LeerFichaAsync(Juego juego, string url, CancellationToken ct)
    {
        string html;
        try { html = await http.GetStringAsync(url, ct); }
        catch (FuenteNoDisponibleException ex) when (ex.CodigoHttp is 404 or 410) { return new(); }
        using var doc = Html.Parsear(html);

        var ld = Html.JsonLd(doc).FirstOrDefault(e => e.TryGetProperty("offers", out _));
        var hayLd = ld.ValueKind == JsonValueKind.Object;
        var nombre = Html.Texto(doc, "[data-qa='mfe-game-title#name']")
                     ?? (hayLd ? Html.Cadena(ld, "name") : null)
                     ?? Html.Meta(doc, "og:title") ?? "";

        // Quitar "PS4 & PS5" del nombre antes de comparar
        var sim = Texto.Similitud(juego.Titulo, Regex.Replace(nombre, @"\bPS[45]\b|PlayStation\W*[45]|&", " ", RegexOptions.IgnoreCase));
        if (sim < opt.Ofertas.SimilitudMinimaTitulo)
        {
            Log.Debug($"PS Store: '{nombre}' no coincide con '{juego.Titulo}'");
            return new();
        }

        var (actual, regular) = LeerPrecios(doc);
        actual ??= Html.PrecioJsonLd(doc);
        if (actual is null) return new();

        var imagen = (hayLd ? Html.Cadena(ld, "image") : null) ?? Html.Meta(doc, "og:image");

        var plataformas = doc.QuerySelectorAll("[data-qa^='mfe-game-title#productTag']")
            .Select(e => e.TextContent.Trim().ToLowerInvariant())
            .Where(t => t is "ps4" or "ps5")
            .Distinct()
            .ToList();
        if (plataformas.Count == 0)
            plataformas = new[] { "ps5", "ps4" }.Where(p => nombre.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
        if (plataformas.Count == 0)
            plataformas = juego.Plataformas.Where(p => p is "ps4" or "ps5").ToList();

        return plataformas.Where(p => juego.Tiene(p)).Select(p => new PrecioTienda
        {
            Tienda = Nombre,
            Plataforma = p,
            Formato = "digital",
            PrecioActual = actual.Value,
            PrecioRegular = regular is > 0 && regular > actual ? regular.Value : actual.Value,
            Url = url,
            TituloEncontrado = nombre,
            ImagenUrl = imagen,
            Similitud = sim,
        }).ToList();
    }

    /// <summary>
    /// La caja principal de compra puede mostrar varias ofertas: la de PS Plus (más barata, o "Included")
    /// y la pública. El precio que paga cualquiera es la oferta pública = la MAYOR de las que traen precio.
    /// </summary>
    private static (double? actual, double? regular) LeerPrecios(IHtmlDocument doc)
    {
        (double final, double? original)? publica = null;
        for (var i = 0; i < 4; i++)
        {
            var final = Texto.ParsearPrecioUsd(Html.Texto(doc, $"[data-qa='mfeCtaMain#offer{i}#finalPrice']"));
            if (final is null) continue; // "Included", "Free trial", etc.
            var original = Texto.ParsearPrecioUsd(Html.Texto(doc, $"[data-qa='mfeCtaMain#offer{i}#originalPrice']"));
            if (publica is null || final > publica.Value.final) publica = (final.Value, original);
        }
        return publica is { } p ? (p.final, p.original ?? p.final) : (null, null);
    }
}
