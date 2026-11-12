using System.Text.Json;
using AngleSharp.Html.Dom;
using PrecioPixel.Config;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Tiendas;

/// <summary>
/// Nintendo eShop (Switch / Switch 2) — web scraping de la FICHA del juego en nintendo.com:
///   https://www.nintendo.com/us/store/products/{slug}-switch/      (Switch)
///   https://www.nintendo.com/us/store/products/{slug}-switch-2/    (Switch 2)
///
/// Qué trae la página (verificado en nintendo.com):
///   • JSON-LD (schema.org Product) del producto principal: name, sku, offers.price, image.
///   • Estado incrustado (__NEXT_DATA__ → initialApolloState) con TODOS los productos que muestra la ficha:
///       "Product:{"sku":"7100027619"}" → { name, platform.code, releaseDate, prices(...) { regularPrice, finalPrice, discounted } }
///     Ahí también aparecen la edición de Switch 2, los DLC y los paquetes, por eso se busca el producto por su SKU.
/// </summary>
public sealed class NintendoEshopScraper(ClienteHttpResiliente http, BuscadorWeb buscador, Opciones opt) : ITiendaScraper
{
    private const string Fichas = "https://www.nintendo.com/us/store/products/";

    public string Nombre => "Nintendo eShop";
    public string UrlBase => "https://www.nintendo.com/";

    public bool AplicaA(Juego juego) => juego.Tiene("switch", "switch2");

    public async Task<List<PrecioTienda>> BuscarAsync(Juego juego, CancellationToken ct)
    {
        var filas = new List<PrecioTienda>();

        if (juego.Tiene("switch"))
        {
            var pagina = await FichaAsync(juego, "switch", ct);
            if (pagina is not null)
            {
                if (Fila(juego, pagina, pagina.Principal, "switch") is { } f) filas.Add(f);

                // La ficha de Switch suele traer también la "Nintendo Switch 2 Edition" del mismo juego
                if (juego.Tiene("switch2") && pagina.EdicionSwitch2 is { } e2 && Fila(juego, pagina, e2, "switch2") is { } f2)
                    filas.Add(f2);
            }
        }

        if (juego.Tiene("switch2") && filas.All(f => f.Plataforma != "switch2"))
        {
            var pagina = await FichaAsync(juego, "switch2", ct);
            if (pagina is not null && Fila(juego, pagina, pagina.Principal, "switch2") is { } f) filas.Add(f);
        }

        return filas;
    }

    private sealed record Producto(string Nombre, double? Final, double? Regular, string? Fecha = null);
    private sealed record Pagina(string Url, string? Imagen, Producto Principal, Producto? EdicionSwitch2);

    private PrecioTienda? Fila(Juego juego, Pagina pagina, Producto p, string plataforma)
    {
        if (p.Final is null) return null;
        var nombreSinEdicion = p.Nombre.Replace("Nintendo Switch™ 2 Edition", "").Replace("Nintendo Switch 2 Edition", "").Trim(' ', '–', '-');
        var sim = Texto.Similitud(juego.Titulo, nombreSinEdicion);
        if (sim < opt.Ofertas.SimilitudMinimaTitulo) return null;

        return new PrecioTienda
        {
            Tienda = Nombre,
            Plataforma = plataforma,
            Formato = "digital",
            PrecioActual = p.Final.Value,
            PrecioRegular = p.Regular is > 0 && p.Regular > p.Final ? p.Regular.Value : p.Final.Value,
            Url = pagina.Url,
            TituloEncontrado = p.Nombre,
            ImagenUrl = pagina.Imagen,
            Similitud = sim,
            FechaLanzamiento = p.Fecha, // "2020-09-17T00:00:00.000Z"
        };
    }

    /// <summary>Descarga la ficha (deducida del título, fijada en el repo o encontrada con el buscador) y la interpreta.</summary>
    private async Task<Pagina?> FichaAsync(Juego juego, string plataforma, CancellationToken ct)
    {
        var (sufijo, clave) = plataforma == "switch2" ? ("switch-2", "nintendo-switch2") : ("switch", "nintendo");
        if (juego.Url(clave) is { } fijada && await LeerAsync(fijada, ct) is { } desdeRepo) return desdeRepo;

        string? url = null;
        foreach (var slug in Texto.VariantesSlug(juego.Titulo, juego.Busqueda).Take(4))
        {
            url = $"{Fichas}{slug}-{sufijo}/";
            if (await LeerAsync(url, ct) is { } pagina) return pagina;
        }

        // La dirección deducida no existe → buscar la ficha
        var encontrada = await buscador.PrimeraUrlAsync(
            $"nintendo.com store {juego.Titulo} {(plataforma == "switch2" ? "Nintendo Switch 2" : "Nintendo Switch")}",
            u => u.Contains("nintendo.com/us/store/products/") && u.TrimEnd('/').EndsWith("-" + sufijo), ct);
        return encontrada is null || encontrada == url ? null : await LeerAsync(encontrada, ct);
    }

