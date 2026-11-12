using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using PrecioPixel.Config;

namespace PrecioPixel.Red;

/// <summary>La fuente está temporalmente deshabilitada por el circuit breaker o devolvió un error definitivo.</summary>
public sealed class FuenteNoDisponibleException(string mensaje, Exception? interna = null, int? codigoHttp = null)
    : Exception(mensaje, interna)
{
    /// <summary>Código HTTP cuando el sitio respondió (ej. 404 = el juego no existe en ese sitio).</summary>
    public int? CodigoHttp { get; } = codigoHttp;
}

/// <summary>El sitio respondió con un CAPTCHA / página anti-bots.</summary>
public sealed class BloqueoDetectadoException(string host) : Exception($"{host} pidió verificación anti-bots (CAPTCHA)");

/// <summary>
/// HttpClient compartido y "a prueba de fallas" para el web scraping:
///  • Timeout por petición.
///  • Reintentos con backoff exponencial + jitter ante errores de red, timeouts, 429 y 5xx.
///  • Respeta el encabezado Retry-After.
///  • Límite de peticiones simultáneas POR HOST (SemaphoreSlim) para no saturar ni ser bloqueados.
///  • Circuit breaker por host: si un sitio falla N veces seguidas se deja de consultar durante X segundos.
///  • Chequeo de conectividad previo a la corrida.
/// Es thread-safe: lo usan todos los hilos de los 3 niveles de paralelismo a la vez.
/// </summary>
public sealed class ClienteHttpResiliente : IDisposable
{
    private readonly HttpClient _http;
    private readonly RedOpciones _red;
    private readonly int _maxPorHost;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _limitesPorHost = new();
    private readonly ConcurrentDictionary<string, EstadoCircuito> _circuitos = new();

    private long _peticiones;
    private long _reintentos;
    public long Peticiones => Interlocked.Read(ref _peticiones);
    public long Reintentos => Interlocked.Read(ref _reintentos);

    private sealed class EstadoCircuito
    {
        public int FallosSeguidos;
        public DateTime AbiertoHasta = DateTime.MinValue;
        public readonly object Lock = new();
    }

    public ClienteHttpResiliente(RedOpciones red, int maxPorHost)
    {
        _red = red;
        _maxPorHost = Math.Max(1, maxPorHost);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = _maxPorHost * 2,
            // Sin cookies automáticas: algunos sitios guardan cookies con caracteres no ASCII
            // (ej. la ciudad "San José") que rompen las peticiones siguientes. Las cookies que sí
            // necesitamos (edad en Steam, moneda USD en Amazon/GOG) se envían a mano en cada petición.
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // el timeout lo controlamos por intento
        // Encabezados de un navegador real: algunos sitios rechazan (403) peticiones que no los traen
        var h = _http.DefaultRequestHeaders;
        h.UserAgent.ParseAdd(red.UserAgent);
        h.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        h.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        h.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        h.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        h.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        h.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        h.TryAddWithoutValidation("Sec-Fetch-User", "?1");
    }

