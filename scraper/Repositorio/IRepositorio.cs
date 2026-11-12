using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrecioPixel.Modelos;

namespace PrecioPixel.Repositorio;

/// <summary>Acceso al REPO de juegos y a los resultados del scraping.</summary>
public interface IRepositorio
{
    string Descripcion { get; }
    Task<List<Juego>> ObtenerJuegosAsync(bool soloActivos, CancellationToken ct);
    Task<Dictionary<string, ResultadoJuego>> ObtenerResultadosAsync(CancellationToken ct);
    Task GuardarJuegosAsync(IEnumerable<Juego> juegos, CancellationToken ct);
    Task DesactivarJuegoAsync(string id, CancellationToken ct);
    Task GuardarResultadosAsync(IReadOnlyList<ResultadoJuego> resultados, ResumenEjecucion resumen, CancellationToken ct);

    /// <summary>
    /// Completa juegos que ya están en el REPO con lo que aprendió una corrida (fecha de lanzamiento, página de
    /// HowLongToBeat) sin tocar el resto de sus campos.
    /// </summary>
    Task ActualizarJuegosAsync(IReadOnlyList<CambioJuego> cambios, CancellationToken ct);
}

/// <summary>Datos nuevos de un juego del REPO. null = ese campo no cambia.</summary>
public sealed record CambioJuego(string Id, string? FechaLanzamiento, string? UrlHltb);

public static class Json
{
    public static readonly JsonSerializerOptions Opciones = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // conserva tildes y ñ legibles
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly JsonSerializerOptions Compacto = new(Opciones) { WriteIndented = false };
}
