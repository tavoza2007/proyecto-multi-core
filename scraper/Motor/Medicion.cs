using System.Text.Json;
using PrecioPixel.Repositorio;

namespace PrecioPixel.Motor;

/// <summary>
/// Una corrida del análisis de rendimiento: la MISMA lista de juegos con una configuración de paralelismo distinta.
///   "secuencial" → los tres niveles uno por uno (la base para calcular el speedup)
///   "8"          → 8 juegos a la vez en el nivel 1, con los niveles 2 y 3 en paralelo
///   "solo-n1"    → solo el nivel 1 en paralelo (núcleos × 2 juegos a la vez; fuentes y tiendas una por una)
///   "solo-n2"    → solo el nivel 2 (las fuentes de cada juego a la vez; juegos y tiendas uno por uno)
///   "solo-n3"    → solo el nivel 3 (las tiendas de cada juego a la vez; juegos y fuentes uno por uno)
/// Cada configuración se mide varias veces (Repeticion) y la página usa la mediana.
/// </summary>
public sealed class Medicion
{
    public string Config { get; set; } = "";

    /// <summary>"secuencial", "paralelo" (los tres niveles) o "niveles" (solo algunos niveles).</summary>
    public string Modo { get; set; } = "paralelo";

    /// <summary>Niveles encendidos: "1+2+3", "3", "ninguno"…</summary>
    public string Niveles { get; set; } = "";

    /// <summary>Número de repetición de esta configuración (1, 2, 3…).</summary>
    public int Repeticion { get; set; } = 1;

    /// <summary>Juegos procesados a la vez (nivel 1). En secuencial es 1.</summary>
    public int Hilos { get; set; }

    public int Nucleos { get; set; }
    public int Juegos { get; set; }

    /// <summary>Duración del scraping de todos los juegos (la parte que se paraleliza).</summary>
    public double Segundos { get; set; }

    /// <summary>Lectura del REPO antes de empezar (parte secuencial del programa).</summary>
    public double SegundosPreparacion { get; set; }

    public long Peticiones { get; set; }
    public long Reintentos { get; set; }
    public int HilosDistintos { get; set; }
    public int JuegosOk { get; set; }
    public int JuegosParciales { get; set; }
    public int JuegosSinDatos { get; set; }
    public List<string> FuentesCaidas { get; set; } = new();
    public string Maquina { get; set; } = "";
    public DateTime Fecha { get; set; }
}

/// <summary>Archivo que lee la página de rendimiento de la web (web/data/rendimiento.json).</summary>
public sealed class ArchivoRendimiento
{
    public DateTime GeneradoEn { get; set; }
    public List<Medicion> Mediciones { get; set; } = new();

    /// <summary>Se escribe completo después de cada configuración: si una se cae, las anteriores quedan guardadas.</summary>
    public async Task EscribirAsync(string ruta, CancellationToken ct)
    {
        GeneradoEn = DateTime.UtcNow;
        var dir = Path.GetDirectoryName(Path.GetFullPath(ruta));
        if (dir is not null) Directory.CreateDirectory(dir);
        var tmp = ruta + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(this, Json.Opciones), ct);
        File.Move(tmp, ruta, overwrite: true);
    }
}
