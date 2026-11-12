using System.Text.Json.Serialization;
using Google.Cloud.Firestore;

namespace PrecioPixel.Modelos;

/// <summary>Precio de un juego en una tienda concreta (una fila de la "lista de precios" del detalle).</summary>
[FirestoreData]
public sealed class PrecioTienda
{
    [FirestoreProperty("tienda")] public string Tienda { get; set; } = "";

    /// <summary>Scraper que produjo la fila. Sirve para conservar sus precios si esa tienda falla en la próxima corrida.</summary>
    [FirestoreProperty("fuente")] public string Fuente { get; set; } = "";
    [FirestoreProperty("plataforma")] public string Plataforma { get; set; } = "";
    [FirestoreProperty("formato")] public string Formato { get; set; } = "digital";
    [FirestoreProperty("precioActual")] public double PrecioActual { get; set; }
    [FirestoreProperty("precioRegular")] public double PrecioRegular { get; set; }
    [FirestoreProperty("descuentoPct")] public int DescuentoPct { get; set; }
    [FirestoreProperty("enOferta")] public bool EnOferta { get; set; }

    /// <summary>"tienda" si la tienda reporta el descuento, "referencia" si se dedujo contra el precio del repo.</summary>
    [FirestoreProperty("motivoOferta")] public string? MotivoOferta { get; set; }

    [FirestoreProperty("moneda")] public string Moneda { get; set; } = "USD";
    [FirestoreProperty("url")] public string Url { get; set; } = "";
    [FirestoreProperty("tituloEncontrado")] public string TituloEncontrado { get; set; } = "";
    [FirestoreProperty("imagenUrl")] public string? ImagenUrl { get; set; }
    [FirestoreProperty("similitud")] public double Similitud { get; set; }
    [FirestoreProperty("consultadoEn")] public DateTime ConsultadoEn { get; set; } = DateTime.UtcNow;

    /// <summary>false cuando la tienda falló en esta corrida y se conserva el último precio conocido.</summary>
    [FirestoreProperty("vigente")] public bool Vigente { get; set; } = true;

    /// <summary>Fecha de lanzamiento que reporta la tienda (enriquece el repo). No se guarda.</summary>
    [JsonIgnore] public string? FechaLanzamiento { get; set; }

    /// <summary>Metascore que algunas tiendas exponen (Steam). Se usa como respaldo. No se guarda.</summary>
    [JsonIgnore] public int? MetacriticTienda { get; set; }

    [JsonIgnore] public string Clave => $"{Tienda}|{Plataforma}|{Formato}";
}

[FirestoreData]
public sealed class HltbInfo
{
    [FirestoreProperty("principal")] public double? Principal { get; set; }
    [FirestoreProperty("principalExtras")] public double? PrincipalExtras { get; set; }
    [FirestoreProperty("completista")] public double? Completista { get; set; }
    [FirestoreProperty("todosEstilos")] public double? TodosEstilos { get; set; }
    [FirestoreProperty("url")] public string? Url { get; set; }
    [FirestoreProperty("tituloEncontrado")] public string? TituloEncontrado { get; set; }

    /// <summary>Portada (box art) que muestra la página del juego en HowLongToBeat.</summary>
    [FirestoreProperty("imagenUrl")] public string? ImagenUrl { get; set; }
}

[FirestoreData]
public sealed class MetacriticInfo
{
    /// <summary>Score mostrado en la tarjeta (el mejor de las consolas del repo).</summary>
    [FirestoreProperty("score")] public int? Score { get; set; }

    /// <summary>Score por cada consola asociada al juego en el repo.</summary>
    [FirestoreProperty("porPlataforma")] public Dictionary<string, int> PorPlataforma { get; set; } = new();

    [FirestoreProperty("url")] public string? Url { get; set; }
    [FirestoreProperty("fuente")] public string? Fuente { get; set; }

    /// <summary>Portada que muestra la página del juego en Metacritic.</summary>
    [FirestoreProperty("imagenUrl")] public string? ImagenUrl { get; set; }
}

