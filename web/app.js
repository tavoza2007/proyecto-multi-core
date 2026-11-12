// ═══════════════ PrecioPixel · app ═══════════════
// Lee los resultados del scraper desde Firestore (en vivo) o, si Firebase no está
// configurado, desde data/deals.json. Filtra, ordena, pagina y muestra el detalle.

const PLATAFORMAS = {
  switch2: "Switch 2", switch: "Switch", ps5: "PS5", ps4: "PS4",
  "xbox-series": "Xbox Series X|S", "xbox-one": "Xbox One", pc: "PC", "3ds": "3DS",
};
const PLATAFORMAS_CORTAS = { ...PLATAFORMAS, "xbox-series": "Series X|S", "xbox-one": "Xbox One" };
const TIPOS = { juego: "Juego base", "juego+dlc": "Juego + DLC", remaster: "Remaster o remake", coleccion: "Colección", otro: "Otro" };
const FORMATOS = { digital: "Digital", fisico: "Físico" };
const ORDEN_TIENDAS = ["Steam", "PlayStation Store", "Nintendo eShop", "Amazon"];
const POR_PAGINA = 24;
const FIREBASE_VERSION = "10.12.2";
const REFRESCO_JSON_MS = 5 * 60 * 1000;

const estado = {
  juegos: [],
  meta: null,
  demo: false,
  modo: "cargando",            // vivo | json | error
  filtros: { tipo: new Set(), plataforma: new Set(), formato: new Set(), tienda: new Set() },
  soloOfertas: false,
  busqueda: "",
  orden: "metacritic",
  vista: "cuadricula",
  pagina: 1,
};

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];

