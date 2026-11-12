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
        foreach (var lote in juegos.Chunk(LoteMaximo))
        {
            var batch = _db.StartBatch();
            foreach (var j in lote)
                batch.Set(_db.Collection(_opt.ColeccionJuegos).Document(j.Id), j, SetOptions.MergeAll);
            await batch.CommitAsync(ct);
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
