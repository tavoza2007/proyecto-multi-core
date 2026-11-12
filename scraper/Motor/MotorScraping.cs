using System.Collections.Concurrent;
using System.Diagnostics;
using PrecioPixel.Config;
using PrecioPixel.Fuentes;
using PrecioPixel.Modelos;
using PrecioPixel.Red;
using PrecioPixel.Tiendas;
using PrecioPixel.Util;

namespace PrecioPixel.Motor;

/// <summary>
/// Corazón del proyecto: aplica el modelo de referencia con sus TRES niveles de paralelismo.
///
///  NIVEL 1 — Para cada juego del repo:     Parallel.ForEachAsync sobre la lista (N juegos a la vez, N ≈ núcleos × 2).
///  NIVEL 2 — Para cada juego, sus 3 fuentes: precios ‖ Metacritic ‖ HowLongToBeat   (Task.WhenAll).
///  NIVEL 3 — Dentro de "precios", cada tienda: Steam ‖ GOG ‖ eShop ‖ PS Store ‖ Amazon (Task.WhenAll).
///
/// Con modo secuencial se hace exactamente el mismo trabajo pero uno por uno (sirve para medir el speedup).
/// </summary>
public sealed class MotorScraping
{
    private readonly IReadOnlyList<ITiendaScraper> _tiendas;
    private readonly MetacriticScraper? _metacritic;
    private readonly HowLongToBeatScraper? _hltb;
    private readonly Opciones _opt;
    private readonly DetectorOfertas _ofertas;
    private readonly ClienteHttpResiliente _http;

    private readonly ConcurrentDictionary<int, byte> _hilosUsados = new();
    private readonly ConcurrentDictionary<string, int> _fallosPorFuente = new();
    private int _procesados;

    public MotorScraping(IReadOnlyList<ITiendaScraper> tiendas, MetacriticScraper? metacritic,
                         HowLongToBeatScraper? hltb, ClienteHttpResiliente http, Opciones opt)
    {
        _tiendas = tiendas;
        _metacritic = metacritic;
        _hltb = hltb;
        _http = http;
        _opt = opt;
        _ofertas = new DetectorOfertas(opt.Ofertas);
    }

    public async Task<(List<ResultadoJuego> resultados, ResumenEjecucion resumen)> EjecutarAsync(
        IReadOnlyList<Juego> juegos,
        IReadOnlyDictionary<string, ResultadoJuego> previos,
        bool paralelo,
        CancellationToken ct)
    {
        _hilosUsados.Clear();
        _fallosPorFuente.Clear();
        _procesados = 0;
        var peticionesAntes = _http.Peticiones;
        var reintentosAntes = _http.Reintentos;

        var resumen = new ResumenEjecucion
        {
            Inicio = DateTime.UtcNow,
            Modo = paralelo ? "paralelo" : "secuencial",
            Juegos = juegos.Count,
            MaxJuegosEnParalelo = paralelo ? _opt.Paralelismo.JuegosEnParaleloEfectivo : 1,
        };
        var reloj = Stopwatch.StartNew();
        var resultados = new ConcurrentBag<ResultadoJuego>();

        // ───────────── NIVEL 1: un juego por tarea ─────────────
        if (paralelo)
        {
            var opciones = new ParallelOptions
            {
                MaxDegreeOfParallelism = _opt.Paralelismo.JuegosEnParaleloEfectivo,
                CancellationToken = ct,
            };
            await Parallel.ForEachAsync(juegos, opciones, async (juego, tok) =>
            {
                var previo = previos.GetValueOrDefault(juego.Id);
                resultados.Add(await ProcesarJuegoAsync(juego, previo, paralelo: true, juegos.Count, tok));
            });
        }
        else
        {
            foreach (var juego in juegos)
                resultados.Add(await ProcesarJuegoAsync(juego, previos.GetValueOrDefault(juego.Id), paralelo: false, juegos.Count, ct));
        }

        reloj.Stop();
        var lista = resultados.OrderBy(r => r.Titulo, StringComparer.OrdinalIgnoreCase).ToList();

        resumen.Fin = DateTime.UtcNow;
        resumen.DuracionSegundos = Math.Round(reloj.Elapsed.TotalSeconds, 2);
        resumen.JuegosOk = lista.Count(r => r.Estado == "ok");
        resumen.JuegosParciales = lista.Count(r => r.Estado == "parcial");
        resumen.JuegosSinDatos = lista.Count(r => r.Estado == "sin-datos");
        resumen.PeticionesHttp = _http.Peticiones - peticionesAntes;
        resumen.Reintentos = _http.Reintentos - reintentosAntes;
        resumen.HilosDistintos = _hilosUsados.Count;
        resumen.FuentesCaidas = _fallosPorFuente
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} ({kv.Value} fallos)")
            .ToList();

