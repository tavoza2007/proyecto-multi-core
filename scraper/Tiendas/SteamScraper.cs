using AngleSharp.Dom;
using PrecioPixel.Config;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Tiendas;

/// <summary>
/// Steam (PC) — web scraping del HTML de la tienda:
///   1) Página de búsqueda   store.steampowered.com/search/?term=…   → filas a.search_result_row
///   2) Página del juego     store.steampowered.com/app/{id}/         → precio, precio tachado, Metascore, fecha, imagen
/// Se envían las cookies de edad que pone el navegador para saltar la verificación de edad (juegos +18).
/// </summary>
public sealed class SteamScraper(ClienteHttpResiliente http, Opciones opt) : ITiendaScraper
{
    private static readonly Dictionary<string, string> Cookies = new()
    {
        ["Cookie"] = "birthtime=0; lastagecheckage=1-0-1990; wants_mature_content=1; Steam_Language=english",
    };

    public string Nombre => "Steam";
    public string UrlBase => "https://store.steampowered.com/";

    public bool AplicaA(Juego juego) => juego.Tiene("pc");

    public async Task<List<PrecioTienda>> BuscarAsync(Juego juego, CancellationToken ct)
    {
        var urlJuego = juego.Url("steam")
                       ?? (juego.SteamAppId is long id ? $"https://store.steampowered.com/app/{id}/" : null)
                       ?? await BuscarEnListadoAsync(juego, ct);
        if (urlJuego is null) return new();

        var fila = await LeerPaginaJuegoAsync(juego, urlJuego, ct);
        return Filas.De(fila);
    }

    /// <summary>Scraping de la página de resultados de búsqueda.</summary>
    private async Task<string?> BuscarEnListadoAsync(Juego juego, CancellationToken ct)
    {
        // category1=998 → solo juegos (sin DLC, bandas sonoras, etc.)
        var url = $"https://store.steampowered.com/search/?term={Uri.EscapeDataString(juego.Busqueda)}&category1=998&cc={opt.Red.Pais}&l=english";
        using var doc = Html.Parsear(await http.GetStringAsync(url, ct, Cookies));

        var mejor = doc.QuerySelectorAll("a.search_result_row")
            .Select(fila => new
            {
                titulo = Html.Texto(fila, "span.title") ?? "",
                appId = (fila.GetAttribute("data-ds-appid") ?? "").Split(',')[0],
                href = fila.GetAttribute("href") ?? "",
            })
            .Where(f => f.appId.Length > 0)
            .Select(f => new { f.titulo, f.appId, sim = Texto.Similitud(juego.Titulo, f.titulo) })
            .OrderByDescending(f => f.sim)
            .FirstOrDefault();

        return mejor is null || mejor.sim < opt.Ofertas.SimilitudMinimaTitulo
            ? null
            : $"https://store.steampowered.com/app/{mejor.appId}/";
    }

    /// <summary>Scraping de la ficha del juego.</summary>
    private async Task<PrecioTienda?> LeerPaginaJuegoAsync(Juego juego, string urlJuego, CancellationToken ct)
    {
        var separador = urlJuego.Contains('?') ? "&" : "?";
        var html = await http.GetStringAsync($"{urlJuego}{separador}cc={opt.Red.Pais}&l=english", ct, Cookies);
        using var doc = Html.Parsear(html);

        if (doc.QuerySelector("#app_agegate, .agegate_birthday_selector, form[action*='agecheck']") is not null ||
            (doc.QuerySelector("#appHubAppName") is null && html.Contains("agecheck", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Debug($"Steam: verificación de edad en {urlJuego}");
            return null;
        }

        var nombre = Html.Texto(doc, "#appHubAppName", ".apphub_AppName") ?? Html.Meta(doc, "og:title") ?? "";
        var sim = Texto.Similitud(juego.Titulo, nombre);
        if (sim < opt.Ofertas.SimilitudMinimaTitulo)
        {
            Log.Debug($"Steam: '{nombre}' no coincide con '{juego.Titulo}'");
            return null;
        }

        // La caja de compra puede traer varios bloques: demo, "Friend's Pass", el juego, ediciones y paquetes.
        // Se elige el bloque "Buy <juego>" cuyo nombre más se parece al del repo y que tenga precio.
        double? actual = null, regular = null;
        var bloques = doc.QuerySelectorAll("#game_area_purchase .game_area_purchase_game")
            .Where(b => !b.ClassList.Contains("dynamic_bundle_description") && !b.ClassList.Contains("demo_above_purchase"))
            .Select(b => (bloque: b, titulo: Html.Texto(b, "h1", "h2") ?? ""))
            .Where(x => !EsAccesorio(x.titulo))
            .OrderByDescending(x => x.titulo.StartsWith("Buy ", StringComparison.OrdinalIgnoreCase) ||
                                    x.titulo.StartsWith("Pre-Purchase ", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => Texto.Similitud(juego.Titulo, QuitarVerbo(x.titulo)));
        foreach (var (bloque, _) in bloques)
        {
            (actual, regular) = LeerPrecios(bloque);
            if (actual is not null) break;
        }
        if (actual is null) return null; // no está a la venta (o aún no sale)

        var metascore = int.TryParse(Html.Texto(doc, "#game_area_metascore .score"), out var ms) ? ms : (int?)null;

        return new PrecioTienda
        {
            Tienda = Nombre,
            Plataforma = "pc",
            Formato = "digital",
            PrecioActual = actual.Value,
            PrecioRegular = regular ?? actual.Value,
            Url = urlJuego,
            TituloEncontrado = nombre,
            ImagenUrl = Html.Atributo(doc, "img.game_header_image_full", "src") ?? Html.Meta(doc, "og:image"),
            Similitud = sim,
            MetacriticTienda = metascore,
            FechaLanzamiento = Html.Texto(doc, ".release_date .date"),
        };
    }

    private static readonly string[] Accesorios =
        { "demo", "friend's pass", "friends pass", "playtest", "character creator", "soundtrack", "season pass", "bundle", "upgrade" };

    private static bool EsAccesorio(string tituloBloque) =>
        Accesorios.Any(a => tituloBloque.Contains(a, StringComparison.OrdinalIgnoreCase));

    private static string QuitarVerbo(string tituloBloque) =>
        System.Text.RegularExpressions.Regex.Replace(tituloBloque, @"^(Buy|Pre-Purchase|Play|Download)\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static (double? actual, double? regular) LeerPrecios(IElement compra)
    {
        // Con descuento: <div class="discount_original_price">$24.99</div><div class="discount_final_price">$12.49</div>
        var final = Texto.ParsearPrecioUsd(Html.Texto(compra, ".discount_final_price"));
        if (final is not null)
            return (final, Texto.ParsearPrecioUsd(Html.Texto(compra, ".discount_original_price")));

        // Sin descuento: <div class="game_purchase_price price">$24.99</div>  ("Free to Play" → 0)
        var precio = Texto.ParsearPrecioUsd(Html.Texto(compra, ".game_purchase_price"));
        return (precio, precio);
    }
}
