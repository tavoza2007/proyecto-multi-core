using System.Text.Json;
using PrecioPixel.Modelos;

namespace PrecioPixel.Repositorio;

/// <summary>
/// Repo local (archivos JSON). Sirve para probar todo sin Firebase:
///   dotnet run -- scrape --local
/// Lee los juegos de seed/juegos.json y escribe los resultados en web/data/deals.json.
/// </summary>
public sealed class JsonRepositorio(string archivoJuegos, string archivoResultados) : IRepositorio
{
    private static readonly SemaphoreSlim Candado = new(1, 1);

    public string Descripcion => $"JSON local ({Path.GetFileName(archivoJuegos)} → {Path.GetFileName(archivoResultados)})";

    public async Task<List<Juego>> ObtenerJuegosAsync(bool soloActivos, CancellationToken ct)
    {
        await using var fs = File.OpenRead(archivoJuegos);
        var juegos = await JsonSerializer.DeserializeAsync<List<Juego>>(fs, Json.Opciones, ct) ?? new();
        foreach (var j in juegos.Where(j => string.IsNullOrWhiteSpace(j.Id)))
            j.Id = Util.Texto.Slug(j.Titulo);
        return juegos.Where(j => !soloActivos || j.Activo).OrderBy(j => j.Titulo).ToList();
    }

    public async Task<Dictionary<string, ResultadoJuego>> ObtenerResultadosAsync(CancellationToken ct)
    {
        if (!File.Exists(archivoResultados)) return new();
        await using var fs = File.OpenRead(archivoResultados);
        var export = await JsonSerializer.DeserializeAsync<ExportWeb>(fs, Json.Opciones, ct);
        // Los datos de demostración no son una corrida anterior real: nunca se mezclan con los reales
        if (export is null || export.Demo) return new();
        return export.Juegos.ToDictionary(r => r.Id);
    }

    public async Task GuardarJuegosAsync(IEnumerable<Juego> juegos, CancellationToken ct)
    {
        await Candado.WaitAsync(ct);
        try
        {
            var actuales = File.Exists(archivoJuegos) ? await ObtenerJuegosAsync(false, ct) : new List<Juego>();
            var mapa = actuales.ToDictionary(j => j.Id);
            foreach (var j in juegos) mapa[j.Id] = j;
            await File.WriteAllTextAsync(archivoJuegos,
                JsonSerializer.Serialize(mapa.Values.OrderBy(j => j.Titulo), Json.Opciones), ct);
        }
        finally { Candado.Release(); }
    }

    public async Task DesactivarJuegoAsync(string id, CancellationToken ct)
    {
        var juegos = await ObtenerJuegosAsync(false, ct);
        var j = juegos.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"No existe el juego '{id}'");
        j.Activo = false;
        await GuardarJuegosAsync(new[] { j }, ct);
    }

    public Task GuardarResultadosAsync(IReadOnlyList<ResultadoJuego> resultados, ResumenEjecucion resumen, CancellationToken ct) =>
        ExportWeb.EscribirAsync(archivoResultados, resultados, resumen, demo: false, ct);
}

/// <summary>Formato del archivo deals.json que puede leer la web (modo demo / respaldo).</summary>
public sealed class ExportWeb
{
    public DateTime GeneradoEn { get; set; }
    public bool Demo { get; set; }
    public ResumenEjecucion? Resumen { get; set; }
    public List<ResultadoJuego> Juegos { get; set; } = new();

    public static async Task EscribirAsync(string ruta, IReadOnlyList<ResultadoJuego> resultados, ResumenEjecucion resumen, bool demo, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(ruta));
        if (dir is not null) Directory.CreateDirectory(dir);
        var export = new ExportWeb { GeneradoEn = DateTime.UtcNow, Demo = demo, Resumen = resumen, Juegos = resultados.ToList() };
        var tmp = ruta + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(export, Json.Compacto), ct);
        File.Move(tmp, ruta, overwrite: true); // escritura atómica: la web nunca lee un archivo a medias
    }
}
