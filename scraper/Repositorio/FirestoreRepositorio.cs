using Google.Cloud.Firestore;
using PrecioPixel.Config;
using PrecioPixel.Modelos;

namespace PrecioPixel.Repositorio;

/// <summary>
/// Repo en la nube: Firebase Firestore.
///   juegos/{slug}       → lista dinámica de juegos (se edita desde la consola de Firebase o con add/remove)
///   resultados/{slug}   → lo que muestra la web (precios, ofertas, Metacritic, HLTB)
///   meta/estado         → resumen de la última corrida
///
/// Credenciales (en este orden):
///   1. Variable FIREBASE_SERVICE_ACCOUNT con el JSON completo de la cuenta de servicio (ideal para GitHub Actions)
///   2. Variable GOOGLE_APPLICATION_CREDENTIALS con la ruta al archivo .json
/// </summary>
public sealed class FirestoreRepositorio : IRepositorio
{
    private const int LoteMaximo = 400; // Firestore permite 500 escrituras por batch
    private readonly FirestoreDb _db;
    private readonly FirebaseOpciones _opt;

    public string Descripcion => $"Firestore (proyecto {_db.ProjectId})";

    public FirestoreRepositorio(FirebaseOpciones opt)
    {
        _opt = opt;
        var projectId = !string.IsNullOrWhiteSpace(opt.ProjectId)
            ? opt.ProjectId
            : Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
              ?? throw new InvalidOperationException("Falta Firebase:ProjectId (appsettings.json) o la variable FIREBASE_PROJECT_ID.");

        var builder = new FirestoreDbBuilder { ProjectId = projectId };
        var jsonCuenta = Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT");
        if (!string.IsNullOrWhiteSpace(jsonCuenta))
            builder.JsonCredentials = jsonCuenta;

        _db = builder.Build();
    }

    public async Task<List<Juego>> ObtenerJuegosAsync(bool soloActivos, CancellationToken ct)
    {
        Query q = _db.Collection(_opt.ColeccionJuegos);
        if (soloActivos) q = q.WhereEqualTo("activo", true);
        var snap = await q.GetSnapshotAsync(ct);
        return snap.Documents.Select(d => d.ConvertTo<Juego>()).OrderBy(j => j.Titulo).ToList();
    }

    public async Task<Dictionary<string, ResultadoJuego>> ObtenerResultadosAsync(CancellationToken ct)
    {
        var snap = await _db.Collection(_opt.ColeccionResultados).GetSnapshotAsync(ct);
        return snap.Documents.ToDictionary(d => d.Id, d => d.ConvertTo<ResultadoJuego>());
    }

    public async Task GuardarJuegosAsync(IEnumerable<Juego> juegos, CancellationToken ct)
    {
        var col = _db.Collection(_opt.ColeccionJuegos);
        var existentes = (await col.Select(FieldPath.DocumentId).GetSnapshotAsync(ct)).Documents
            .Select(d => d.Id)
            .ToHashSet();

        foreach (var lote in juegos.Chunk(LoteMaximo))
        {
            var batch = _db.StartBatch();
            foreach (var j in lote)
            {
                var doc = col.Document(j.Id);
                // Juego nuevo → documento completo. Juego que ya existe → solo los campos que trae el archivo,
                // para no borrar lo que el scraper completó (fecha de lanzamiento, página de HowLongToBeat…).
                if (existentes.Contains(j.Id)) batch.Update(doc, CamposConValor(j));
                else batch.Set(doc, j);
            }
            await batch.CommitAsync(ct);
        }
    }

    /// <summary>Campos con valor de un juego. Las URLs van una por una (urlsTienda.hltb…) para no reemplazar el mapa completo.</summary>
    private static Dictionary<FieldPath, object> CamposConValor(Juego j)
    {
        var campos = new Dictionary<FieldPath, object>
        {
            [new FieldPath("titulo")] = j.Titulo,
            [new FieldPath("plataformas")] = j.Plataformas,
            [new FieldPath("anio")] = j.Anio,
            [new FieldPath("precioReferencia")] = j.PrecioReferencia,
            [new FieldPath("tipo")] = j.Tipo,
            [new FieldPath("formatos")] = j.Formatos,
            [new FieldPath("genero")] = j.Genero,
            [new FieldPath("activo")] = j.Activo,
        };
        if (j.SteamAppId is { } steam) campos[new FieldPath("steamAppId")] = steam;
        if (j.TerminoBusqueda is { Length: > 0 } termino) campos[new FieldPath("terminoBusqueda")] = termino;
        if (j.MetacriticSlug is { Length: > 0 } slug) campos[new FieldPath("metacriticSlug")] = slug;
        if (j.FechaLanzamiento is { Length: > 0 } fecha) campos[new FieldPath("fechaLanzamiento")] = fecha;
        foreach (var (clave, url) in j.UrlsTienda)
            if (!string.IsNullOrWhiteSpace(url)) campos[new FieldPath("urlsTienda", clave)] = url;
        return campos;
    }

    public async Task ActualizarJuegosAsync(IReadOnlyList<CambioJuego> cambios, CancellationToken ct)
    {
        var col = _db.Collection(_opt.ColeccionJuegos);
        foreach (var lote in cambios.Chunk(LoteMaximo))
        {
            var batch = _db.StartBatch();
            var escrituras = 0;
            foreach (var c in lote)
            {
                var campos = new Dictionary<FieldPath, object>();
                if (c.FechaLanzamiento is { Length: > 0 } fecha) campos[new FieldPath("fechaLanzamiento")] = fecha;
                if (c.UrlHltb is { Length: > 0 } url) campos[new FieldPath("urlsTienda", "hltb")] = url;
                if (campos.Count == 0) continue;
                batch.Update(col.Document(c.Id), campos); // Update: solo esos campos, el resto del juego queda igual
                escrituras++;
            }
            if (escrituras > 0) await batch.CommitAsync(ct);
        }
    }

    public Task DesactivarJuegoAsync(string id, CancellationToken ct) =>
        _db.Collection(_opt.ColeccionJuegos).Document(id)
            .UpdateAsync(new Dictionary<string, object> { ["activo"] = false }, cancellationToken: ct);

    public async Task GuardarResultadosAsync(IReadOnlyList<ResultadoJuego> resultados, ResumenEjecucion resumen, CancellationToken ct)
    {
        var col = _db.Collection(_opt.ColeccionResultados);

        // Los lotes se escriben EN PARALELO (cada batch es atómico)
        await Task.WhenAll(resultados.Chunk(LoteMaximo).Select(async lote =>
        {
            var batch = _db.StartBatch();
            foreach (var r in lote) batch.Set(col.Document(r.Id), r);
            await batch.CommitAsync(ct);
        }));

        // Quitar de la web los juegos que ya no están activos en el repo
        var activos = resultados.Select(r => r.Id).ToHashSet();
        var existentes = await col.Select(FieldPath.DocumentId).GetSnapshotAsync(ct);
        var sobrantes = existentes.Documents.Where(d => !activos.Contains(d.Id)).ToList();
        foreach (var lote in sobrantes.Chunk(LoteMaximo))
        {
            var batch = _db.StartBatch();
            foreach (var d in lote) batch.Delete(d.Reference);
            await batch.CommitAsync(ct);
        }

        await _db.Collection(_opt.ColeccionMeta).Document("estado").SetAsync(resumen, cancellationToken: ct);
    }
}
