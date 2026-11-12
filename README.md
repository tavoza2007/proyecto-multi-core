# PrecioPixel 🏷️

Comparador de precios de videojuegos con **web scraping multicore en C# (.NET 8)**, repo en **Firebase Firestore** y web pública en **Vercel o Netlify** que se actualiza sola.

**No usa APIs**: todos los datos se obtienen descargando las páginas HTML de cada sitio (como las ve un navegador) y extrayendo la información con selectores CSS (AngleSharp), etiquetas `meta`, datos estructurados schema.org (JSON-LD) y el estado que las páginas traen incrustado.

Implementa el *Modelo de referencia – Programación Multicore v1.7*:

| Requisito del modelo | Dónde está |
|---|---|
| Repo dinámico de ≥ 200 juegos, no "quemado" | Colección `juegos` en Firestore (260 juegos en `seed/juegos.json`), comandos `seed`, `add`, `remove`, `list` |
| Parámetros del repo: consolas, fechas, precio de referencia, otros | `plataformas`, `anio` (+ fecha exacta tomada de la tienda), `precioReferencia`, `tipo`, `formatos`, `genero`, `steamAppId`, `terminoBusqueda`, `metacriticSlug`, `activo` |
| **1er nivel de paralelismo**: scraping para cada juego | `Motor/MotorScraping.cs` → `Parallel.ForEachAsync` |
| **2do nivel**: consulta paralela de los sitios (precios ‖ Metacritic ‖ HLTB) | `ProcesarJuegoAsync` → `Task.WhenAll` |
| **3er nivel**: verificar precios en las distintas tiendas | `ObtenerPreciosAsync` → `Task.WhenAll` sobre Steam, GOG, eShop, PS Store, Amazon |
| Precio en al menos dos sitios principales | Cada juego consulta ≥ 2 tiendas según su plataforma (ver tabla abajo) |
| Indicador de oferta y cómo saberlo | `Motor/DetectorOfertas.cs` (dos criterios, ver abajo) |
| Score de Metacritic por consola del repo | `Fuentes/MetacriticScraper.cs` (una consulta por consola, en paralelo) |
| Tiempo de How Long To Beat | `Fuentes/HowLongToBeatScraper.cs` |
| Validar problemas de conexión y garantizar datos actualizados | `Red/ClienteHttpResiliente.cs` + reglas de "no publicar vacío" (ver abajo) |
| Web pública (no local), actualización recurrente | `web/` en Vercel/Netlify + GitHub Actions cada 6 h + `onSnapshot` en vivo |
| Filtros: tipo, plataforma, formato, tienda | Barra lateral de la web (con conteos) |
| Orden: nombre, precio, % descuento, Metacritic | Selector "Ordenar por" |
| Tarjeta: imagen, título, mejor precio, regular tachado, %, consolas, Metacritic | `tarjeta()` en `web/app.js` |
| Detalle: nombre, imagen, precios ordenados con link, precio sugerido, score, HLTB completo | Diálogo de detalle (`htmlDetalle()`), con enlace directo `#juego/slug` |

---

## Arquitectura

```
                 ┌──────────────── GitHub Actions (cron cada 6 h) ────────────────┐
                 │                                                                 │
 Firestore       │   dotnet run -- scrape                                          │
 juegos/{slug} ──┼─► NIVEL 1  Parallel.ForEachAsync(juegos, núcleos×2)             │
                 │      └─► NIVEL 2  Task.WhenAll( precios , Metacritic , HLTB )   │
                 │             └─► NIVEL 3  Task.WhenAll( Steam , GOG , eShop ,    │
                 │                            PS Store , Amazon )                  │
                 │   ClienteHttpResiliente: timeout · reintentos · circuit breaker │
                 └───────────────────────────────┬─────────────────────────────────┘
                                                 ▼
                        Firestore resultados/{slug} + meta/estado
                                                 │  onSnapshot (tiempo real)
                                                 ▼
                         Web estática en Vercel / Netlify (web/)
```

### Qué página se lee de cada sitio (todo es HTML, sin APIs)

