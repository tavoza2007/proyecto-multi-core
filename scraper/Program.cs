using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using PrecioPixel;
using PrecioPixel.Config;
using PrecioPixel.Fuentes;
using PrecioPixel.Modelos;
using PrecioPixel.Motor;
using PrecioPixel.Red;
using PrecioPixel.Repositorio;
using PrecioPixel.Tiendas;
using PrecioPixel.Util;

// ════════════════════════════════════════════════════════════════════════════
//  PrecioPixel · Scraper multicore de ofertas de videojuegos (solo web scraping de HTML, sin APIs)
//
//  Comandos:
//    scrape     [--local] [--secuencial] [--limite N] [--verbose] [--sin-previos]   ← corrida normal
//    benchmark  [--n 20] [--local]                                  ← secuencial vs paralelo, speedup (rápido)
//    rendimiento [--configs secuencial,1,2,4,8] [--salida web/data/rendimiento.json] [--limite N]
//                                                                    ← análisis de rendimiento completo (todos los juegos)
//    seed       [--archivo seed/juegos.json] [--sincronizar]        ← sube la lista de juegos a Firestore
//    add        --titulo "X" --plataformas pc,ps5 --precio 59.99 [--anio 2024] [--tipo juego]
//               [--formatos digital,fisico] [--genero "Acción"] [--steam 123]
//    remove     --id slug
//    list       [--local]
// ════════════════════════════════════════════════════════════════════════════

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var comando = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "scrape";
var argumentos = LeerArgumentos(args);
Log.Detallado = argumentos.ContainsKey("verbose");

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables(prefix: "PP_") // ej. PP_Firebase__ProjectId, PP_Paralelismo__MaxJuegosEnParalelo
    .Build();
var opt = config.Get<Opciones>() ?? new Opciones();

using var cancelacion = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelacion.Cancel(); Log.Advertencia("Cancelando…"); };
var ct = cancelacion.Token;

try
{
    var repo = CrearRepositorio(argumentos.ContainsKey("local"), opt);
    Log.Titulo($"PrecioPixel · repo: {repo.Descripcion} · núcleos: {Environment.ProcessorCount}");

    return comando switch
    {
        "scrape" => await ScrapeAsync(repo, opt, argumentos, ct),
        "benchmark" => await BenchmarkAsync(repo, opt, argumentos, ct),
        "rendimiento" => await RendimientoAsync(repo, opt, argumentos, ct),
        "seed" => await SeedAsync(repo, argumentos, ct),
        "add" => await AgregarAsync(repo, argumentos, ct),
        "remove" => await QuitarAsync(repo, argumentos, ct),
        "list" => await ListarAsync(repo, ct),
        _ => Ayuda(),
    };
}
catch (OperationCanceledException)
{
    Log.Advertencia("Ejecución cancelada. No se guardó nada.");
    return 130;
}
catch (Exception ex)
{
    Log.Error($"Error fatal: {ex.Message}");
    Log.Debug(ex.ToString());
    return 1;
}

// ─────────────────────────────── comandos ───────────────────────────────