// ─────────────────────────── utilidades ───────────────────────────
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const dinero = (n) => (n == null ? "—" : n === 0 ? "Gratis" : `$${n.toFixed(2)}`);
const normalizar = (s) => String(s ?? "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();

function fecha(v) {
  if (!v) return null;
  if (typeof v.toDate === "function") return v.toDate();   // Timestamp de Firestore
  if (v.seconds != null) return new Date(v.seconds * 1000);
  const d = new Date(v);
  return isNaN(d) ? null : d;
}

const rtf = new Intl.RelativeTimeFormat("es", { numeric: "auto" });
function haceCuanto(d) {
  if (!d) return "";
  const seg = (d - Date.now()) / 1000;
  const abs = Math.abs(seg);
  if (abs < 60) return "hace un momento";
  if (abs < 3600) return rtf.format(Math.round(seg / 60), "minute");
  if (abs < 86400) return rtf.format(Math.round(seg / 3600), "hour");
  return rtf.format(Math.round(seg / 86400), "day");
}

function claseMc(score) {
  if (score == null) return "mc--nd";
  return score >= 75 ? "mc--alto" : score >= 50 ? "mc--medio" : "mc--bajo";
}

// ─────────────────────────── portadas ───────────────────────────
// Cada juego trae varias portadas (Steam, eShop, PS Store, GOG, Metacritic, HowLongToBeat, Amazon).
// Se muestra la primera; si no carga se prueba la siguiente. Las portadas horizontales llenan el marco;
// las verticales (cajas) se muestran completas sobre un fondo desenfocado de la misma imagen.
const SIN_PORTADA = `<div class="portada__vacia" aria-hidden="true"><svg viewBox="0 0 48 32"><rect x="3" y="5" width="42" height="22" rx="11"/><path d="M14 12v8M10 16h8"/><circle cx="32" cy="14" r="1.8"/><circle cx="36" cy="18" r="1.8"/></svg></div>`;

function portadasDe(j) {
  const lista = [];
  for (const u of [j.imagenUrl, ...(j.imagenes ?? [])]) if (u && !lista.includes(u)) lista.push(u);
  return lista;
}

function portada(j) {
  const lista = portadasDe(j);
  if (!lista.length) return `<div class="portada portada--vacia">${SIN_PORTADA}</div>`;
  return `<div class="portada" data-portadas="${esc(JSON.stringify(lista))}" data-i="0">
    <img class="portada__fondo" src="${esc(lista[0])}" alt="" aria-hidden="true" loading="lazy" decoding="async" referrerpolicy="no-referrer">
    <img class="portada__img" src="${esc(lista[0])}" alt="Portada de ${esc(j.titulo)}" loading="lazy" decoding="async" referrerpolicy="no-referrer">
  </div>`;
}

// Si una portada no carga, se pasa a la siguiente; si ninguna carga, queda el marco vacío
document.addEventListener("error", (e) => {
  const img = e.target;
  if (!img.classList?.contains("portada__img")) return;
  const caja = img.closest(".portada");
  const lista = JSON.parse(caja.dataset.portadas || "[]");
  const i = Number(caja.dataset.i) + 1;
  if (i < lista.length) {
    caja.dataset.i = i;
    img.src = lista[i];
    caja.querySelector(".portada__fondo").src = lista[i];
  } else {
    caja.className = "portada portada--vacia";
    caja.innerHTML = SIN_PORTADA;
  }
}, true);

// Al cargar se mide la imagen: horizontal → llena el marco; vertical o cuadrada → completa con fondo desenfocado
document.addEventListener("load", (e) => {
  const img = e.target;
  if (!img.classList?.contains("portada__img") || !img.naturalWidth) return;
  img.closest(".portada").dataset.forma = img.naturalWidth / img.naturalHeight >= 1.45 ? "horizontal" : "vertical";
}, true);

// ─────────────────────────── carga de datos ───────────────────────────
function normalizarJuego(j) {
  const precios = (j.precios ?? []).map((p) => ({ ...p, consultadoEn: fecha(p.consultadoEn), vigente: p.vigente !== false }));
  precios.sort((a, b) => a.precioActual - b.precioActual);
  return {
    ...j,
    precios,
    actualizadoEn: fecha(j.actualizadoEn),
    fechaMinimoHistorico: fecha(j.fechaMinimoHistorico),
    _busqueda: normalizar(j.titulo),
  };
}

async function cargarDesdeFirestore(config) {
  const base = `https://www.gstatic.com/firebasejs/${FIREBASE_VERSION}`;
  const { initializeApp } = await import(`${base}/firebase-app.js`);
  const { getFirestore, collection, doc, onSnapshot } = await import(`${base}/firebase-firestore.js`);
  const db = getFirestore(initializeApp(config));

  // onSnapshot: cuando el scraper escribe nuevos precios, la página se actualiza sola
  onSnapshot(collection(db, "resultados"), (snap) => {
    estado.juegos = snap.docs.map((d) => normalizarJuego({ id: d.id, ...d.data() }));
    estado.modo = "vivo";
    estado.demo = false;
    pintarTodo();
  }, (err) => fallo(`No se pudo leer Firestore: ${err.message}`));

  onSnapshot(doc(db, "meta", "estado"), (snap) => {
    estado.meta = snap.exists() ? snap.data() : null;
    pintarEstado();
  });
}

async function cargarDesdeJson() {
  const r = await fetch(`data/deals.json?t=${Date.now()}`, { cache: "no-store" });
  if (!r.ok) throw new Error(`data/deals.json respondió ${r.status}`);
  const data = await r.json();
  estado.juegos = (data.juegos ?? []).map(normalizarJuego);
  estado.meta = { ...(data.resumen ?? {}), fin: data.resumen?.fin ?? data.generadoEn };
  estado.demo = !!data.demo;
  estado.modo = "json";
  pintarTodo();
}

function fallo(msg) {
  estado.modo = "error";
  $("#estado-texto").textContent = msg;
  $("#estado").dataset.modo = "error";
}

async function iniciar() {
  leerUrl();
  pintarEsqueleto();
  const config = window.PRECIOPIXEL_FIREBASE;
  try {
    if (config?.projectId) {
      await cargarDesdeFirestore(config);
    } else {
      await cargarDesdeJson();
      setInterval(() => cargarDesdeJson().catch(() => {}), REFRESCO_JSON_MS);
    }
  } catch (e) {
    console.error(e);
    try { await cargarDesdeJson(); } catch { fallo("No se pudieron cargar los precios. Revisa tu conexión y recarga la página."); }
  }
  setInterval(pintarEstado, 30_000);
}

// ─────────────────────────── filtros ───────────────────────────
/** Calcula qué precios del juego pasan los filtros y cuál es el mejor. null = el juego no pasa. */
function evaluar(j, ignorar = null) {
  const f = estado.filtros;
  const usa = (dim) => dim !== ignorar && f[dim].size > 0;

  if (estado.busqueda && !j._busqueda.includes(estado.busqueda)) return null;
  if (usa("tipo") && !f.tipo.has(j.tipo ?? "otro")) return null;
  if (usa("plataforma") && !j.plataformas?.some((p) => f.plataforma.has(p))) return null;

  let filas = j.precios;
  if (usa("plataforma")) filas = filas.filter((p) => f.plataforma.has(p.plataforma));
  if (usa("formato")) filas = filas.filter((p) => f.formato.has(p.formato));
  if (usa("tienda")) filas = filas.filter((p) => f.tienda.has(p.tienda));

  const filtraFilas = usa("formato") || usa("tienda");
  if (filtraFilas && filas.length === 0) {
    // Sin precios todavía: el formato se juzga por lo que dice el repo
    if (j.precios.length > 0 || usa("tienda")) return null;
    if (usa("formato") && !j.formatos?.some((x) => f.formato.has(x))) return null;
  }

  const mejor = filas.find((p) => p.vigente) ?? filas[0] ?? null;
  const vista = {
    filas,
    mejor,
    precio: mejor?.precioActual ?? null,
    regular: mejor ? (mejor.motivoOferta === "referencia" ? j.precioReferencia : mejor.precioRegular) : null,
    descuento: mejor?.enOferta ? mejor.descuentoPct : 0,
    enOferta: !!mejor?.enOferta,
  };
  if (estado.soloOfertas && ignorar !== "ofertas" && !vista.enOferta) return null;
  return vista;
}

const ORDENES = {
  metacritic: (a, b) => (b.j.metacritic?.score ?? -1) - (a.j.metacritic?.score ?? -1) || a.j.titulo.localeCompare(b.j.titulo),
  descuento: (a, b) => b.v.descuento - a.v.descuento || (a.v.precio ?? 1e9) - (b.v.precio ?? 1e9),
  "precio-asc": (a, b) => (a.v.precio ?? 1e9) - (b.v.precio ?? 1e9) || a.j.titulo.localeCompare(b.j.titulo),
  "precio-desc": (a, b) => (b.v.precio ?? -1) - (a.v.precio ?? -1) || a.j.titulo.localeCompare(b.j.titulo),
  "nombre-asc": (a, b) => a.j.titulo.localeCompare(b.j.titulo, "es"),
  "nombre-desc": (a, b) => b.j.titulo.localeCompare(a.j.titulo, "es"),
};

function resultados() {
  const lista = [];
  for (const j of estado.juegos) {
    const v = evaluar(j);
    if (v) lista.push({ j, v });
  }
  return lista.sort(ORDENES[estado.orden] ?? ORDENES.metacritic);
}

// ─────────────────────────── pintar ───────────────────────────
function pintarTodo() {
  pintarFacetas();
  pintarEstante();
  pintarEstado();
  abrirDesdeHash();
}

function pintarEstado() {
  const caja = $("#estado");
  caja.dataset.modo = estado.modo;
  $("#aviso-demo").hidden = !estado.demo;
  if (estado.modo === "error") return;
  const fin = fecha(estado.meta?.fin);
  const cuando = fin ? `Actualizado ${haceCuanto(fin)}` : "Sin fecha de actualización";
  $("#estado-texto").textContent = estado.modo === "vivo" ? `En vivo · ${cuando}` : cuando;
  if (fin) caja.title = fin.toLocaleString("es");
}

function pintarEsqueleto() {
  $("#estante").innerHTML = Array.from({ length: 8 }, () => `<li class="esqueleto" aria-hidden="true"></li>`).join("");
}

function opcionesDe(dim) {
  const valores = new Map();
  for (const j of estado.juegos) {
    if (dim === "tipo") valores.set(j.tipo ?? "otro", true);
    if (dim === "plataforma") j.plataformas?.forEach((p) => valores.set(p, true));
    if (dim === "formato") { j.precios.forEach((p) => valores.set(p.formato, true)); j.formatos?.forEach((x) => valores.set(x, true)); }
    if (dim === "tienda") j.precios.forEach((p) => valores.set(p.tienda, true));
  }
  const claves = [...valores.keys()];
  const orden = { tipo: Object.keys(TIPOS), plataforma: Object.keys(PLATAFORMAS), formato: Object.keys(FORMATOS) }[dim];
  if (orden) return claves.sort((a, b) => orden.indexOf(a) - orden.indexOf(b));
  return claves.sort((a, b) => {
    const ia = ORDEN_TIENDAS.indexOf(a), ib = ORDEN_TIENDAS.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
  });
}

function etiquetaDe(dim, valor) {
  return ({ tipo: TIPOS, plataforma: PLATAFORMAS, formato: FORMATOS }[dim]?.[valor]) ?? valor;
}

function contar(dim, valor) {
  let n = 0;
  for (const j of estado.juegos) {
    const v = evaluar(j, dim);
    if (!v) continue;
    if (dim === "tipo" && (j.tipo ?? "otro") === valor) n++;
    else if (dim === "plataforma" && j.plataformas?.includes(valor)) n++;
    else if (dim === "formato" && (v.filas.some((p) => p.formato === valor) || (j.precios.length === 0 && j.formatos?.includes(valor)))) n++;
    else if (dim === "tienda" && v.filas.some((p) => p.tienda === valor)) n++;
  }
  return n;
}

function pintarFacetas() {
  for (const dim of ["tipo", "plataforma", "formato", "tienda"]) {
    const caja = $(`#f-${dim}`);
    caja.innerHTML = opcionesDe(dim).map((valor) => {
      const n = contar(dim, valor);
      const marcado = estado.filtros[dim].has(valor);
      return `<label class="opcion" data-vacia="${n === 0 && !marcado}">
        <input type="checkbox" data-dim="${dim}" value="${esc(valor)}" ${marcado ? "checked" : ""}>
        <span>${esc(etiquetaDe(dim, valor))}</span><span class="opcion__n">${n}</span>
      </label>`;
    }).join("");
  }
  $("#solo-ofertas").checked = estado.soloOfertas;
  const activos = Object.values(estado.filtros).reduce((s, x) => s + x.size, 0) + (estado.soloOfertas ? 1 : 0);
  const c = $("#contador-filtros");
  c.hidden = activos === 0;
  c.textContent = activos;
}

function tarjeta({ j, v }) {
  const plats = (j.plataformas ?? []).map((p) => `<li class="plat">${esc(PLATAFORMAS_CORTAS[p] ?? p)}</li>`).join("");
  const mc = j.metacritic?.score;
  const sinPrecio = v.precio == null;

  const etiqueta = sinPrecio
    ? `<div class="etiqueta etiqueta--sin-oferta etiqueta--sin-precio"><span class="etiqueta__precio">Sin precio disponible</span></div>`
    : `<div class="etiqueta ${v.enOferta ? "" : "etiqueta--sin-oferta"}">
         <span class="etiqueta__precio">${dinero(v.precio)}</span>
         ${v.enOferta && v.regular > v.precio ? `<span class="etiqueta__antes">${dinero(v.regular)}</span>` : ""}
       </div>`;

  const sellos = [];
  if (j.igualaMinimoHistorico) sellos.push(`<span class="sello">Mínimo histórico</span>`);
  if (v.mejor && !v.mejor.vigente) sellos.push(`<span class="sello sello--viejo" title="La tienda no respondió en la última revisión">Precio sin confirmar</span>`);

  return `<li class="juego">
    <button class="juego__abrir" type="button" data-id="${esc(j.id)}" aria-label="${esc(j.titulo)}: ver detalle">
      ${portada(j)}
      ${v.enOferta && v.descuento > 0 ? `<span class="descuento">−${v.descuento}%</span>` : ""}
      <div class="juego__cuerpo">
        <h3 class="juego__titulo">${esc(j.titulo)}</h3>
        ${etiqueta}
        <p class="juego__tienda">${v.mejor ? `en ${esc(v.mejor.tienda)}${v.mejor.formato === "fisico" ? " · físico" : ""}` : "&nbsp;"}</p>
        <div class="juego__pie">
          <ul class="plataformas" aria-label="Plataformas">${plats}</ul>
          ${sellos.join("")}
          <span class="mc ${claseMc(mc)}" title="Metacritic">${mc ?? "N/D"}</span>
        </div>
      </div>
    </button>
  </li>`;
}

function pintarEstante() {
  const lista = resultados();
  const paginas = Math.max(1, Math.ceil(lista.length / POR_PAGINA));
  estado.pagina = Math.min(Math.max(1, estado.pagina), paginas);
  const desde = (estado.pagina - 1) * POR_PAGINA;
  const visibles = lista.slice(desde, desde + POR_PAGINA);

  const estante = $("#estante");
  estante.dataset.vista = estado.vista;
  estante.innerHTML = visibles.map(tarjeta).join("");
  $("#vacio").hidden = lista.length > 0;

  const ofertas = lista.filter((x) => x.v.enOferta).length;
  $("#total").innerHTML = lista.length
    ? `<strong>${lista.length}</strong> juegos · ${ofertas} en oferta${paginas > 1 ? ` · mostrando ${desde + 1}–${desde + visibles.length}` : ""}`
    : "";

  pintarPaginas(paginas);
  $$(".vista button").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.vista === estado.vista)));
  $("#ordenar").value = estado.orden;
  escribirUrl();
}

