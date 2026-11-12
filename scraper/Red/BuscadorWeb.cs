using System.Web;
using PrecioPixel.Util;

namespace PrecioPixel.Red;

/// <summary>
/// Encuentra la URL de la página de un juego en un sitio cuando no se puede deducir (ej. HowLongToBeat usa ids numéricos).
/// Hace web scraping de la página de resultados de un buscador (Bing y, si falla, la versión HTML de DuckDuckGo),
/// igual que lo haría una persona: buscar "sitio + título" y abrir el primer resultado que coincida.
/// </summary>
public sealed class BuscadorWeb(ClienteHttpResiliente http)
{
    public async Task<string?> PrimeraUrlAsync(string consulta, Func<string, bool> acepta, CancellationToken ct)
    {
        foreach (var buscar in new Func<string, CancellationToken, Task<List<string>>>[] { BingAsync, DuckDuckGoAsync })
        {
            try
            {
                var url = (await buscar(consulta, ct)).FirstOrDefault(acepta);
                if (url is not null) return url;
            }
            catch (Exception ex) when (ex is FuenteNoDisponibleException or BloqueoDetectadoException)
            {
                Log.Debug($"Buscador: {ex.Message}");
            }
        }
        return null;
    }

    private async Task<List<string>> DuckDuckGoAsync(string consulta, CancellationToken ct)
    {
        var html = await http.GetStringAsync($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(consulta)}", ct);
        using var doc = Html.Parsear(html);
        return doc.QuerySelectorAll("a.result__a")
            .Select(a => a.GetAttribute("href") ?? "")
            .Select(Desenvolver)
            .Where(u => u.StartsWith("http"))
            .ToList();
    }

    private async Task<List<string>> BingAsync(string consulta, CancellationToken ct)
    {
        var html = await http.GetStringAsync($"https://www.bing.com/search?q={Uri.EscapeDataString(consulta)}&setlang=en&cc=us", ct);
        using var doc = Html.Parsear(html);
        var urls = doc.QuerySelectorAll("li.b_algo h2 a")
            .Select(a => DesenvolverBing(a.GetAttribute("href") ?? ""))
            .Where(u => u.StartsWith("http"))
            .ToList();
        Log.Debug($"Bing: {urls.Count} resultados para \"{consulta}\"");
        return urls;
    }

    /// <summary>
    /// Bing envuelve los enlaces: bing.com/ck/a?…&amp;u=a1aHR0cHM6Ly9… → "a1" + la URL real en base64.
    /// </summary>
    private static string DesenvolverBing(string href)
    {
        if (!href.Contains("bing.com/ck/a")) return href;
        var u = HttpUtility.ParseQueryString(new Uri(href).Query)["u"];
        if (u is null || !u.StartsWith("a1")) return href;
        try
        {
            var b64 = u[2..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException) { return href; }
    }

    /// <summary>DuckDuckGo envuelve los enlaces: //duckduckgo.com/l/?uddg=https%3A%2F%2F… → la URL real.</summary>
    private static string Desenvolver(string href)
    {
        if (href.StartsWith("//")) href = "https:" + href;
        if (!href.Contains("uddg=")) return href;
        var q = new Uri(href).Query;
        return HttpUtility.ParseQueryString(q)["uddg"] ?? href;
    }
}