static async Task<int> ScrapeAsync(IRepositorio repo, Opciones opt, Dictionary<string, string> a, CancellationToken ct)
{
    var paralelo = !a.ContainsKey("secuencial");
    var juegos = await repo.ObtenerJuegosAsync(soloActivos: true, ct);
    if (a.TryGetValue("limite", out var lim) && int.TryParse(lim, out var n)) juegos = juegos.Take(n).ToList();
    if (juegos.Count == 0) { Log.Error("El repo no tiene juegos activos. Usa el comando seed o add."); return 1; }

    using var http = new ClienteHttpResiliente(opt.Red, opt.Paralelismo.MaxPeticionesPorHost);
    var (motor, urls, hltb) = CrearMotor(http, opt);

    // 1) Validar conexión ANTES de tocar nada
    Log.Titulo("Verificando conectividad con los sitios…");
    var conectividad = await http.VerificarConectividadAsync(urls, ct);
    if (!conectividad.Values.Any(ok => ok))
    {
        Log.Error("No hay conexión con ningún sitio. Se aborta la corrida y se conservan los datos publicados.");
        return 2;
    }

    // 2) Resultados previos: para el mínimo histórico y para conservar precios de tiendas que fallen.
    //    Con --sin-previos se empieza de cero (útil si la corrida anterior tenía datos malos).
    var previos = a.ContainsKey("sin-previos")
        ? new Dictionary<string, ResultadoJuego>()
        : await repo.ObtenerResultadosAsync(ct);

    Log.Titulo($"Scraping de {juegos.Count} juegos · modo {(paralelo ? $"PARALELO ({opt.Paralelismo.JuegosEnParaleloEfectivo} juegos a la vez)" : "SECUENCIAL")}");
    var (resultados, resumen) = await motor.EjecutarAsync(juegos, previos, paralelo, ct);

    ImprimirResumen(resumen);

    // 3) Nunca publicar una corrida vacía (p. ej. se cayó internet a la mitad)
    if (resumen.JuegosSinDatos == resumen.Juegos)
    {
        Log.Error("Ningún juego obtuvo precios. No se sobrescriben los datos publicados.");
        return 3;
    }

    await repo.GuardarResultadosAsync(resultados, resumen, ct);
    Log.Ok($"Resultados guardados en {repo.Descripcion}");

    // 4) El REPO se completa solo con lo que aprendió esta corrida (fechas de lanzamiento, páginas de HowLongToBeat)
    await CompletarRepoAsync(repo, juegos, resultados, hltb, ct);

    if (!string.IsNullOrWhiteSpace(opt.Salida.ExportarJson) && repo is not JsonRepositorio)
    {
        var ruta = Rutas.Resolver(opt.Salida.ExportarJson);
        await ExportWeb.EscribirAsync(ruta, resultados, resumen, demo: false, ct);
        Log.Ok($"Respaldo JSON exportado en {ruta}");
    }
    return 0;
}

static async Task<int> BenchmarkAsync(IRepositorio repo, Opciones opt, Dictionary<string, string> a, CancellationToken ct)
{
    var n = a.TryGetValue("n", out var s) && int.TryParse(s, out var v) ? v : 20;
    var juegos = (await repo.ObtenerJuegosAsync(true, ct)).Take(n).ToList();
    var vacio = new Dictionary<string, ResultadoJuego>();

    using var http = new ClienteHttpResiliente(opt.Red, opt.Paralelismo.MaxPeticionesPorHost);
    var (motor, _, _) = CrearMotor(http, opt);

    Log.Titulo($"BENCHMARK con {juegos.Count} juegos · {Environment.ProcessorCount} núcleos lógicos");
    Log.Info("Calentamiento (1 juego) para abrir conexiones y descubrir endpoints…");
    await motor.EjecutarAsync(juegos.Take(1).ToList(), vacio, paralelo: true, ct);

    Log.Titulo("── Corrida SECUENCIAL ──");
    var (_, sec) = await motor.EjecutarAsync(juegos, vacio, paralelo: false, ct);
    Log.Titulo("── Corrida PARALELA (3 niveles) ──");
    var (_, par) = await motor.EjecutarAsync(juegos, vacio, paralelo: true, ct);

    var speedup = sec.DuracionSegundos / Math.Max(0.001, par.DuracionSegundos);
    Console.WriteLine();
    Console.WriteLine("┌──────────────┬────────────┬────────────┬──────────┬────────────┐");
    Console.WriteLine("│ Modo         │ Tiempo (s) │ Peticiones │ Hilos    │ Juegos OK  │");
    Console.WriteLine("├──────────────┼────────────┼────────────┼──────────┼────────────┤");
    Console.WriteLine($"│ Secuencial   │ {sec.DuracionSegundos,10:0.00} │ {sec.PeticionesHttp,10} │ {sec.HilosDistintos,8} │ {sec.JuegosOk,4}/{sec.Juegos,-5} │");
    Console.WriteLine($"│ Paralelo     │ {par.DuracionSegundos,10:0.00} │ {par.PeticionesHttp,10} │ {par.HilosDistintos,8} │ {par.JuegosOk,4}/{par.Juegos,-5} │");
    Console.WriteLine("└──────────────┴────────────┴────────────┴──────────┴────────────┘");
    Log.Ok($"Speedup = {speedup:0.00}x   ·   Eficiencia por núcleo = {speedup / Environment.ProcessorCount:P0}");
    Log.Info("Nota: el scraping es mayormente espera de red, por eso el speedup puede superar el número de núcleos.");
    return 0;
}

