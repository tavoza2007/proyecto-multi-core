using PrecioPixel.Modelos;

namespace PrecioPixel.Tiendas;

/// <summary>
/// Un sitio web de venta de juegos. Cada implementación sabe buscar un juego y devolver sus precios.
/// Agregar una tienda nueva = crear una clase que implemente esta interfaz y registrarla en Program.cs.
/// </summary>
public interface ITiendaScraper
{
    string Nombre { get; }

    /// <summary>Raíz del sitio, para el chequeo de conectividad.</summary>
    string UrlBase { get; }

    /// <summary>¿Esta tienda vende el juego en alguna de las plataformas del repo?</summary>
    bool AplicaA(Juego juego);

    /// <summary>Puede devolver varias filas (ej. versión PS4 y PS5, o físico por plataforma).</summary>
    Task<List<PrecioTienda>> BuscarAsync(Juego juego, CancellationToken ct);
}

internal static class Filas
{
    public static List<PrecioTienda> De(PrecioTienda? fila) =>
        fila is null ? new List<PrecioTienda>() : new List<PrecioTienda> { fila };
}
