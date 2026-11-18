using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Util;

namespace PrecioPixel.Fuentes;

/// <summary>
/// Metacritic: obtiene el Metascore del juego PARA CADA CONSOLA asociada en el repo.
/// Las consultas de las distintas consolas de un mismo juego también se hacen en paralelo (si el nivel 3 está activo).
///   https://www.metacritic.com/game/{slug}/critic-reviews/?platform={plataforma}
/// De la página principal del juego también se toman la portada y la fecha de lanzamiento (JSON-LD datePublished).
/// </summary>
public sealed partial class MetacriticScraper(ClienteHttpResiliente http, BuscadorWeb buscador)
{
    public string UrlBase => "https://www.metacritic.com/";

    [GeneratedRegex(@"title=""Metascore (\d{1,3}) out of 100""", RegexOptions.IgnoreCase)]
    private static partial Regex RegexTitulo();

    /// <param name="consolasEnParalelo">
    /// true: las páginas de cada consola se piden a la vez (Task.WhenAll). false: una por una, para que el modo
    /// secuencial del análisis de rendimiento sea secuencial de verdad. Lo decide el nivel 3 del motor.
    /// </param>
    public async Task<MetacriticInfo> ObtenerAsync(Juego juego, bool consolasEnParalelo, CancellationToken ct)
    {
        // 1) Slug fijado en el repo (campo metacriticSlug o urlsTienda.metacritic)
        string? fijo = juego.Url("metacritic") is { } u && RegexSlug().Match(u) is { Success: true } m ? m.Groups[1].Value
                     : string.IsNullOrWhiteSpace(juego.MetacriticSlug) ? null : juego.MetacriticSlug;
        if (fijo is not null) return await ConSlugAsync(juego, fijo, consolasEnParalelo, ct) ?? new MetacriticInfo();

        // 2) Variantes del título: "alan-wake-2" no existe pero "alan-wake-ii" sí
        foreach (var slug in Texto.VariantesSlug(juego.Titulo, juego.Busqueda).Take(5))
            if (await ConSlugAsync(juego, slug, consolasEnParalelo, ct) is { } info)
                return info;

        // 3) Último recurso: buscador web
        var encontrada = await buscador.PrimeraUrlAsync($"metacritic {juego.Titulo} game", x => RegexSlug().IsMatch(x), ct);
        return encontrada is null ? new MetacriticInfo()
            : await ConSlugAsync(juego, RegexSlug().Match(encontrada).Groups[1].Value, consolasEnParalelo, ct) ?? new MetacriticInfo();
    }

    [GeneratedRegex(@"metacritic\.com/game/([a-z0-9\-]+)")]
    private static partial Regex RegexSlug();

    /// <summary>null si la página del juego no existe con ese slug (404).</summary>
    private async Task<MetacriticInfo?> ConSlugAsync(Juego juego, string slug, bool consolasEnParalelo, CancellationToken ct)
    {
        var urlJuego = $"https://www.metacritic.com/game/{slug}/";

        // Primero la página principal: confirma que el slug existe y da el score general y la portada
        var general = await LeerPaginaAsync(urlJuego, ct);
        if (!general.Existe) return null;

        var info = new MetacriticInfo
        {
            Url = urlJuego,
            Fuente = "Metacritic",
            ImagenUrl = general.Portada,
            FechaLanzamiento = Fechas.Normalizar(general.Fecha),
        };

        // Una página por consola: a la vez (Task.WhenAll) o una por una
        var consolas = juego.Plataformas
            .Where(p => Plataformas.Todas.TryGetValue(p, out var i) && i.MetacriticSlug is not null)
            .ToList();
        async Task<(string plataforma, Pagina pagina)> LeerConsolaAsync(string p) =>
            (p, await LeerPaginaAsync($"{urlJuego}critic-reviews/?platform={Plataformas.Todas[p].MetacriticSlug}", ct));

        var paginas = new List<(string plataforma, Pagina pagina)>();
        if (consolasEnParalelo)
            paginas.AddRange(await Task.WhenAll(consolas.Select(LeerConsolaAsync)));
        else
            foreach (var p in consolas)
                paginas.Add(await LeerConsolaAsync(p));

        foreach (var (plataforma, pagina) in paginas)
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

    private sealed record Pagina(bool Existe, int? Score, string? Portada, string? Fecha = null);

    /// <summary>
    /// Lee de una página de Metacritic el Metascore, la portada y la fecha de lanzamiento del juego.
    /// Score null si no existe (404) o aún no tiene.
    /// </summary>
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
        string? portada = null, fecha = null;

        // JSON-LD (schema.org VideoGame): aggregateRating.ratingValue, image (la portada) y datePublished ("2020-09-17")
        foreach (var s in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var j = JsonDocument.Parse(s.TextContent);
                var raiz = j.RootElement;
                if (raiz.ValueKind != JsonValueKind.Object) continue;
                fecha ??= Html.Cadena(raiz, "datePublished", "releaseDate");
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

        // El Metascore de la consola: <div class="c-siteReviewScore" title="Metascore 90 out of 100">.
        // Cada reseña individual usa el MISMO elemento, dentro de una "review-card": esos se ignoran,
        // porque si la consola aún no tiene Metascore, el primero sería la nota de un solo crítico (a veces 100).
        if (score is null)
        {
            var resumen = doc.QuerySelectorAll("[title^='Metascore']")
                .FirstOrDefault(e => e.Closest("[data-testid*='review-card'], [class*='review-card']") is null);
            if (resumen is not null && RegexTitulo().Match(resumen.OuterHtml) is { Success: true } m1)
                score = int.Parse(m1.Groups[1].Value);
        }

        portada ??= Html.Meta(doc, "og:image");
        return new Pagina(true, score, portada, fecha);
    }
}