    public Task<string> GetStringAsync(string url, CancellationToken ct, IDictionary<string, string>? headers = null) =>
        EnviarAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AgregarHeaders(req, headers);
            return req;
        }, ct);

    private static void AgregarHeaders(HttpRequestMessage req, IDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (k, v) in headers)
            if (!req.Headers.TryAddWithoutValidation(k, v)) // un encabezado propio reemplaza al predeterminado del mismo nombre
                req.Content?.Headers.TryAddWithoutValidation(k, v);
    }

    private async Task<string> EnviarAsync(Func<HttpRequestMessage> crearPeticion, CancellationToken ct)
    {
        using var muestra = crearPeticion();
        var host = muestra.RequestUri!.Host;
        var circuito = _circuitos.GetOrAdd(host, _ => new EstadoCircuito());

        lock (circuito.Lock)
        {
            if (DateTime.UtcNow < circuito.AbiertoHasta)
                throw new FuenteNoDisponibleException($"{host} deshabilitado temporalmente (circuit breaker abierto)");
        }

        var limite = _limitesPorHost.GetOrAdd(host, _ => new SemaphoreSlim(_maxPorHost, _maxPorHost));
        await limite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? ultimoError = null;

            for (var intento = 1; intento <= _red.Reintentos; intento++)
            {
                ct.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_red.TimeoutSegundos));

                TimeSpan? esperarAntesDeReintentar = null;
                try
                {
                    Interlocked.Increment(ref _peticiones);
                    using var req = crearPeticion();
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        var cuerpo = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        if (PareceCaptcha(cuerpo))
                            throw new BloqueoDetectadoException(host);
                        RegistrarExito(circuito);
                        return cuerpo;
                    }

                    var codigo = (int)resp.StatusCode;
                    if (codigo == 429 || codigo == 503 || codigo >= 500 || codigo == 408)
                    {
                        // Error transitorio: reintentar
                        ultimoError = new HttpRequestException($"HTTP {codigo} en {host}", null, resp.StatusCode);
                        esperarAntesDeReintentar = LeerRetryAfter(resp.Headers.RetryAfter);
                    }
                    else
                    {
                        // 4xx definitivo (404, 403...): no tiene sentido reintentar.
                        // Un 403 significa que el sitio bloquea la conexión: cuenta para el circuit breaker
                        // y así se deja de insistir con ese sitio durante un rato.
                        if (codigo is 401 or 403) RegistrarFallo(circuito, host);
                        throw new FuenteNoDisponibleException($"HTTP {codigo} en {req.RequestUri}", null, codigo);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    ultimoError = new TimeoutException($"Timeout de {_red.TimeoutSegundos}s en {host}");
                }
                catch (HttpRequestException ex) // DNS, conexión rechazada, TLS, conexión reiniciada...
                {
                    ultimoError = ex;
                }

                if (intento < _red.Reintentos)
                {
                    Interlocked.Increment(ref _reintentos);
                    var backoff = esperarAntesDeReintentar
                                  ?? TimeSpan.FromMilliseconds(_red.BackoffBaseMs * Math.Pow(2, intento - 1) + Random.Shared.Next(0, 400));
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
            }

            RegistrarFallo(circuito, host);
            throw new FuenteNoDisponibleException($"{host} no respondió tras {_red.Reintentos} intentos: {ultimoError?.Message}", ultimoError);
        }
        catch (BloqueoDetectadoException)
        {
            RegistrarFallo(circuito, host);
            throw;
        }
        finally
        {
            limite.Release();
        }
    }

    private static TimeSpan? LeerRetryAfter(RetryConditionHeaderValue? retry)
    {
        if (retry?.Delta is { } d) return d > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : d;
        if (retry?.Date is { } f)
        {
            var delta = f - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero) return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }
        return null;
    }

    private static bool PareceCaptcha(string html) =>
        html.Length < 20_000 &&
        (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("Robot Check", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("are you a human", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("anomaly.js", StringComparison.OrdinalIgnoreCase) ||          // DuckDuckGo
         html.Contains("confirm this search was made by a human", StringComparison.OrdinalIgnoreCase));

    private static void RegistrarExito(EstadoCircuito c)
    {
        lock (c.Lock) c.FallosSeguidos = 0;
    }

    private void RegistrarFallo(EstadoCircuito c, string host)
    {
        lock (c.Lock)
        {
            c.FallosSeguidos++;
            if (c.FallosSeguidos >= _red.FallosParaAbrirCircuito)
            {
                c.AbiertoHasta = DateTime.UtcNow.AddSeconds(_red.CircuitoAbiertoSegundos);
                c.FallosSeguidos = 0;
                Log.Advertencia($"Circuit breaker ABIERTO para {host} por {_red.CircuitoAbiertoSegundos}s");
            }
        }
    }

    /// <summary>Hosts cuyo circuito se abrió al menos una vez en la corrida (para el resumen).</summary>
    public IEnumerable<string> HostsConCircuitoAbierto =>
        _circuitos.Where(kv => kv.Value.AbiertoHasta > DateTime.MinValue).Select(kv => kv.Key);

    /// <summary>
    /// Verifica conectividad antes de empezar. Consulta todos los hosts EN PARALELO.
    /// Devuelve qué hosts respondieron. Si ninguno responde, no hay internet y la corrida se aborta
    /// (así nunca se sobrescriben datos buenos con datos vacíos).
    /// </summary>
    public async Task<Dictionary<string, bool>> VerificarConectividadAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var tareas = urls.Distinct().Select(async url =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                // Cualquier respuesta HTTP (incluso 403/404) significa que hay conexión con el host
                Log.Info($"  ✓ {new Uri(url).Host,-32} HTTP {(int)resp.StatusCode} en {sw.ElapsedMilliseconds} ms");
                return (url, ok: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                Log.Advertencia($"  ✗ {new Uri(url).Host,-32} sin conexión ({ex.GetType().Name})");
                return (url, ok: false);
            }
        });
        var res = await Task.WhenAll(tareas).ConfigureAwait(false);
        return res.ToDictionary(r => r.url, r => r.ok);
    }

    public void Dispose() => _http.Dispose();
}