| Sitio | Plataformas | Página que se descarga | Qué se extrae |
|---|---|---|---|
| Steam | PC | `store.steampowered.com/search/?term=…` y `/app/{id}/` | filas `a.search_result_row`; en la ficha `.discount_final_price`, `.discount_original_price`, `.game_purchase_price`, Metascore, fecha, imagen |
| GOG | PC | `gog.com/en/game/{slug}` | precio final, precio base e imagen de la ficha (GOG cobra precios regionales según el país de la conexión) |
| Nintendo eShop | Switch, Switch 2 | `nintendo.com/us/store/products/{slug}-switch/` (o `-switch-2/`) | SKU del juego en el JSON-LD y, con ese SKU, su precio regular y final en el estado incrustado de la página; de la misma ficha sale la Nintendo Switch 2 Edition |
| PlayStation Store | PS4, PS5 | `playstation.com/en-us/games/{slug}/` (de ahí sale el `conceptId`) y luego `store.playstation.com/en-us/concept/{id}` | nombre, plataformas y ofertas de la caja de compra (atributos `data-qa`), precio tachado, imagen del JSON-LD |
| Amazon | consolas (físico) | `amazon.com/s?k=…&i=videogames` | resultados `s-search-result`: título, `.a-price`, precio tachado, imagen |
| Metacritic | por consola | `metacritic.com/game/{slug}/critic-reviews/?platform=…` | Metascore de cada consola del repo |
| HowLongToBeat | — | `howlongtobeat.com/game/{id}` | historia principal, principal + extras, completista, todos los estilos |

Cuando la dirección de una página no se puede deducir del título (HowLongToBeat usa números, o un slug no coincide), se hace scraping de la página de resultados de un buscador (versión HTML de DuckDuckGo, o Bing de respaldo) y se abre el primer resultado del sitio. También se puede fijar la URL exacta en el repo con el campo `urlsTienda` (claves `steam`, `gog`, `nintendo`, `nintendo-switch2`, `playstation`, `metacritic`, `hltb`).

Cada juego se consulta en al menos dos sitios: PC → Steam + GOG; Switch → eShop + Amazon; PlayStation → PS Store + Amazon.

### Moneda: siempre dólares

Varias tiendas cambian la moneda o los precios según el país desde donde te conectas (Amazon muestra colones en Costa Rica, GOG muestra precios regionales). El scraper envía las mismas cookies de preferencia que guarda el navegador al elegir Estados Unidos y USD, y además descarta cualquier precio que venga en otra moneda o que sea más de 4 veces el precio de referencia del repo. Steam, PlayStation Store y Nintendo se consultan en sus versiones de EE. UU. (`cc=us`, `/en-us/`, `/us/`).

### Portadas de los juegos

La portada de cada juego también sale del scraping de las mismas páginas: la imagen de la ficha en Steam, Nintendo eShop, PlayStation Store o GOG; la portada que muestran Metacritic y HowLongToBeat (ahí tienen portada todos los juegos); y la foto de la caja en Amazon (en tamaño completo). Se guardan todas en `imagenes`, en ese orden. La web muestra la primera y, si no carga, prueba la siguiente. Las portadas horizontales llenan el marco de la tarjeta; las verticales (cajas) se muestran completas sobre un fondo desenfocado de la misma imagen.

### ¿Cómo sabemos si está en oferta?

1. **Criterio "tienda"**: la tienda muestra un precio regular mayor que el actual → descuento = (regular − actual) / regular.
2. **Criterio "referencia"**: si la tienda no marca descuento pero el precio está al menos **10 %** (configurable) por debajo del `precioReferencia` del repo, también es oferta, y se compara contra ese precio.

Además se guarda el **mínimo histórico** entre corridas; la tarjeta muestra "Mínimo histórico" cuando la oferta actual lo iguala.

### Problemas de conexión y datos actualizados

