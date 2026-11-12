using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Fuentes;

/// <summary>
/// Metacritic: obtiene el Metascore del juego PARA CADA CONSOLA asociada en el repo.
/// Las consultas de las distintas consolas de un mismo juego también se hacen en paralelo.
///   https://www.metacritic.com/game/{slug}/critic-reviews/?platform={plataforma}
/// </summary>
public sealed partial class MetacriticScraper(ClienteHttpResiliente http, BuscadorWeb buscador)
{
    public string UrlBase => "https://www.metacritic.com/";

    [GeneratedRegex(@"title=""Metascore (\d{1,3}) out of 100""", RegexOptions.IgnoreCase)]
    private static partial Regex RegexTitulo();

    [GeneratedRegex(@"""criticScoreSummary""\s*:\s*\{[^{}]*?""score""\s*:\s*(\d{1,3})")]
    private static partial Regex RegexPayload();

    public async Task<MetacriticInfo> ObtenerAsync(Juego juego, CancellationToken ct)
    {
        // 1) Slug fijado en el repo (campo metacriticSlug o urlsTienda.metacritic)
        string? fijo = juego.Url("metacritic") is { } u && RegexSlug().Match(u) is { Success: true } m ? m.Groups[1].Value
                     : string.IsNullOrWhiteSpace(juego.MetacriticSlug) ? null : juego.MetacriticSlug;
        if (fijo is not null) return await ConSlugAsync(juego, fijo, ct) ?? new MetacriticInfo();

        // 2) Variantes del título: "alan-wake-2" no existe pero "alan-wake-ii" sí
        foreach (var slug in Texto.VariantesSlug(juego.Titulo, juego.Busqueda).Take(5))
            if (await ConSlugAsync(juego, slug, ct) is { } info)
                return info;

        // 3) Último recurso: buscador web
        var encontrada = await buscador.PrimeraUrlAsync($"metacritic {juego.Titulo} game", x => RegexSlug().IsMatch(x), ct);
        return encontrada is null ? new MetacriticInfo()
            : await ConSlugAsync(juego, RegexSlug().Match(encontrada).Groups[1].Value, ct) ?? new MetacriticInfo();
    }

    [GeneratedRegex(@"metacritic\.com/game/([a-z0-9\-]+)")]
    private static partial Regex RegexSlug();

    /// <summary>null si la página del juego no existe con ese slug (404).</summary>
    private async Task<MetacriticInfo?> ConSlugAsync(Juego juego, string slug, CancellationToken ct)
    {
        var urlJuego = $"https://www.metacritic.com/game/{slug}/";

        // Primero la página principal: confirma que el slug existe y da el score general y la portada
        var general = await LeerPaginaAsync(urlJuego, ct);
        if (!general.Existe) return null;

        var info = new MetacriticInfo { Url = urlJuego, Fuente = "Metacritic", ImagenUrl = general.Portada };

        // Una tarea por consola → Task.WhenAll
        var tareas = juego.Plataformas
            .Where(p => Plataformas.Todas.TryGetValue(p, out var i) && i.MetacriticSlug is not null)
            .Select(async p =>
            {
                var mcPlat = Plataformas.Todas[p].MetacriticSlug!;
                var pagina = await LeerPaginaAsync($"{urlJuego}critic-reviews/?platform={mcPlat}", ct);
                return (plataforma: p, pagina);
            })
            .ToList();

        foreach (var (plataforma, pagina) in await Task.WhenAll(tareas))
        {
            if (pagina.Score is not null) info.PorPlataforma[plataforma] = pagina.Score.Value;
            info.ImagenUrl ??= pagina.Portada;
        }

        // Respaldo: si ninguna consola dio score, se usa el general de la página principal
        if (info.PorPlataforma.Count == 0 && general.Score is not null && juego.Plataformas.Count > 0)
            info.PorPlataforma[juego.Plataformas[0]] = general.Score.Value;

        info.Score = info.PorPlataforma.Count > 0 ? info.PorPlataforma.Values.Max() : null;
        return info;
    }

    private sealed record Pagina(bool Existe, int? Score, string? Portada);

    /// <summary>Lee de una página de Metacritic el Metascore y la portada del juego. Score null si no existe (404) o aún no tiene.</summary>
    private async Task<Pagina> LeerPaginaAsync(string url, CancellationToken ct)
    {
        string html;
        try
        {
            html = await http.GetStringAsync(url, ct);
        }
        catch (FuenteNoDisponibleException ex) when (ex.CodigoHttp is 404 or 410)
        {
            return new Pagina(false, null, null);
        }

        using var doc = new HtmlParser().ParseDocument(html);
        int? score = null;
        string? portada = null;

        // JSON-LD (schema.org VideoGame): aggregateRating.ratingValue e image (la portada)
        foreach (var s in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var j = JsonDocument.Parse(s.TextContent);
                var raiz = j.RootElement;
                if (raiz.ValueKind != JsonValueKind.Object) continue;
                if (score is null && raiz.TryGetProperty("aggregateRating", out var ar) && ar.TryGetProperty("ratingValue", out var rv))
                    score = rv.ValueKind == JsonValueKind.Number ? rv.GetInt32() : int.Parse(rv.GetString()!);
                if (portada is null && raiz.TryGetProperty("image", out var img))
                    portada = img.ValueKind == JsonValueKind.String ? img.GetString()
                            : img.ValueKind == JsonValueKind.Object ? Html.Cadena(img, "url", "contentUrl")
                            : img.ValueKind == JsonValueKind.Array && img.GetArrayLength() > 0 && img[0].ValueKind == JsonValueKind.String ? img[0].GetString()
                            : null;
            }
            catch (Exception e) when (e is JsonException or FormatException) { }
        }

        // Respaldos del score: atributo title="Metascore 97 out of 100" y estado incrustado de la página
        if (score is null && RegexTitulo().Match(html) is { Success: true } m1) score = int.Parse(m1.Groups[1].Value);
        if (score is null && RegexPayload().Match(html) is { Success: true } m2) score = int.Parse(m2.Groups[1].Value);

        portada ??= Html.Meta(doc, "og:image");
        return new Pagina(true, score, portada);
    }
}