    private async Task<Pagina?> LeerAsync(string url, CancellationToken ct)
    {
        string html;
        try { html = await http.GetStringAsync(url, ct); }
        catch (FuenteNoDisponibleException ex) when (ex.CodigoHttp is 404 or 410) { return null; }

        using var doc = Html.Parsear(html);

        // 1) Producto principal según el JSON-LD
        string? sku = null, nombre = null, imagen = null;
        double? precioLd = null;
        foreach (var e in Html.JsonLd(doc))
        {
            if (!e.TryGetProperty("offers", out _) || !e.TryGetProperty("sku", out _)) continue;
            sku = Html.Cadena(e, "sku");
            nombre = Html.Cadena(e, "name");
            imagen = Html.Cadena(e, "image");
            precioLd = Html.PrecioJsonLd(doc);
            break;
        }
        nombre ??= Html.Texto(doc, "h1") ?? Html.Meta(doc, "og:title") ?? "";
        imagen ??= Html.Meta(doc, "og:image");

        // 2) Precio regular / final de ese mismo producto (y la edición de Switch 2) en el estado incrustado
        var productos = ProductosIncrustados(doc);
        var principal = sku is not null && productos.TryGetValue(sku, out var info)
            ? new Producto(info.nombre ?? nombre, info.final ?? precioLd, info.regular ?? precioLd, info.fecha)
            : new Producto(nombre, precioLd, precioLd);

        var edicion2 = productos.Values
            .Where(p => p.plataforma == "NINTENDO_SWITCH_2" && p.nombre is not null && p.sku.StartsWith("7100") &&
                        p.nombre.Contains("2 Edition", StringComparison.OrdinalIgnoreCase) &&
                        !p.nombre.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
            .Select(p => new Producto(p.nombre!, p.final, p.regular, p.fecha))
            .FirstOrDefault();

        return new Pagina(url, imagen, principal, PrincipalEsSwitch2(productos, sku) ? null : edicion2);
    }

    private static bool PrincipalEsSwitch2(Dictionary<string, (string sku, string? nombre, string? plataforma, double? final, double? regular, string? fecha)> productos, string? sku) =>
        sku is not null && productos.TryGetValue(sku, out var p) && p.plataforma == "NINTENDO_SWITCH_2";

    /// <summary>
    /// Lee de __NEXT_DATA__ los objetos "Product:{"sku":"…"}" con su nombre, plataforma, precios y fecha de lanzamiento.
    /// Los SKU digitales de juegos empiezan con 7100 (los de 7500 son DLC y los de 7700 paquetes).
    /// </summary>
    private static Dictionary<string, (string sku, string? nombre, string? plataforma, double? final, double? regular, string? fecha)> ProductosIncrustados(IHtmlDocument doc)
    {
        var salida = new Dictionary<string, (string, string?, string?, double?, double?, string?)>();
        if (Html.NextData(doc) is not { } estado) return salida;

        foreach (var o in Html.Objetos(estado))
            foreach (var prop in o.EnumerateObject())
            {
                if (!prop.Name.StartsWith("Product:") || prop.Value.ValueKind != JsonValueKind.Object) continue;
                var producto = prop.Value;
                var sku = Html.Cadena(producto, "sku")
                          ?? System.Text.RegularExpressions.Regex.Match(prop.Name, @"""sku"":""([^""]+)""").Groups[1].Value;
                if (string.IsNullOrEmpty(sku) || salida.ContainsKey(sku)) continue;

                double? final = null, regular = null;
                foreach (var campo in producto.EnumerateObject())
                    if (campo.Name.StartsWith("prices") && campo.Value.ValueKind == JsonValueKind.Object)
                    {
                        final = Html.Numero(campo.Value, "finalPrice");
                        regular = Html.Numero(campo.Value, "regularPrice");
                        break;
                    }

                var plataforma = producto.TryGetProperty("platform", out var pl) && pl.ValueKind == JsonValueKind.Object
                    ? Html.Cadena(pl, "code") : null;
                salida[sku] = (sku, Html.Cadena(producto, "name"), plataforma, final, regular, Html.Cadena(producto, "releaseDate"));
            }
        return salida;
    }
}