/// <summary>Resultado del scraping de un juego (colección "resultados"). Es lo que lee la web.</summary>
[FirestoreData]
public sealed class ResultadoJuego
{
    [FirestoreDocumentId] public string Id { get; set; } = "";
    [FirestoreProperty("titulo")] public string Titulo { get; set; } = "";
    /// <summary>Portada principal del juego (la primera de <see cref="Imagenes"/>).</summary>
    [FirestoreProperty("imagenUrl")] public string? ImagenUrl { get; set; }

    /// <summary>Todas las portadas encontradas, en orden de preferencia. La web usa la siguiente si una no carga.</summary>
    [FirestoreProperty("imagenes")] public List<string> Imagenes { get; set; } = new();

    [FirestoreProperty("plataformas")] public List<string> Plataformas { get; set; } = new();
    [FirestoreProperty("tipo")] public string Tipo { get; set; } = "juego";
    [FirestoreProperty("formatos")] public List<string> Formatos { get; set; } = new();
    [FirestoreProperty("genero")] public string Genero { get; set; } = "";
    [FirestoreProperty("anio")] public int Anio { get; set; }
    [FirestoreProperty("fechaLanzamiento")] public string? FechaLanzamiento { get; set; }
    [FirestoreProperty("precioReferencia")] public double PrecioReferencia { get; set; }

    /// <summary>Lista de precios ordenada de menor a mayor.</summary>
    [FirestoreProperty("precios")] public List<PrecioTienda> Precios { get; set; } = new();

    [FirestoreProperty("mejorPrecio")] public double? MejorPrecio { get; set; }
    [FirestoreProperty("precioRegular")] public double? PrecioRegular { get; set; }
    [FirestoreProperty("descuentoPct")] public int DescuentoPct { get; set; }
    [FirestoreProperty("enOferta")] public bool EnOferta { get; set; }
    [FirestoreProperty("mejorTienda")] public string? MejorTienda { get; set; }
    [FirestoreProperty("tiendas")] public List<string> Tiendas { get; set; } = new();

    [FirestoreProperty("metacritic")] public MetacriticInfo Metacritic { get; set; } = new();
    [FirestoreProperty("hltb")] public HltbInfo? Hltb { get; set; }

    [FirestoreProperty("minimoHistorico")] public double? MinimoHistorico { get; set; }
    [FirestoreProperty("fechaMinimoHistorico")] public DateTime? FechaMinimoHistorico { get; set; }
    [FirestoreProperty("igualaMinimoHistorico")] public bool IgualaMinimoHistorico { get; set; }

    [FirestoreProperty("actualizadoEn")] public DateTime ActualizadoEn { get; set; } = DateTime.UtcNow;

    /// <summary>ok | parcial (alguna fuente falló) | sin-datos</summary>
    [FirestoreProperty("estado")] public string Estado { get; set; } = "ok";
    [FirestoreProperty("errores")] public List<string> Errores { get; set; } = new();
    [FirestoreProperty("duracionMs")] public long DuracionMs { get; set; }
}

/// <summary>Documento meta/estado: lo muestra la web ("Actualizado hace X min").</summary>
[FirestoreData]
public sealed class ResumenEjecucion
{
    [FirestoreProperty("inicio")] public DateTime Inicio { get; set; }
    [FirestoreProperty("fin")] public DateTime Fin { get; set; }
    [FirestoreProperty("modo")] public string Modo { get; set; } = "paralelo";
    [FirestoreProperty("juegos")] public int Juegos { get; set; }
    [FirestoreProperty("juegosOk")] public int JuegosOk { get; set; }
    [FirestoreProperty("juegosParciales")] public int JuegosParciales { get; set; }
    [FirestoreProperty("juegosSinDatos")] public int JuegosSinDatos { get; set; }
    [FirestoreProperty("peticionesHttp")] public long PeticionesHttp { get; set; }
    [FirestoreProperty("reintentos")] public long Reintentos { get; set; }
    [FirestoreProperty("hilosDistintos")] public int HilosDistintos { get; set; }
    [FirestoreProperty("nucleos")] public int Nucleos { get; set; } = Environment.ProcessorCount;
    [FirestoreProperty("maxJuegosEnParalelo")] public int MaxJuegosEnParalelo { get; set; }
    [FirestoreProperty("duracionSegundos")] public double DuracionSegundos { get; set; }
    [FirestoreProperty("fuentesCaidas")] public List<string> FuentesCaidas { get; set; } = new();
}
