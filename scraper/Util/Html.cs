using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace PrecioPixel.Util;

/// <summary>
/// Utilidades de web scraping sobre el HTML descargado (AngleSharp).
/// Todo lo que se lee sale de la página tal como la ve un navegador: selectores CSS,
/// etiquetas meta, datos estructurados schema.org (JSON-LD) y el estado incrustado de la página.
/// </summary>
public static class Html
{
    /// <summary>HtmlParser no es thread-safe: se crea uno por documento (se llama desde muchos hilos).</summary>
    public static IHtmlDocument Parsear(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>Texto del primer selector que exista y no esté vacío.</summary>
    public static string? Texto(IParentNode nodo, params string[] selectores)
    {
        foreach (var s in selectores)
        {
            var t = nodo.QuerySelector(s)?.TextContent?.Trim();
            if (!string.IsNullOrEmpty(t)) return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
        }
        return null;
    }

    public static string? Atributo(IParentNode nodo, string selector, string atributo)
    {
        var v = nodo.QuerySelector(selector)?.GetAttribute(atributo)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>&lt;meta property="og:image" content="…"&gt; o &lt;meta name="…"&gt;</summary>
    public static string? Meta(IDocument doc, string nombre) =>
        Atributo(doc, $"meta[property='{nombre}']", "content") ?? Atributo(doc, $"meta[name='{nombre}']", "content");

    /// <summary>Bloques schema.org JSON-LD de la página (los que Google usa para mostrar precio y estrellas).</summary>
    public static List<JsonElement> JsonLd(IDocument doc)
    {
        var salida = new List<JsonElement>();
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var j = JsonDocument.Parse(script.TextContent);
                Aplanar(j.RootElement, salida);
            }
            catch (JsonException) { }
        }
        return salida;

        static void Aplanar(JsonElement e, List<JsonElement> salida)
        {
            if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) Aplanar(x, salida);
            else if (e.ValueKind == JsonValueKind.Object)
            {
                salida.Add(e.Clone());
                if (e.TryGetProperty("@graph", out var g)) Aplanar(g, salida);
            }
        }
    }

    /// <summary>Precio publicado en JSON-LD: offers.price / offers.lowPrice.</summary>
    public static double? PrecioJsonLd(IDocument doc)
    {
        foreach (var e in JsonLd(doc))
        {
            if (!e.TryGetProperty("offers", out var offers)) continue;
            var lista = offers.ValueKind == JsonValueKind.Array ? offers.EnumerateArray().ToList() : new List<JsonElement> { offers };
            foreach (var o in lista)
                foreach (var campo in new[] { "price", "lowPrice" })
                    if (o.TryGetProperty(campo, out var p))
                    {
                        var v = p.ValueKind == JsonValueKind.Number ? p.GetDouble() : Util.Texto.ParsearPrecio(p.GetString());
                        if (v is not null) return v;
                    }
        }
        return null;
    }

    /// <summary>Estado que las páginas hechas con Next.js incrustan en &lt;script id="__NEXT_DATA__"&gt;.</summary>
    public static JsonElement? NextData(IDocument doc)
    {
        var s = doc.QuerySelector("script#__NEXT_DATA__")?.TextContent;
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            using var j = JsonDocument.Parse(s);
            return j.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Recorre todos los objetos de un árbol JSON (búsqueda en profundidad).</summary>
    public static IEnumerable<JsonElement> Objetos(JsonElement e)
    {
        var pila = new Stack<JsonElement>();
        pila.Push(e);
        while (pila.Count > 0)
        {
            var x = pila.Pop();
            if (x.ValueKind == JsonValueKind.Object)
            {
                yield return x;
                foreach (var p in x.EnumerateObject()) pila.Push(p.Value);
            }
            else if (x.ValueKind == JsonValueKind.Array)
            {
                foreach (var y in x.EnumerateArray()) pila.Push(y);
            }
        }
    }

    public static double? Numero(JsonElement obj, params string[] props)
    {
        foreach (var p in props)
            if (obj.TryGetProperty(p, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (v.ValueKind == JsonValueKind.String && Util.Texto.ParsearPrecio(v.GetString()) is { } d) return d;
            }
        return null;
    }

    public static string? Cadena(JsonElement obj, params string[] props)
    {
        foreach (var p in props)
            if (obj.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    /// <summary>Convierte un href relativo en absoluto.</summary>
    public static string Absoluta(string? href, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href)) return baseUrl;
        return Uri.TryCreate(new Uri(baseUrl), href, out var u) ? u.ToString() : href;
    }
}