function pintarPaginas(total) {
  const nav = $("#paginas");
  if (total <= 1) { nav.innerHTML = ""; return; }
  const p = estado.pagina;
  const nums = new Set([1, total, p - 1, p, p + 1].filter((n) => n >= 1 && n <= total));
  const orden = [...nums].sort((a, b) => a - b);
  let html = `<button type="button" data-pag="${p - 1}" ${p === 1 ? "disabled" : ""} aria-label="Página anterior">‹</button>`;
  orden.forEach((n, i) => {
    if (i > 0 && n - orden[i - 1] > 1) html += `<span>…</span>`;
    html += `<button type="button" data-pag="${n}" ${n === p ? 'aria-current="page"' : ""}>${n}</button>`;
  });
  html += `<button type="button" data-pag="${p + 1}" ${p === total ? "disabled" : ""} aria-label="Página siguiente">›</button>`;
  nav.innerHTML = html;
}

// ─────────────────────────── detalle ───────────────────────────
function abrirDetalle(id, desdeHash = false) {
  const j = estado.juegos.find((x) => x.id === id);
  if (!j) return;
  const dlg = $("#detalle");
  $("#detalle-contenido").innerHTML = htmlDetalle(j);
  if (!dlg.open) dlg.showModal();
  if (!desdeHash) history.replaceState(null, "", `${location.pathname}${location.search}#juego/${encodeURIComponent(id)}`);
}

