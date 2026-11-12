using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PrecioPixel.Config;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Tiendas;

/// <summary>
/// Amazon (copias FÍSICAS para consola). Web scraping del HTML de resultados de búsqueda.
/// Amazon a veces responde con CAPTCHA: el cliente HTTP lo detecta, se marca la fuente como fallida y
/// la web conserva el último precio conocido marcado como "no vigente".
/// </summary>
public sealed class AmazonScraper(ClienteHttpResiliente http, Opciones opt) : ITiendaScraper
{
    private static readonly string[] Consolas = { "switch2", "switch", "ps5", "ps4", "xbox-series", "xbox-one", "3ds" };

    public string Nombre => "Amazon";
    public string UrlBase => "https://www.amazon.com/";

    public bool AplicaA(Juego juego) => juego.Formatos.Contains("fisico") && juego.Tiene(Consolas);

    // Amazon limita a quien hace muchas búsquedas seguidas: máximo 2 a la vez y una pausa corta antes de cada una
    private static readonly SemaphoreSlim Turno = new(2, 2);

    public async Task<List<PrecioTienda>> BuscarAsync(Juego juego, CancellationToken ct)
    {
        var url = $"https://www.amazon.com/s?k={Uri.EscapeDataString(juego.Busqueda)}&i=videogames";
        string html;
        await Turno.WaitAsync(ct);
        try
        {
            await Task.Delay(Random.Shared.Next(300, 900), ct);
            html = await http.GetStringAsync(url, ct, new Dictionary<string, string>
            {
                ["Referer"] = "https://www.amazon.com/",
                // Amazon muestra los precios en la moneda del país desde donde te conectas (colones en Costa Rica).
                // Esta cookie es la misma que guarda el sitio al elegir "USD" en sus preferencias de moneda.
                ["Cookie"] = "i18n-prefs=USD; lc-main=en_US",
            });
        }
        finally { Turno.Release(); }

        using var doc = new HtmlParser().ParseDocument(html);
        var items = doc.QuerySelectorAll("div[data-component-type='s-search-result']").ToList();

        // Página de bloqueo ("Sorry! Something went wrong", aviso de acceso automatizado): es una falla, no "0 resultados".
        // Así el motor conserva los precios anteriores de Amazon marcados como no vigentes.
        if (items.Count == 0 && (html.Contains("api-services-support@amazon.com", StringComparison.OrdinalIgnoreCase) ||
                                 html.Contains("Something went wrong", StringComparison.OrdinalIgnoreCase) ||
                                 html.Contains("validateCaptcha", StringComparison.OrdinalIgnoreCase)))
            throw new BloqueoDetectadoException("www.amazon.com");

        var plataformasJuego = juego.Plataformas.Where(p => Consolas.Contains(p)).ToList();
        var filas = new List<PrecioTienda>();
        var enOtraMoneda = 0;

        foreach (var item in items)
        {
            var titulo = (item.QuerySelector("h2")?.GetAttribute("aria-label") ?? item.QuerySelector("h2")?.TextContent ?? "").Trim();
            if (titulo.StartsWith("Sponsored Ad - ", StringComparison.OrdinalIgnoreCase)) titulo = titulo["Sponsored Ad - ".Length..];
            if (titulo.Length == 0 || NoEsCopiaFisica(titulo)) continue;

            var plataforma = Plataformas.DetectarEnTexto(titulo, plataformasJuego)
                             ?? Plataformas.DetectarEnTexto(item.TextContent, plataformasJuego);
            if (plataforma is null) continue;

            var sim = Texto.Similitud(juego.Titulo, QuitarPlataforma(titulo));
            if (sim < opt.Ofertas.SimilitudMinimaTitulo) continue;

            // Solo dólares: si Amazon ignora la cookie y muestra "CRC 24,332.50", la fila se descarta
            var textoPrecio = item.QuerySelector(".a-price:not(.a-text-price) .a-offscreen")?.TextContent;
            var actual = Texto.ParsearPrecioUsd(textoPrecio);
            if (actual is null && Texto.ParsearPrecio(textoPrecio) is not null) enOtraMoneda++;
            if (actual is null or <= 0) continue;
            var regular = Texto.ParsearPrecioUsd(item.QuerySelector(".a-price.a-text-price .a-offscreen")?.TextContent);

            filas.Add(new PrecioTienda
            {
                Tienda = Nombre,
                Plataforma = plataforma,
                Formato = "fisico",
                PrecioActual = actual.Value,
                PrecioRegular = regular is > 0 && regular > actual ? regular.Value : actual.Value,
                Url = Enlace(item),
                TituloEncontrado = titulo,
                ImagenUrl = ImagenGrande(item.QuerySelector("img.s-image")?.GetAttribute("src")),
                Similitud = sim,
            });
        }

        Log.Debug($"Amazon '{juego.Titulo}': {items.Count} resultados, {filas.Count} útiles, {enOtraMoneda} en otra moneda");
        if (filas.Count == 0 && enOtraMoneda > 0)
            throw new FuenteNoDisponibleException($"Amazon mostró {enOtraMoneda} precios en otra moneda en lugar de USD");

        return filas
            .GroupBy(f => f.Plataforma)
            .Select(g => g.OrderByDescending(f => f.Similitud).ThenBy(f => f.PrecioActual).First())
            .ToList();
    }