// Análisis de rendimiento: los MISMOS juegos (todos los activos del REPO) con distintas configuraciones.
//   secuencial → los tres niveles uno por uno (base del speedup)
//   N          → N juegos a la vez en el nivel 1 (los niveles 2 y 3 siempre en paralelo)
// Cada configuración usa un cliente HTTP nuevo y un calentamiento que no se mide. No se guarda nada en el REPO.
static async Task<int> RendimientoAsync(IRepositorio repo, Opciones opt, Dictionary<string, string> a, CancellationToken ct)
{
    var preparacion = Stopwatch.StartNew();
    var juegos = await repo.ObtenerJuegosAsync(soloActivos: true, ct);
    if (a.TryGetValue("limite", out var lim) && int.TryParse(lim, out var n)) juegos = juegos.Take(n).ToList();
    preparacion.Stop();
    if (juegos.Count == 0) { Log.Error("El repo no tiene juegos activos."); return 1; }

    var maximo = Environment.ProcessorCount * 2;
    var configs = (a.GetValueOrDefault("configs") ?? $"secuencial,1,2,4,{maximo}")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.Equals("max", StringComparison.OrdinalIgnoreCase) ? maximo.ToString(CultureInfo.InvariantCulture) : c.ToLowerInvariant())
        .Distinct()
        .ToList();
    var invalidas = configs.Where(c => c != "secuencial" && !(int.TryParse(c, out var h) && h > 0)).ToList();
    if (invalidas.Count > 0) { Log.Error($"Configuraciones no válidas: {string.Join(", ", invalidas)}. Usa secuencial, max o números."); return 1; }

    var salida = Rutas.Resolver(a.GetValueOrDefault("salida") ?? "web/data/rendimiento.json");
    var calentamiento = int.TryParse(a.GetValueOrDefault("calentamiento"), out var w) ? Math.Max(0, w) : 3;
    var maquina = (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "GitHub Actions · " : "") +
                  $"{RuntimeInformation.OSDescription.Trim()} · {Environment.ProcessorCount} núcleos lógicos";
    var vacio = new Dictionary<string, ResultadoJuego>();
    var archivo = new ArchivoRendimiento();

    Log.Titulo($"ANÁLISIS DE RENDIMIENTO · {juegos.Count} juegos · {maquina}");
    Log.Info($"Configuraciones: {string.Join(", ", configs)} · calentamiento: {calentamiento} juegos (no se mide)");

    foreach (var config in configs)
    {
        var secuencial = config == "secuencial";
        var hilos = secuencial ? 1 : int.Parse(config, CultureInfo.InvariantCulture);
        opt.Paralelismo.MaxJuegosEnParalelo = hilos;

        // Cliente HTTP nuevo por configuración: sin conexiones abiertas ni circuitos de la corrida anterior
        using var http = new ClienteHttpResiliente(opt.Red, opt.Paralelismo.MaxPeticionesPorHost);
        var (motor, _, _) = CrearMotor(http, opt);

        if (calentamiento > 0)
            await motor.EjecutarAsync(juegos.Take(calentamiento).ToList(), vacio, paralelo: !secuencial, ct);

        Log.Titulo(secuencial ? "──────── SECUENCIAL ────────" : $"──────── PARALELO · {hilos} juegos a la vez ────────");
        var (_, r) = await motor.EjecutarAsync(juegos, vacio, paralelo: !secuencial, ct);
        ImprimirResumen(r);

        archivo.Mediciones.Add(new Medicion
        {
            Config = config,
            Modo = secuencial ? "secuencial" : "paralelo",
            Hilos = hilos,
            Nucleos = Environment.ProcessorCount,
            Juegos = juegos.Count,
            Segundos = r.DuracionSegundos,
            SegundosPreparacion = Math.Round(preparacion.Elapsed.TotalSeconds, 2),
            Peticiones = r.PeticionesHttp,
            Reintentos = r.Reintentos,
            HilosDistintos = r.HilosDistintos,
            JuegosOk = r.JuegosOk,
            JuegosParciales = r.JuegosParciales,
            JuegosSinDatos = r.JuegosSinDatos,
            FuentesCaidas = r.FuentesCaidas,
            Maquina = maquina,
            Fecha = DateTime.UtcNow,
        });
        await archivo.EscribirAsync(salida, ct);
    }

    // Tabla final: speedup = tiempo secuencial ÷ tiempo paralelo · eficiencia = speedup ÷ hilos
    var tSecuencial = archivo.Mediciones.FirstOrDefault(m => m.Modo == "secuencial")?.Segundos;
    Console.WriteLine();
    Console.WriteLine("┌──────────────┬────────────┬──────────┬────────────┬────────────┐");
    Console.WriteLine("│ Configuración│ Tiempo (s) │ Speedup  │ Eficiencia │ Peticiones │");
    Console.WriteLine("├──────────────┼────────────┼──────────┼────────────┼────────────┤");
    foreach (var m in archivo.Mediciones)
    {
        var speedup = tSecuencial is { } ts && m.Segundos > 0 ? ts / m.Segundos : (double?)null;
        var nombre = m.Modo == "secuencial" ? "Secuencial" : $"{m.Hilos} hilos";
        var txtSpeedup = speedup is { } s1 ? $"{s1,7:0.00}x" : "     –  ";
        var txtEficiencia = speedup is { } s2 ? $"{s2 / m.Hilos,9:P0}" : "      –  ";
        Console.WriteLine($"│ {nombre,-12} │ {m.Segundos,10:0.0} │ {txtSpeedup} │ {txtEficiencia}  │ {m.Peticiones,10} │");
    }
    Console.WriteLine("└──────────────┴────────────┴──────────┴────────────┴────────────┘");
    Log.Ok($"Mediciones guardadas en {salida}");
    return 0;
}