        return (lista, resumen);
    }

    // ───────────── NIVEL 2: las fuentes de UN juego ─────────────
    private async Task<ResultadoJuego> ProcesarJuegoAsync(Juego juego, ResultadoJuego? previo, bool paralelo, int total, CancellationToken ct)
    {
        MarcarHilo();
        var reloj = Stopwatch.StartNew();

        Func<Task<ResultadoPrecios>> fPrecios = () => ObtenerPreciosAsync(juego, paralelo, ct);
        Func<Task<(MetacriticInfo? valor, string? error)>> fMeta = () => _metacritic is null
            ? Task.FromResult<(MetacriticInfo?, string?)>((null, null))
            : IntentarAsync("Metacritic", () => _metacritic.ObtenerAsync(juego, ct), ct);
        Func<Task<(HltbInfo? valor, string? error)>> fHltb = () => _hltb is null
            ? Task.FromResult<(HltbInfo?, string?)>((null, null))
            : IntentarAsync("HowLongToBeat", () => _hltb.ObtenerAsync(juego, ct), ct);

        ResultadoPrecios precios;
        (MetacriticInfo? valor, string? error) meta;
        (HltbInfo? valor, string? error) hltb;

        if (paralelo)
        {
            var tPrecios = fPrecios();
            var tMeta = fMeta();
            var tHltb = fHltb();
            await Task.WhenAll(tPrecios, tMeta, tHltb);
            (precios, meta, hltb) = (tPrecios.Result, tMeta.Result, tHltb.Result);
        }
        else
        {
            precios = await fPrecios();
            meta = await fMeta();
            hltb = await fHltb();
        }

        MarcarHilo();
        var r = Consolidar(juego, previo, precios, meta, hltb);
        r.DuracionMs = reloj.ElapsedMilliseconds;

        var n = Interlocked.Increment(ref _procesados);
        var precioTxt = r.MejorPrecio is null ? "sin precio" : $"${r.MejorPrecio:0.00} en {r.MejorTienda}" + (r.EnOferta ? $" (-{r.DescuentoPct}%)" : "");
        var linea = $"[{n,3}/{total}] {r.Titulo} — {precioTxt} · MC {r.Metacritic.Score?.ToString() ?? "–"} · HLTB {r.Hltb?.Principal?.ToString("0.#") ?? "–"}h · {r.DuracionMs} ms";
        if (r.Estado == "ok") Log.Ok("✓ " + linea);
        else Log.Advertencia((r.Estado == "parcial" ? "◐ " : "✗ ") + linea + "  ⚠ " + string.Join(" | ", r.Errores));

        return r;
    }

    private sealed record ResultadoPrecios(List<PrecioTienda> Filas, HashSet<string> FuentesFallidas, List<string> Errores);

    // ───────────── NIVEL 3: las tiendas de UN juego ─────────────
    private async Task<ResultadoPrecios> ObtenerPreciosAsync(Juego juego, bool paralelo, CancellationToken ct)
    {
        var aplicables = _tiendas.Where(t => t.AplicaA(juego)).ToList();
        var respuestas = new List<(string tienda, List<PrecioTienda>? filas, string? error)>();

        if (paralelo)
        {
            var tareas = aplicables.Select(async t =>
            {
                var (filas, error) = await IntentarAsync(t.Nombre, () => t.BuscarAsync(juego, ct), ct);
                return (t.Nombre, filas, error);
            });
            respuestas.AddRange(await Task.WhenAll(tareas));
        }
        else
        {
            foreach (var t in aplicables)
            {
                var (filas, error) = await IntentarAsync(t.Nombre, () => t.BuscarAsync(juego, ct), ct);
                respuestas.Add((t.Nombre, filas, error));
            }
        }

        var todas = new List<PrecioTienda>();
        var fallidas = new HashSet<string>();
        var errores = new List<string>();
        foreach (var (tienda, filas, error) in respuestas)
        {
            if (error is not null)
            {
                fallidas.Add(tienda);
                errores.Add(error);
                continue;
            }
            foreach (var f in filas!)
            {
                // Validación de cordura: un precio mayor a 4 veces el de referencia casi siempre es
                // otra moneda, una edición de colección o un vendedor de reventa. No se publica.
                if (juego.PrecioReferencia > 0 && f.PrecioActual > juego.PrecioReferencia * 4)
                {
                    errores.Add($"{tienda}: precio descartado (${f.PrecioActual:0.00}, más de 4 veces el de referencia)");
                    continue;
                }
                f.Fuente = tienda;
                f.ConsultadoEn = DateTime.UtcNow;
                f.Vigente = true;
                _ofertas.Evaluar(f, juego);
                todas.Add(f);
            }
        }
        return new ResultadoPrecios(todas, fallidas, errores);
    }

    /// <summary>Ejecuta una fuente capturando su error, para que la falla de un sitio no tumbe al resto.</summary>
    private async Task<(T? valor, string? error)> IntentarAsync<T>(string fuente, Func<Task<T>> accion, CancellationToken ct)
    {
        try
        {
            MarcarHilo();
            var v = await accion();
            MarcarHilo();
            return (v, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _fallosPorFuente.AddOrUpdate(fuente, 1, (_, n) => n + 1);
            var msg = ex is FuenteNoDisponibleException or BloqueoDetectadoException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log.Debug($"{fuente} falló: {msg}");
            return (default, $"{fuente}: {Recortar(msg, 140)}");
        }
    }

    private ResultadoJuego Consolidar(Juego juego, ResultadoJuego? previo, ResultadoPrecios precios,
                                      (MetacriticInfo? valor, string? error) meta, (HltbInfo? valor, string? error) hltb)
    {
        var filas = new List<PrecioTienda>(precios.Filas);

        // Garantía de datos: si una tienda falló AHORA, se conserva su último precio conocido marcado como no vigente.
        if (previo is not null)
            foreach (var viejo in previo.Precios.Where(p => precios.FuentesFallidas.Contains(p.Fuente)))
            {
                viejo.Vigente = false;
                filas.Add(viejo);
            }

        filas = filas.OrderBy(p => p.PrecioActual).ThenBy(p => p.Tienda).ToList();
        var mejor = filas.FirstOrDefault(p => p.Vigente) ?? filas.FirstOrDefault();

        var errores = new List<string>(precios.Errores);
        if (meta.error is not null) errores.Add(meta.error);
        if (hltb.error is not null) errores.Add(hltb.error);

        // Metacritic: si falló se conserva el anterior; si no hay, se usa el metascore que reporta Steam
        var mc = meta.valor ?? (meta.error is not null ? previo?.Metacritic : null) ?? new MetacriticInfo();
        if (mc.Score is null && filas.FirstOrDefault(f => f.MetacriticTienda is not null) is { } conMc)
        {
            mc.Score = conMc.MetacriticTienda;
            mc.PorPlataforma[conMc.Plataforma] = conMc.MetacriticTienda!.Value;
            mc.Fuente = "Metacritic (vía Steam)";
        }

        var r = new ResultadoJuego
        {
            Id = juego.Id,
            Titulo = juego.Titulo,
            Plataformas = juego.Plataformas,
            Tipo = juego.Tipo,
            Formatos = juego.Formatos,
            Genero = juego.Genero,
            Anio = juego.Anio,
            PrecioReferencia = juego.PrecioReferencia,
            Precios = filas,
            Tiendas = filas.Select(f => f.Tienda).Distinct().OrderBy(t => t).ToList(),
            Metacritic = mc,
            Hltb = hltb.valor ?? (hltb.error is not null ? previo?.Hltb : null),
            Imagenes = ElegirPortadas(filas, mc, hltb.valor ?? previo?.Hltb, previo),
            FechaLanzamiento = ElegirFechaLanzamiento(juego, filas, mc, previo),
            ActualizadoEn = DateTime.UtcNow,
            Errores = errores,
        };

        r.ImagenUrl = r.Imagenes.FirstOrDefault();

        if (mejor is not null)
        {
            r.MejorPrecio = mejor.PrecioActual;
            r.MejorTienda = mejor.Tienda;
            r.EnOferta = mejor.EnOferta;
            r.DescuentoPct = mejor.DescuentoPct;
            r.PrecioRegular = DetectorOfertas.PrecioTachado(mejor, juego);
        }

        // Mínimo histórico (entre todas las corridas)
        r.MinimoHistorico = previo?.MinimoHistorico;
        r.FechaMinimoHistorico = previo?.FechaMinimoHistorico;
        if (r.MejorPrecio is { } actual && mejor!.Vigente)
        {
            if (r.MinimoHistorico is null || actual < r.MinimoHistorico - 0.009)
            {
                r.MinimoHistorico = actual;
                r.FechaMinimoHistorico = DateTime.UtcNow;
            }
            r.IgualaMinimoHistorico = previo?.MinimoHistorico is not null && actual <= previo.MinimoHistorico + 0.009 && r.EnOferta;
        }

        r.Estado = filas.Count == 0 ? "sin-datos" : errores.Count > 0 ? "parcial" : "ok";
        return r;
    }

    /// <summary>
    /// Fecha de lanzamiento "yyyy-MM-dd": la del REPO si ya la tiene; si no, la que publican Metacritic y las tiendas.
    /// Un port sale después del original (DOOM: PC en 2016, Switch en 2017), por eso se prefiere la fecha cuyo año
    /// coincide con el año del repo y, si ninguna coincide, la más antigua.
    /// </summary>
    private static string? ElegirFechaLanzamiento(Juego juego, List<PrecioTienda> filas, MetacriticInfo mc, ResultadoJuego? previo)
    {
        if (Fechas.Normalizar(juego.FechaLanzamiento) is { } delRepo) return delRepo;

        // Lo que se leyó en esta corrida + la fecha de la corrida anterior (por si hoy falló la tienda que la dio)
        var candidatas = filas.Select(f => f.FechaLanzamiento)
            .Prepend(mc.FechaLanzamiento)
            .Prepend(previo?.FechaLanzamiento)
            .Select(Fechas.Normalizar)
            .OfType<string>()
            .Distinct()
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return candidatas.FirstOrDefault(f => Fechas.Anio(f) == juego.Anio) ?? candidatas.FirstOrDefault();
    }

    /// <summary>
    /// Portadas del juego tomadas de las páginas que se leyeron, en orden de preferencia:
    /// arte horizontal de las tiendas digitales primero (llena mejor la tarjeta), luego la portada de
    /// Metacritic y la de HowLongToBeat (todos los juegos tienen ahí su portada) y al final la foto de la caja en Amazon.
    /// La web prueba la siguiente si una no carga.
    /// </summary>
    private static List<string> ElegirPortadas(List<PrecioTienda> filas, MetacriticInfo mc, HltbInfo? hltb, ResultadoJuego? previo)
    {
        var portadas = new List<string>();
        void Agregar(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http") && !portadas.Contains(url)) portadas.Add(url);
        }

        foreach (var tienda in new[] { "Steam", "Nintendo eShop", "PlayStation Store", "GOG" })
            foreach (var f in filas.Where(f => f.Fuente == tienda)) Agregar(f.ImagenUrl);
        Agregar(mc.ImagenUrl);
        Agregar(hltb?.ImagenUrl);
        foreach (var f in filas.Where(f => f.Fuente == "Amazon")) Agregar(f.ImagenUrl);
        foreach (var f in filas) Agregar(f.ImagenUrl);

        // Si en esta corrida ninguna página dio portada, se conservan las anteriores
        if (portadas.Count == 0 && previo is not null)
        {
            foreach (var u in previo.Imagenes) Agregar(u);
            Agregar(previo.ImagenUrl);
        }
        return portadas;
    }

    private void MarcarHilo() => _hilosUsados.TryAdd(Environment.CurrentManagedThreadId, 0);

    private static string Recortar(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