- Antes de empezar se hace un **chequeo de conectividad en paralelo** a todos los sitios. Si no hay conexión con ninguno, la corrida se aborta y se conservan los datos publicados.
- Cada petición tiene **timeout**, **3 reintentos con backoff exponencial + jitter**, respeta `Retry-After` (429) y detecta **CAPTCHA**.
- **Circuit breaker por sitio**: si un sitio falla 6 veces seguidas se deja de consultar 90 s para no perder tiempo.
- **Límite de peticiones simultáneas por sitio** (4) con `SemaphoreSlim`, para no ser bloqueados.
- Si una tienda falla para un juego, se conserva su **último precio conocido marcado como "no vigente"** (la web lo muestra como "Precio sin confirmar" con su antigüedad). Lo mismo con Metacritic y HLTB.
- Si **ningún** juego obtuvo precios, no se sobrescribe nada.
- Cada resultado guarda `actualizadoEn`, `estado` (ok / parcial / sin-datos) y `errores`; `meta/estado` guarda el resumen de la corrida que la web muestra como "Actualizado hace X".

---

## Puesta en marcha

### 0. Requisitos
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Una cuenta de Firebase (plan gratuito Spark alcanza)
- Una cuenta de GitHub y otra de Vercel o Netlify

### 1. Probar sin Firebase (modo local)
Lo más fácil en Windows: doble clic en `probar.bat`. Compila, hace el scraping de 15 juegos y guarda todo lo que muestra en `salida.txt`.

```bash
cd scraper
dotnet run -- list --local                 # ver el repo
dotnet run -- scrape --local --limite 10   # scraping real de 10 juegos → web/data/deals.json
```
Para ver la web: `cd web && python -m http.server 8080` y abrir http://localhost:8080.
Si quieres ver la web sin hacer scraping: `python tools/generar_demo.py` crea datos de demostración (la web muestra un aviso amarillo).

### 2. Crear Firebase
1. En https://console.firebase.google.com crea un proyecto.
2. **Firestore Database → Crear base de datos** (modo producción).
3. **Reglas**: pega el contenido de `firestore.rules` y publica.
4. **Configuración del proyecto → Cuentas de servicio → Generar nueva clave privada**. Se descarga un `.json` (¡no lo subas a GitHub!).
5. **Configuración del proyecto → Tus apps → Agregar app web**. Copia el objeto de configuración en `web/firebase-config.js`.

### 3. Cargar el repo de juegos
Lo más fácil en Windows: doble clic en `subir-juegos.bat` (busca sola la clave en Documentos o Descargas, sube los juegos y, si quieres, hace el primer scraping). El resultado queda en `salida-firebase.txt`. Para hacerlo a mano:

```bash
# Linux/macOS
export FIREBASE_PROJECT_ID=tu-proyecto
export GOOGLE_APPLICATION_CREDENTIALS=/ruta/a/la-clave.json
# Windows PowerShell
$env:FIREBASE_PROJECT_ID="tu-proyecto"
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\ruta\a\la-clave.json"

cd scraper
dotnet run -- seed            # sube los 260 juegos de seed/juegos.json
dotnet run -- scrape          # primera corrida
```

Administrar la lista (también se puede editar directo en la consola de Firebase):
```bash
dotnet run -- add --titulo "Hades" --plataformas pc,switch --precio 24.99 --anio 2020 --genero Roguelike --formatos digital,fisico --steam 1145360
dotnet run -- remove --id hades          # lo desactiva; desaparece de la web en la próxima corrida
dotnet run -- list
```

### 4. Actualización recurrente (GitHub Actions)
1. Sube el proyecto a un repositorio de GitHub.
2. **Settings → Secrets and variables → Actions → New repository secret**:
   - `FIREBASE_PROJECT_ID` = id del proyecto
   - `FIREBASE_SERVICE_ACCOUNT` = pega el contenido completo del `.json`
3. En la pestaña **Actions** ejecuta "Scraper PrecioPixel" con **Run workflow** para probar.
4. La frecuencia está en `.github/workflows/scraper.yml` (`cron: "17 */6 * * *"` = cada 6 horas). Cámbiala a la que defina el equipo.

### 5. Publicar la web
**Vercel**: New Project → importa el repositorio → Deploy (el `vercel.json` ya indica que la carpeta publicada es `web`).
**Netlify**: Add new site → Import from Git → Deploy (el `netlify.toml` ya indica `publish = "web"`).

