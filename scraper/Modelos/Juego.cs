using System.Text.Json.Serialization;
using Google.Cloud.Firestore;

namespace PrecioPixel.Modelos;

/// <summary>
/// Un juego del REPO (colección "juegos" en Firestore). La lista es dinámica:
/// se agrega/edita/desactiva desde la consola de Firebase o con los comandos add/remove.
/// </summary>
[FirestoreData]
public sealed class Juego
{
    /// <summary>Slug del juego, también es el id del documento (ej. "celeste").</summary>
    [FirestoreDocumentId] public string Id { get; set; } = "";

    [FirestoreProperty("titulo")] public string Titulo { get; set; } = "";

    /// <summary>Códigos: switch, switch2, ps5, ps4, xbox-series, xbox-one, pc, 3ds.</summary>
    [FirestoreProperty("plataformas")] public List<string> Plataformas { get; set; } = new();

    [FirestoreProperty("anio")] public int Anio { get; set; }

    /// <summary>Precio de referencia / sugerido (MSRP) en USD definido por el equipo.</summary>
    [FirestoreProperty("precioReferencia")] public double PrecioReferencia { get; set; }

    /// <summary>juego | juego+dlc | remaster | coleccion | otro</summary>
    [FirestoreProperty("tipo")] public string Tipo { get; set; } = "juego";

    /// <summary>digital y/o fisico.</summary>
    [FirestoreProperty("formatos")] public List<string> Formatos { get; set; } = new() { "digital" };

    [FirestoreProperty("genero")] public string Genero { get; set; } = "";

    /// <summary>Opcional: id de Steam para ir directo a la ficha (si no, se busca por título).</summary>
    [FirestoreProperty("steamAppId")] public long? SteamAppId { get; set; }

    /// <summary>Opcional: texto de búsqueda distinto del título.</summary>
    [FirestoreProperty("terminoBusqueda")] public string? TerminoBusqueda { get; set; }

    /// <summary>Opcional: slug de Metacritic cuando no coincide con el título.</summary>
    [FirestoreProperty("metacriticSlug")] public string? MetacriticSlug { get; set; }

    /// <summary>
    /// Opcional: URL exacta de la página del juego en un sitio, cuando la búsqueda no la encuentra bien.
    /// Claves: steam, gog, nintendo, nintendo-switch2, playstation, metacritic, hltb.
    /// </summary>
    [FirestoreProperty("urlsTienda")] public Dictionary<string, string> UrlsTienda { get; set; } = new();

    [FirestoreProperty("activo")] public bool Activo { get; set; } = true;

    public string? Url(string clave) => UrlsTienda.TryGetValue(clave, out var u) && !string.IsNullOrWhiteSpace(u) ? u : null;

    [JsonIgnore] public string Busqueda => string.IsNullOrWhiteSpace(TerminoBusqueda) ? Titulo : TerminoBusqueda!;

    public bool Tiene(params string[] codigos) => Plataformas.Any(p => codigos.Contains(p, StringComparer.OrdinalIgnoreCase));
}