    private static string Enlace(IElement item)
    {
        var href = item.QuerySelector("a.s-no-outline")?.GetAttribute("href")
                   ?? item.QuerySelector("h2 a")?.GetAttribute("href")
                   ?? item.QuerySelector("a[href*='/dp/']")?.GetAttribute("href")
                   ?? "";
        if (href.StartsWith("http")) return href;
        // Dejamos solo /dp/ASIN para un enlace limpio
        var dp = href.IndexOf("/dp/", StringComparison.Ordinal);
        if (dp >= 0)
        {
            var asin = new string(href[(dp + 4)..].TakeWhile(char.IsLetterOrDigit).ToArray());
            return $"https://www.amazon.com/dp/{asin}";
        }
        return "https://www.amazon.com" + href;
    }

    /// <summary>Códigos digitales, suscripciones e importaciones (títulos en japonés/chino/coreano) no son la copia física que buscamos.</summary>
    private static bool NoEsCopiaFisica(string titulo) =>
        titulo.Contains("digital code", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("online game code", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("download code", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("[digital", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("membership", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("code in box", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("code in the box", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("code-in-the-box", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("renewed", StringComparison.OrdinalIgnoreCase) ||          // reacondicionado / usado
        titulo.Contains("european version", StringComparison.OrdinalIgnoreCase) ||
        titulo.Contains("pegi", StringComparison.OrdinalIgnoreCase) ||              // importaciones de Europa
        titulo.Contains("hk version", StringComparison.OrdinalIgnoreCase) ||
        titulo.Any(c => c is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or '【' or '】');

    /// <summary>
    /// Las miniaturas de Amazon traen el tamaño en el nombre: 71abc._AC_UY218_.jpg.
    /// Quitando ese modificador se obtiene la portada en tamaño completo: 71abc.jpg
    /// </summary>
    private static string? ImagenGrande(string? src) =>
        src is null ? null : System.Text.RegularExpressions.Regex.Replace(src, @"\._[A-Za-z0-9,_]+_(?=\.(jpg|jpeg|png|webp)$)", "");

    private static string QuitarPlataforma(string titulo)
    {
        var t = " " + Texto.Normalizar(titulo) + " ";
        foreach (var info in Plataformas.Todas.Values)
            foreach (var p in info.PalabrasAmazon.Append(Texto.Normalizar(info.Nombre)))
                t = t.Replace(" " + p + " ", " ");
        foreach (var extra in new[] { " standard edition ", " standard ", " video game ", " physical " })
            t = t.Replace(extra, " ");
        return t.Trim();
    }
}
