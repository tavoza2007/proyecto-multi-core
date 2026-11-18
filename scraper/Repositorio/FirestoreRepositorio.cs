using Google.Cloud.Firestore;
using Grpc.Core;
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
    // Lotes chicos y uno tras otro: escribir los 260 juegos de golpe puede superar el ancho de banda de
    // escritura que Firestore acepta en ese momento (error ResourceExhausted del 18 nov).
    private const int LoteMaximo = 25;
    private const int IntentosMaximos = 6;
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
            await CommitConReintentosAsync(batch =>
            {
                foreach (var j in lote)
                {
                    var doc = col.Document(j.Id);
                    // Juego nuevo → documento completo. Juego que ya existe → solo los campos que trae el archivo,
                    // para no borrar lo que el scraper completó (fecha de lanzamiento, página de HowLongToBeat…).
                    if (existentes.Contains(j.Id)) batch.Update(doc, CamposConValor(j));
                    else batch.Set(doc, j);
                }
            }, ct);
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
        var actualizaciones = new List<(DocumentReference doc, Dictionary<FieldPath, object> campos)>();
        foreach (var c in cambios)
        {
            var campos = new Dictionary<FieldPath, object>();
            if (c.FechaLanzamiento is { Length: > 0 } fecha) campos[new FieldPath("fechaLanzamiento")] = fecha;
            if (c.UrlHltb is { Length: > 0 } url) campos[new FieldPath("urlsTienda", "hltb")] = url;
            if (campos.Count > 0) actualizaciones.Add((col.Document(c.Id), campos));
        }

        // Update: solo esos campos, el resto del juego queda igual
        foreach (var lote in actualizaciones.Chunk(LoteMaximo))
            await CommitConReintentosAsync(batch => { foreach (var (doc, campos) in lote) batch.Update(doc, campos); }, ct);
    }

    public Task DesactivarJuegoAsync(string id, CancellationToken ct) =>
        _db.Collection(_opt.ColeccionJuegos).Document(id)
            .UpdateAsync(new Dictionary<string, object> { ["activo"] = false }, cancellationToken: ct);

    public async Task GuardarResultadosAsync(IReadOnlyList<ResultadoJuego> resultados, ResumenEjecucion resumen, CancellationToken ct)
    {
        var col = _db.Collection(_opt.ColeccionResultados);

        // Lotes de 25, uno tras otro (cada batch es atómico)
        foreach (var lote in resultados.Chunk(LoteMaximo))
            await CommitConReintentosAsync(batch => { foreach (var r in lote) batch.Set(col.Document(r.Id), r); }, ct);

        // Quitar de la web los juegos que ya no están activos en el repo
        var activos = resultados.Select(r => r.Id).ToHashSet();
        var existentes = await col.Select(FieldPath.DocumentId).GetSnapshotAsync(ct);
        var sobrantes = existentes.Documents.Where(d => !activos.Contains(d.Id)).ToList();
        foreach (var lote in sobrantes.Chunk(LoteMaximo))
            await CommitConReintentosAsync(batch => { foreach (var d in lote) batch.Delete(d.Reference); }, ct);

        // El resumen va al final: la web muestra su hora como "Actualizado el…"
        var estado = _db.Collection(_opt.ColeccionMeta).Document("estado");
        await CommitConReintentosAsync(batch => batch.Set(estado, resumen), ct);
    }

    /// <summary>
    /// Escribe un batch. Si Firestore responde que está saturado o no disponible (ResourceExhausted, Unavailable…),
    /// espera y lo vuelve a intentar con espera exponencial + aleatoria (2, 4, 8, 16, 32 s), como pide su documentación.
    /// El batch se arma de nuevo en cada intento.
    /// </summary>
    private async Task CommitConReintentosAsync(Action<WriteBatch> armar, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            var batch = _db.StartBatch();
            armar(batch);
            try
            {
                await batch.CommitAsync(ct);
                return;
            }
            catch (RpcException ex) when (EsTransitorio(ex.StatusCode) && intento < IntentosMaximos)
            {
                var espera = TimeSpan.FromSeconds(Math.Pow(2, intento)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                Log.Advertencia($"Firestore respondió {ex.StatusCode}; reintento {intento} de {IntentosMaximos - 1} en {espera.TotalSeconds:0.#} s");
                await Task.Delay(espera, ct);
            }
        }
    }

    private static bool EsTransitorio(StatusCode codigo) =>
        codigo is StatusCode.ResourceExhausted or StatusCode.Unavailable or StatusCode.DeadlineExceeded
            or StatusCode.Aborted or StatusCode.Internal;
}