function abrirDesdeHash() {
  const m = location.hash.match(/^#juego\/(.+)$/);
  if (m && estado.juegos.length) abrirDetalle(decodeURIComponent(m[1]), true);
}

function htmlDetalle(j) {
  const mejor = j.precios.find((p) => p.vigente) ?? j.precios[0];
  const filas = j.precios.map((p) => {
    const regular = p.motivoOferta === "referencia" ? j.precioReferencia : p.precioRegular;
    return `<tr class="${p === mejor ? "mejor-fila" : ""}">
      <td>${esc(p.tienda)}${p.vigente ? "" : `<span class="no-vigente">Último precio conocido · ${esc(haceCuanto(p.consultadoEn))}</span>`}</td>
      <td class="col-plat">${esc(PLATAFORMAS[p.plataforma] ?? p.plataforma)}</td>
      <td class="col-formato">${esc(FORMATOS[p.formato] ?? p.formato)}</td>
      <td class="num">${dinero(p.precioActual)}</td>
      <td class="num">${p.enOferta && regular > p.precioActual ? `<span class="tachado">${dinero(regular)}</span> <span class="pct">−${p.descuentoPct}%</span>` : ""}</td>
      <td class="num"><a class="boton ir" href="${esc(p.url)}" target="_blank" rel="noopener noreferrer">Ir a la tienda</a></td>
    </tr>`;
  }).join("");

  const mc = j.metacritic ?? {};
  const mcPlats = Object.entries(mc.porPlataforma ?? {})
    .map(([p, s]) => `<div class="mc-plataforma"><span class="mc ${claseMc(s)}">${s}</span>${esc(PLATAFORMAS[p] ?? p)}</div>`).join("");

  const h = j.hltb;
  const maxH = Math.max(h?.principal ?? 0, h?.principalExtras ?? 0, h?.completista ?? 0, h?.todosEstilos ?? 0, 1);
  const filaH = (nombre, horas) => `<div class="hltb__fila"><span>${nombre}</span>
      <span class="hltb__barra" aria-hidden="true"><span style="width:${horas ? Math.max(3, (horas / maxH) * 100) : 0}%"></span></span>
      <span class="hltb__horas">${horas ? `${horas} h` : "—"}</span></div>`;

  return `
    <aside class="detalle__lado">
      ${portada(j)}
      <dl class="datos">
        <dt>Precio sugerido</dt><dd>${j.precioReferencia ? dinero(j.precioReferencia) : "—"}</dd>
        <dt>Mejor precio</dt><dd>${mejor ? `${dinero(mejor.precioActual)} en ${esc(mejor.tienda)}` : "—"}</dd>
        <dt>Mínimo histórico</dt><dd>${j.minimoHistorico != null ? `${dinero(j.minimoHistorico)}${j.fechaMinimoHistorico ? ` <small>(${j.fechaMinimoHistorico.toLocaleDateString("es")})</small>` : ""}` : "—"}</dd>
        <dt>Lanzamiento</dt><dd>${esc(j.fechaLanzamiento ?? j.anio ?? "—")}</dd>
        <dt>Género</dt><dd>${esc(j.genero || "—")}</dd>
        <dt>Tipo</dt><dd>${esc(TIPOS[j.tipo] ?? j.tipo)}</dd>
        <dt>Revisado</dt><dd>${esc(haceCuanto(j.actualizadoEn) || "—")}</dd>
      </dl>
    </aside>
    <section class="detalle__principal">
      <button class="detalle__cerrar" type="button" data-cerrar aria-label="Cerrar">✕</button>
      <h2 id="detalle-titulo">${esc(j.titulo)}</h2>
      <p class="detalle__sub">${(j.plataformas ?? []).map((p) => esc(PLATAFORMAS[p] ?? p)).join(", ")}</p>

      <h3>Precios por tienda</h3>
      ${j.precios.length ? `<table class="precios">
        <thead><tr><th>Tienda</th><th class="col-plat">Plataforma</th><th class="col-formato">Formato</th><th class="num">Precio</th><th class="num">Antes</th><th></th></tr></thead>
        <tbody>${filas}</tbody></table>` : `<p>No encontramos este juego a la venta en las tiendas revisadas.</p>`}

      <h3>Metacritic</h3>
      ${mcPlats ? `<div class="mc-plataformas">${mcPlats}</div>` : `<p>Sin calificación publicada.</p>`}
      ${mc.url ? `<p class="nota"><a href="${esc(mc.url)}" target="_blank" rel="noopener noreferrer">Ver reseñas en Metacritic</a></p>` : ""}

      <h3>¿Cuánto dura? (HowLongToBeat)</h3>
      ${h ? `<div class="hltb">
          ${filaH("Historia principal", h.principal)}
          ${filaH("Principal + extras", h.principalExtras)}
          ${filaH("Completista", h.completista)}
          ${filaH("Todos los estilos", h.todosEstilos)}
        </div>
        ${h.url ? `<p class="nota"><a href="${esc(h.url)}" target="_blank" rel="noopener noreferrer">Ver en HowLongToBeat</a></p>` : ""}`
      : `<p>Sin datos de duración.</p>`}
    </section>`;
}

// ─────────────────────────── URL (filtros compartibles) ───────────────────────────
function leerUrl() {
  const q = new URLSearchParams(location.search);
  const lista = (k) => new Set((q.get(k) ?? "").split(",").filter(Boolean));
  estado.filtros.tipo = lista("tipo");
  estado.filtros.plataforma = lista("plataforma");
  estado.filtros.formato = lista("formato");
  estado.filtros.tienda = lista("tienda");
  estado.soloOfertas = q.get("ofertas") === "1";
  estado.busqueda = normalizar(q.get("q") ?? "");
  $("#buscar").value = q.get("q") ?? "";
  estado.orden = ORDENES[q.get("orden")] ? q.get("orden") : "metacritic";
  estado.vista = q.get("vista") === "lista" ? "lista" : "cuadricula";
  estado.pagina = Number(q.get("p")) || 1;
}

function escribirUrl() {
  const q = new URLSearchParams();
  for (const [k, s] of Object.entries(estado.filtros)) if (s.size) q.set(k, [...s].join(","));
  if (estado.soloOfertas) q.set("ofertas", "1");
  if ($("#buscar").value.trim()) q.set("q", $("#buscar").value.trim());
  if (estado.orden !== "metacritic") q.set("orden", estado.orden);
  if (estado.vista !== "cuadricula") q.set("vista", estado.vista);
  if (estado.pagina > 1) q.set("p", estado.pagina);
  const s = q.toString();
  history.replaceState(null, "", `${location.pathname}${s ? `?${s}` : ""}${location.hash}`);
}

// ─────────────────────────── eventos ───────────────────────────
function refiltrar() {
  estado.pagina = 1;
  pintarFacetas();
  pintarEstante();
}

function limpiar() {
  Object.values(estado.filtros).forEach((s) => s.clear());
  estado.soloOfertas = false;
  estado.busqueda = "";
  $("#buscar").value = "";
  refiltrar();
}

$("#filtros").addEventListener("change", (e) => {
  const t = e.target;
  if (t.id === "solo-ofertas") estado.soloOfertas = t.checked;
  else if (t.dataset.dim) t.checked ? estado.filtros[t.dataset.dim].add(t.value) : estado.filtros[t.dataset.dim].delete(t.value);
  refiltrar();
});

let temporizador;
$("#buscar").addEventListener("input", (e) => {
  clearTimeout(temporizador);
  temporizador = setTimeout(() => { estado.busqueda = normalizar(e.target.value.trim()); refiltrar(); }, 160);
});

$("#ordenar").addEventListener("change", (e) => { estado.orden = e.target.value; estado.pagina = 1; pintarEstante(); });
$$(".vista button").forEach((b) => b.addEventListener("click", () => { estado.vista = b.dataset.vista; pintarEstante(); }));
$("#limpiar").addEventListener("click", limpiar);
$("#vacio-limpiar").addEventListener("click", limpiar);

$("#paginas").addEventListener("click", (e) => {
  const b = e.target.closest("button[data-pag]");
  if (!b || b.disabled) return;
  estado.pagina = Number(b.dataset.pag);
  pintarEstante();
  $("#resultados").scrollIntoView({ behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
});

$("#estante").addEventListener("click", (e) => {
  const b = e.target.closest(".juego__abrir");
  if (b) abrirDetalle(b.dataset.id);
});

const dlg = $("#detalle");
dlg.addEventListener("click", (e) => {
  if (e.target === dlg || e.target.closest("[data-cerrar]")) dlg.close();
});
dlg.addEventListener("close", () => {
  if (location.hash.startsWith("#juego/")) history.replaceState(null, "", `${location.pathname}${location.search}`);
});
window.addEventListener("hashchange", abrirDesdeHash);

// Filtros como cajón en móvil
const panel = $("#filtros");
const abrirPanel = (abrir) => { panel.dataset.abierto = String(abrir); };
$("#abrir-filtros").addEventListener("click", () => abrirPanel(true));
$("#cerrar-filtros").addEventListener("click", () => abrirPanel(false));
$("#ver-resultados").addEventListener("click", () => abrirPanel(false));
document.addEventListener("keydown", (e) => { if (e.key === "Escape") abrirPanel(false); });

iniciar();