La página escucha Firestore con `onSnapshot`: cuando el scraper termina, los precios cambian en pantalla sin recargar.

---

## Medir el paralelismo (para la presentación)
```bash
dotnet run -- benchmark --n 20
```
Corre los mismos 20 juegos en modo **secuencial** y en modo **paralelo (3 niveles)** y muestra tiempo, peticiones, hilos distintos usados y el **speedup**. También se puede forzar: `dotnet run -- scrape --secuencial`.

El log muestra el hilo de cada línea (`h 12`, `h 37`…) para ver los niveles trabajando al mismo tiempo.

## Configuración (`scraper/appsettings.json`)
Todo se puede sobrescribir con variables de entorno con prefijo `PP_`, por ejemplo `PP_Paralelismo__MaxJuegosEnParalelo=32`.

| Clave | Para qué |
|---|---|
| `Paralelismo.MaxJuegosEnParalelo` | Juegos simultáneos (nivel 1). 0 = núcleos × 2 |
| `Paralelismo.MaxPeticionesPorHost` | Peticiones simultáneas a un mismo sitio |
| `Red.*` | Timeout, reintentos, backoff, circuit breaker |
| `Ofertas.UmbralContraReferenciaPct` | % bajo el precio de referencia para considerar oferta |
| `Ofertas.SimilitudMinimaTitulo` | Qué tan parecido debe ser el título encontrado (0–1) |
| `Tiendas.*.Habilitada` | Encender/apagar cada tienda (Steam, Gog, NintendoEshop, PlayStationStore, Amazon) |

## Estructura
```
scraper/                C# .NET 8
  Program.cs            comandos (scrape, benchmark, seed, add, remove, list)
  Motor/                MotorScraping (3 niveles) y DetectorOfertas
  Tiendas/              Steam, GOG, Nintendo eShop, PlayStation Store, Amazon
  Fuentes/              Metacritic, HowLongToBeat
  Red/                  cliente HTTP resiliente y buscador web (para encontrar páginas)
  Repositorio/          Firestore y JSON local
  Modelos/              Juego, ResultadoJuego, PrecioTienda…
seed/juegos.json        repo inicial (260 juegos)
web/                    sitio público (HTML + CSS + JS, sin frameworks)
tools/generar_demo.py   datos de demostración para la web
.github/workflows/      corrida programada
firestore.rules, vercel.json, netlify.toml
```

## Agregar otra tienda
Crea una clase en `Tiendas/` que implemente `ITiendaScraper` (`AplicaA` y `BuscarAsync`) y regístrala en `CrearMotor` de `Program.cs`. Entra automáticamente al nivel 3 de paralelismo, al chequeo de conectividad y a los filtros de la web.

## Limitaciones conocidas
Los sitios cambian su HTML sin aviso. El código está hecho para tolerarlo (varios selectores por dato, JSON-LD de respaldo, recorrido flexible del estado incrustado de cada página), pero si una fuente deja de responder:
- **Amazon** suele pedir CAPTCHA a servidores de nube; se detecta y se conserva el último precio. Se puede apagar en `appsettings.json`.
- **Buscadores**: Bing y DuckDuckGo pueden pedir verificación a las búsquedas automáticas. El programa lo detecta, no la resuelve y sigue sin ellos. Para los juegos que no encuentre, fija la URL en `urlsTienda`.
- **Nintendo eShop**: si un juego tiene una dirección distinta al título (ediciones, nombres largos), pon su URL en `urlsTienda.nintendo`.
- **HowLongToBeat** bloquea algunas conexiones (error 403 "IFW", incluso abriéndolo en un navegador normal). Si te pasa, los tiempos quedan vacíos en esa corrida; desde GitHub Actions (servidores en EE. UU.) puede funcionar, o fija la URL del juego en `urlsTienda.hltb`.
- Si una corrida anterior dejó datos malos, `dotnet run -- scrape --sin-previos` empieza de cero (no conserva precios viejos ni el mínimo histórico).
- Corre `dotnet run -- scrape --limite 5 --verbose` para ver el detalle de cada falla.
