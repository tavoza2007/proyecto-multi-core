namespace PrecioPixel.Util;

/// <summary>Catálogo de plataformas soportadas y sus equivalencias en cada sitio.</summary>
public static class Plataformas
{
    public sealed record Info(string Codigo, string Nombre, string? MetacriticSlug, string[] PalabrasAmazon);

    public static readonly IReadOnlyDictionary<string, Info> Todas = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase)
    {
        ["switch2"] = new("switch2", "Nintendo Switch 2", "nintendo-switch-2", new[] { "switch 2" }),
        ["switch"] = new("switch", "Nintendo Switch", "nintendo-switch", new[] { "nintendo switch", "switch" }),
        ["ps5"] = new("ps5", "PlayStation 5", "playstation-5", new[] { "playstation 5", "ps5" }),
        ["ps4"] = new("ps4", "PlayStation 4", "playstation-4", new[] { "playstation 4", "ps4" }),
        ["xbox-series"] = new("xbox-series", "Xbox Series X|S", "xbox-series-x", new[] { "xbox series x", "xbox series" }),
        ["xbox-one"] = new("xbox-one", "Xbox One", "xbox-one", new[] { "xbox one" }),
        ["pc"] = new("pc", "PC", "pc", new[] { "pc", "windows" }),
        ["3ds"] = new("3ds", "Nintendo 3DS", "3ds", new[] { "3ds" }),
    };

    private static readonly string[] OrdenDeteccion = { "switch2", "switch", "ps5", "ps4", "xbox-series", "xbox-one", "3ds", "pc" };

    public static string Nombre(string codigo) => Todas.TryGetValue(codigo, out var i) ? i.Nombre : codigo;

    /// <summary>Detecta la plataforma en el título de un producto de Amazon. Respeta el orden (Switch 2 antes que Switch).</summary>
    public static string? DetectarEnTexto(string texto, IEnumerable<string> candidatas)
    {
        var t = " " + Texto.Normalizar(texto) + " ";
        foreach (var codigo in OrdenDeteccion) // más específicas primero
        {
            if (!Todas[codigo].PalabrasAmazon.Any(palabra => t.Contains(" " + palabra + " "))) continue;
            // La primera plataforma que aparece en el texto es la del producto. Si no es una del juego
            // (ej. "Nintendo Switch 2 Edition" cuando el repo solo tiene Switch), el producto no sirve:
            // no se sigue buscando, para no confundir "Switch 2" con "Switch".
            return candidatas.Contains(codigo, StringComparer.OrdinalIgnoreCase) ? codigo : null;
        }
        return null;
    }
}