static async Task<int> SeedAsync(IRepositorio repo, Dictionary<string, string> a, CancellationToken ct)
{
    var archivo = Rutas.Resolver(a.GetValueOrDefault("archivo") ?? "seed/juegos.json");
    var juegos = await new JsonRepositorio(archivo, "").ObtenerJuegosAsync(false, ct);
    var repetidos = juegos.GroupBy(j => j.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    if (repetidos.Count > 0) { Log.Error("Ids repetidos: " + string.Join(", ", repetidos)); return 1; }

    await repo.GuardarJuegosAsync(juegos, ct);
    Log.Ok($"{juegos.Count} juegos cargados en {repo.Descripcion}");

    // --sincronizar: los juegos que ya no están en el archivo se desactivan (salen de la web en la próxima corrida)
    if (a.ContainsKey("sincronizar"))
    {
        var ids = juegos.Select(j => j.Id).ToHashSet();
        var sobrantes = (await repo.ObtenerJuegosAsync(soloActivos: true, ct)).Where(j => !ids.Contains(j.Id)).ToList();
        foreach (var j in sobrantes)
        {
            await repo.DesactivarJuegoAsync(j.Id, ct);
            Log.Info($"Desactivado (ya no está en {Path.GetFileName(archivo)}): {j.Id}");
        }
    }
    return 0;
}

static async Task<int> AgregarAsync(IRepositorio repo, Dictionary<string, string> a, CancellationToken ct)
{
    if (!a.TryGetValue("titulo", out var titulo) || !a.TryGetValue("plataformas", out var plats))
    {
        Log.Error("Uso: add --titulo \"Hades\" --plataformas pc,switch --precio 24.99 [--anio 2020] [--tipo juego] [--formatos digital,fisico] [--genero Roguelike] [--steam 1145360]");
        return 1;
    }
    var juego = new Juego
    {
        Id = a.GetValueOrDefault("id") ?? Texto.Slug(titulo),
        Titulo = titulo,
        Plataformas = plats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        PrecioReferencia = double.TryParse(a.GetValueOrDefault("precio"), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0,
        Anio = int.TryParse(a.GetValueOrDefault("anio"), out var anio) ? anio : DateTime.UtcNow.Year,
        Tipo = a.GetValueOrDefault("tipo") ?? "juego",
        Formatos = (a.GetValueOrDefault("formatos") ?? "digital").Split(',', StringSplitOptions.TrimEntries).ToList(),
        Genero = a.GetValueOrDefault("genero") ?? "",
        SteamAppId = long.TryParse(a.GetValueOrDefault("steam"), out var st) ? st : null,
        Activo = true,
    };
    var invalidas = juego.Plataformas.Where(x => !Plataformas.Todas.ContainsKey(x)).ToList();
    if (invalidas.Count > 0)
    {
        Log.Error($"Plataformas no válidas: {string.Join(", ", invalidas)}. Válidas: {string.Join(", ", Plataformas.Todas.Keys)}");
        return 1;
    }
    await repo.GuardarJuegosAsync(new[] { juego }, ct);
    Log.Ok($"Agregado/actualizado: {juego.Id}");
    return 0;
}

static async Task<int> QuitarAsync(IRepositorio repo, Dictionary<string, string> a, CancellationToken ct)
{
    if (!a.TryGetValue("id", out var id)) { Log.Error("Uso: remove --id slug-del-juego"); return 1; }
    await repo.DesactivarJuegoAsync(id, ct);
    Log.Ok($"Desactivado: {id} (desaparece de la web en la próxima corrida)");
    return 0;
}

static async Task<int> ListarAsync(IRepositorio repo, CancellationToken ct)
{
    var juegos = await repo.ObtenerJuegosAsync(false, ct);
    foreach (var j in juegos)
        Console.WriteLine($"{(j.Activo ? "●" : "○")} {j.Id,-45} {string.Join(",", j.Plataformas),-28} ${j.PrecioReferencia,6:0.00}  {j.Anio}");
    Log.Info($"{juegos.Count(j => j.Activo)} activos de {juegos.Count}");
    return 0;
}

static int Ayuda()
{
    Console.WriteLine("Comandos: scrape | benchmark | rendimiento | seed | add | remove | list   (opción --local para usar JSON sin Firebase)");
    return 1;
}

// ─────────────────────────────── armado ───────────────────────────────

static IRepositorio CrearRepositorio(bool local, Opciones opt)
{
    var hayFirebase = !string.IsNullOrWhiteSpace(opt.Firebase.ProjectId) ||
                      !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID"));
    if (local || !hayFirebase)
    {
        if (!local) Log.Advertencia("Firebase no está configurado → se usa el repo JSON local.");
        return new JsonRepositorio(Rutas.Resolver("seed/juegos.json"), Rutas.Resolver(opt.Salida.ExportarJson is { Length: > 0 } r ? r : "web/data/deals.json"));
    }
    return new FirestoreRepositorio(opt.Firebase);
}

static (MotorScraping motor, List<string> urls, HowLongToBeatScraper? hltb) CrearMotor(ClienteHttpResiliente http, Opciones opt)
{
    var buscador = new BuscadorWeb(http);
    var tiendas = new List<ITiendaScraper>();
    if (opt.Tiendas.Steam.Habilitada) tiendas.Add(new SteamScraper(http, opt));
    if (opt.Tiendas.Gog.Habilitada) tiendas.Add(new GogScraper(http, buscador, opt));
    if (opt.Tiendas.NintendoEshop.Habilitada) tiendas.Add(new NintendoEshopScraper(http, buscador, opt));
    if (opt.Tiendas.PlayStationStore.Habilitada) tiendas.Add(new PlayStationStoreScraper(http, buscador, opt));
    if (opt.Tiendas.Amazon.Habilitada) tiendas.Add(new AmazonScraper(http, opt));

    var mc = opt.Fuentes.Metacritic.Habilitada ? new MetacriticScraper(http, buscador) : null;
    var hltb = opt.Fuentes.HowLongToBeat.Habilitada ? new HowLongToBeatScraper(http, buscador, new WikidataScraper(http), opt) : null;

    var urls = tiendas.Select(t => t.UrlBase).ToList();
    if (mc is not null) urls.Add(mc.UrlBase);
    if (hltb is not null) urls.Add(hltb.UrlBase);
    urls.Add("https://html.duckduckgo.com/");

    return (new MotorScraping(tiendas, mc, hltb, http, opt), urls, hltb);
}

// Completa el REPO con lo que aprendió la corrida, solo en los campos vacíos:
//   • fechaLanzamiento, cuando la fecha encontrada coincide con el año del juego en el repo;
//   • urlsTienda.hltb, la página de HowLongToBeat encontrada en Wikidata (o con el buscador y confirmada).
// Las siguientes corridas van directo a esas páginas. Si esto falla no pasa nada: los resultados ya se guardaron.
static async Task CompletarRepoAsync(IRepositorio repo, List<Juego> juegos, List<ResultadoJuego> resultados,
                                     HowLongToBeatScraper? hltb, CancellationToken ct)
{
    var resultadoDe = resultados.ToDictionary(x => x.Id);
    var cambios = new List<CambioJuego>();
    foreach (var juego in juegos)
    {
        string? fecha = null, urlHltb = null;
        if (string.IsNullOrWhiteSpace(juego.FechaLanzamiento) && resultadoDe.TryGetValue(juego.Id, out var res) &&
            Fechas.Normalizar(res.FechaLanzamiento) is { } encontrada && Fechas.Anio(encontrada) == juego.Anio)
            fecha = encontrada;
        if (hltb is not null && juego.Url("hltb") is null && hltb.UrlsDescubiertas.TryGetValue(juego.Id, out var url))
            urlHltb = url;
        if (fecha is not null || urlHltb is not null) cambios.Add(new CambioJuego(juego.Id, fecha, urlHltb));
    }
    if (cambios.Count == 0) return;

    try
    {
        await repo.ActualizarJuegosAsync(cambios, ct);
        Log.Ok($"REPO completado: {cambios.Count(c => c.FechaLanzamiento is not null)} fechas de lanzamiento y " +
               $"{cambios.Count(c => c.UrlHltb is not null)} páginas de HowLongToBeat nuevas");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Log.Advertencia($"No se pudo completar el REPO (los resultados sí quedaron guardados): {ex.Message}");
    }
}

static void ImprimirResumen(ResumenEjecucion r)
{
    Console.WriteLine();
    Log.Titulo("════════════ RESUMEN ════════════");
    Log.Info($"Modo: {r.Modo} · {r.Juegos} juegos en {r.DuracionSegundos:0.0} s ({r.Juegos / Math.Max(0.1, r.DuracionSegundos):0.0} juegos/s)");
    Log.Info($"OK: {r.JuegosOk} · Parciales: {r.JuegosParciales} · Sin datos: {r.JuegosSinDatos}");
    Log.Info($"Peticiones HTTP: {r.PeticionesHttp} · Reintentos: {r.Reintentos}");
    Log.Info($"Hilos distintos usados: {r.HilosDistintos} · Núcleos: {r.Nucleos} · Juegos simultáneos: {r.MaxJuegosEnParalelo}");
    if (r.FuentesCaidas.Count > 0) Log.Advertencia("Fallas por fuente: " + string.Join(", ", r.FuentesCaidas));
}

static Dictionary<string, string> LeerArgumentos(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var clave = args[i][2..];
        var valor = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        d[clave] = valor;
    }
    return d;
}
