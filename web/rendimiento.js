// ═══════════════ PrecioPixel · análisis de rendimiento ═══════════════
// Lee las mediciones que deja el comando `dotnet run -- rendimiento` (data/rendimiento.json desde GitHub
// Actions y data/rendimiento-pc.json desde una computadora), calcula speedup, eficiencia, Karp–Flatt y
// ajusta la ley de Amdahl. Las gráficas se dibujan en SVG, sin librerías.

const FUENTES = [
  { id: "github", archivo: "data/rendimiento.json", nombre: "GitHub Actions" },
  { id: "pc", archivo: "data/rendimiento-pc.json", nombre: "Tu PC" },
];

const $ = (s) => document.querySelector(s);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const valido = (n) => n != null && Number.isFinite(n);
const num = (n, dec = 1) => (valido(n) ? n.toLocaleString("es-CR", { minimumFractionDigits: dec, maximumFractionDigits: dec }) : "—");
const pct = (x) => (valido(x) ? `${Math.round(x * 100)} %` : "—");
const veces = (x) => (valido(x) ? `${num(x, x >= 10 ? 1 : 2)}×` : "—");
const FMT_FECHA = new Intl.DateTimeFormat("es-CR", { day: "numeric", month: "long", year: "numeric", hour: "numeric", minute: "2-digit" });

function tiempo(seg) {
  if (!valido(seg)) return "—";
  const total = Math.round(seg);
  if (total < 90) return `${total} s`;
  const m = Math.floor(total / 60), s = total % 60;
  return s ? `${m} min ${s} s` : `${m} min`;
}

const nombreConfig = (m) => (m.modo === "secuencial" ? "Secuencial" : `${m.hilos} ${m.hilos === 1 ? "hilo" : "hilos"}`);

// ─────────────────────────── cálculo ───────────────────────────
const amdahl = (f, p) => 1 / (f + (1 - f) / p);

function analizar(datos) {
  const ms = datos.mediciones.filter((m) => valido(m.segundos) && m.segundos > 0);
  const sec = ms.find((m) => m.modo === "secuencial") ?? null;
  const par = ms.filter((m) => m.modo !== "secuencial").sort((a, b) => a.hilos - b.hilos);
  const uno = par.find((m) => m.hilos === 1) ?? null;

  // Amdahl sobre el nivel 1 (base: 1 hilo). Se busca la f que mejor explica los speedups medidos (mínimos cuadrados).
  let f = null;
  const puntos = uno ? par.filter((m) => m.hilos > 1).map((m) => ({ p: m.hilos, s: uno.segundos / m.segundos })) : [];
  if (puntos.length) {
    let menorError = Infinity;
    for (let i = 0; i <= 4000; i++) {
      const x = i / 4000;
      const error = puntos.reduce((acc, { p, s }) => acc + (s - amdahl(x, p)) ** 2, 0);
      if (error < menorError) { menorError = error; f = x; }
    }
  }

  const filas = [...(sec ? [sec] : []), ...par].map((m) => {
    const vsSec = sec ? sec.segundos / m.segundos : null;
    const vsUno = uno && m.modo !== "secuencial" ? uno.segundos / m.segundos : null;
    return {
      m,
      vsSec,
      vsUno,
      eficiencia: vsUno != null ? vsUno / m.hilos : null,
      // Karp–Flatt: fracción secuencial que se deduce de cada medición
      karpFlatt: vsUno != null && m.hilos > 1 ? (1 / vsUno - 1 / m.hilos) / (1 - 1 / m.hilos) : null,
    };
  });

  // Factor que aportan los niveles 2 y 3 (1 hilo frente a secuencial)
  const factorNiveles = sec && uno ? sec.segundos / uno.segundos : null;
  const mejor = filas.filter((x) => x.m.modo !== "secuencial")
    .reduce((a, b) => ((b.vsSec ?? b.vsUno ?? 0) > (a?.vsSec ?? a?.vsUno ?? 0) ? b : a), null);
  return { ms, sec, par, uno, f, filas, factorNiveles, mejor };
}

// ─────────────────────────── gráficas SVG ───────────────────────────
function ticksBonitos(max, n = 4) {
  if (!(max > 0)) return [0, 1];
  const paso0 = max / n;
  const mag = 10 ** Math.floor(Math.log10(paso0));
  const paso = [1, 2, 2.5, 5, 10].map((k) => k * mag).find((p) => p >= paso0 - 1e-9);
  const pasos = Math.ceil(max / paso - 1e-9);
  return Array.from({ length: pasos + 1 }, (_, i) => Number((i * paso).toPrecision(12)));
}

const barra = (x, y, ancho, alto, r) => {
  r = Math.min(r, ancho / 2, alto);
  return `M${x},${y + alto}V${y + r}Q${x},${y} ${x + r},${y}H${x + ancho - r}Q${x + ancho},${y} ${x + ancho},${y + r}V${y + alto}Z`;
};

/**
 * cfg: { tipo: "barras" | "lineas", etiquetas, series: [{ nombre, valores, color, punteada, referencia }],
 *        formatoEje, formatoValor, descripcion, rotuloFinal }
 * Las series "referencia" (ideal, 100 %) no definen la escala: si se salen del área se recortan.
 */
function dibujar(cont, cfg) {
  const ancho = Math.max(260, cont.clientWidth);
  const alto = cont.clientHeight || 260;
  const mg = { izq: 44, der: cfg.tipo === "lineas" ? 44 : 12, arr: 14, aba: 30 };
  const w = ancho - mg.izq - mg.der;
  const h = alto - mg.arr - mg.aba;

  const datos = cfg.series.filter((s) => !s.referencia).flatMap((s) => s.valores).filter(valido);
  const ticks = ticksBonitos(Math.max(...datos, cfg.minimoMaximo ?? 0) * 1.08);
  const tope = ticks.at(-1);
  const y = (v) => mg.arr + h - (v / tope) * h;
  const banda = w / cfg.etiquetas.length;
  const x = (i) => mg.izq + banda * (i + 0.5);
  const id = cont.id;

  let s = `<svg width="${ancho}" height="${alto}" viewBox="0 0 ${ancho} ${alto}" role="img" aria-label="${esc(cfg.descripcion)}">`;
  s += `<defs><clipPath id="${id}-area"><rect x="${mg.izq}" y="${mg.arr - 2}" width="${w}" height="${h + 2}"/></clipPath></defs>`;
  s += `<g class="rejilla">${ticks.slice(1).map((t) => `<line x1="${mg.izq}" x2="${mg.izq + w}" y1="${y(t)}" y2="${y(t)}"/>`).join("")}</g>`;
  s += `<line class="base" x1="${mg.izq}" x2="${mg.izq + w}" y1="${y(0)}" y2="${y(0)}"/>`;
  s += `<g class="eje">`;
  s += ticks.map((t) => `<text x="${mg.izq - 8}" y="${y(t) + 4}" text-anchor="end">${esc(cfg.formatoEje(t))}</text>`).join("");
  s += cfg.etiquetas.map((e, i) => `<text x="${x(i)}" y="${alto - 9}" text-anchor="middle">${esc(e)}</text>`).join("");
  s += `</g>`;

  if (cfg.tipo === "barras") {
    const serie = cfg.series[0];
    const bw = Math.min(46, banda * 0.6);
    s += serie.valores.map((v, i) => (valido(v)
      ? `<path d="${barra(x(i) - bw / 2, y(v), bw, y(0) - y(v), 4)}" style="fill:${serie.color}"/>` : "")).join("");
    // Valor solo en la primera y la última barra: lo demás está en el tooltip y en la tabla
    [0, serie.valores.length - 1].forEach((i) => {
      if (valido(serie.valores[i])) s += `<text class="valor" x="${x(i)}" y="${y(serie.valores[i]) - 6}" text-anchor="middle">${esc(cfg.formatoValor(serie.valores[i]))}</text>`;
    });
    for (const ref of cfg.series.filter((r) => r.referencia)) {
      s += `<line x1="${mg.izq}" x2="${mg.izq + w}" y1="${y(ref.valores[0])}" y2="${y(ref.valores[0])}" style="stroke:${ref.color};stroke-width:2;stroke-dasharray:6 5"/>`;
    }
  } else {
    s += `<g clip-path="url(#${id}-area)">`;
    for (const serie of cfg.series) {
      const pts = serie.valores.map((v, i) => (valido(v) ? `${x(i)},${y(v)}` : null)).filter(Boolean);
      if (pts.length < 2) continue;
      s += `<polyline points="${pts.join(" ")}" fill="none" style="stroke:${serie.color};stroke-width:2;stroke-linejoin:round;stroke-linecap:round${serie.punteada ? ";stroke-dasharray:6 5" : ""}"/>`;
    }
    s += `</g>`;
    const medida = cfg.series.find((r) => !r.referencia && !r.punteada) ?? cfg.series[0];
    s += medida.valores.map((v, i) => (valido(v) ? `<circle class="marcador" cx="${x(i)}" cy="${y(v)}" r="4.5" style="fill:${medida.color}"/>` : "")).join("");
    const ult = medida.valores.length - 1;
    if (valido(medida.valores[ult])) {
      s += `<text class="valor" x="${x(ult) + 9}" y="${y(medida.valores[ult]) + 4}">${esc(cfg.formatoValor(medida.valores[ult]))}</text>`;
    }
  }

  s += `<line class="cruz" x1="0" x2="0" y1="${mg.arr}" y2="${mg.arr + h}" style="display:none"/>`;
  s += cfg.etiquetas.map((_, i) => `<rect class="zona" data-i="${i}" x="${mg.izq + banda * i}" y="${mg.arr}" width="${banda}" height="${h}" fill="transparent"/>`).join("");
  s += `</svg><div class="tooltip" hidden></div>`;
  cont.innerHTML = s;

  const tip = cont.querySelector(".tooltip");
  const cruz = cont.querySelector(".cruz");
  cont.querySelectorAll(".zona").forEach((zona) => {
    zona.addEventListener("pointerenter", () => {
      const i = Number(zona.dataset.i);
      const lineas = cfg.series.filter((r) => valido(r.valores[i]))
        .map((r) => `<span><i style="color:${r.color}"></i>${esc(r.nombre)}: ${esc(cfg.formatoValor(r.valores[i]))}</span>`);
      tip.innerHTML = `<strong>${esc(cfg.tituloTooltip?.(i) ?? cfg.etiquetas[i])}</strong>${lineas.join("<br>")}`;
      const visibles = cfg.series.filter((r) => !r.referencia).map((r) => r.valores[i]).filter(valido);
      tip.style.left = `${Math.min(Math.max(x(i), 90), ancho - 90)}px`;
      tip.style.top = `${Math.max(y(Math.max(...visibles, 0)), mg.arr + 40)}px`;
      tip.hidden = false;
      if (cfg.tipo === "lineas") {
        cruz.setAttribute("x1", x(i)); cruz.setAttribute("x2", x(i)); cruz.style.display = "";
      }
    });
    zona.addEventListener("pointerleave", () => { tip.hidden = true; cruz.style.display = "none"; });
  });
}

// ─────────────────────────── página ───────────────────────────
let fuentes = [];
let actual = null;
const graficas = [];

function pintar(fuente) {
  actual = fuente;
  const a = analizar(fuente.datos);
  const juegos = a.ms[0]?.juegos ?? 0;

  // Titular: el mejor speedup como una etiqueta de precio
  const mejor = a.mejor;
  if (mejor && a.sec) {
    $("#titular").innerHTML = `
      <div class="etiqueta rend__etiqueta"><span class="etiqueta__precio">${esc(veces(mejor.vsSec))}</span></div>
      <p>más rápido con <strong>${mejor.m.hilos} hilos</strong> que la versión secuencial:
        revisar los ${juegos} juegos en todas las tiendas tardó <strong>${esc(tiempo(mejor.m.segundos))}</strong>
        en lugar de <strong>${esc(tiempo(a.sec.segundos))}</strong>.</p>`;
  } else if (mejor) {
    $("#titular").innerHTML = `
      <div class="etiqueta rend__etiqueta"><span class="etiqueta__precio">${esc(veces(mejor.vsUno))}</span></div>
      <p>más rápido con <strong>${mejor.m.hilos} hilos</strong> que con 1 hilo (${esc(tiempo(mejor.m.segundos))} para ${juegos} juegos).
        Falta la medición secuencial.</p>`;
  }

  const m0 = a.ms[0];
  const fecha = fuente.datos.generadoEn ? FMT_FECHA.format(new Date(fuente.datos.generadoEn)) : "";
  $("#contexto").textContent = [`${juegos} juegos`, m0?.maquina, fecha && `medido el ${fecha}`].filter(Boolean).join(" · ");
  $("#metodo-github").hidden = fuente.id !== "github";

  // Tabla
  $("#tabla").innerHTML = a.filas.map((x) => `
    <tr class="${x === mejor ? "destacada" : ""}">
      <td>${esc(nombreConfig(x.m))}</td>
      <td>${esc(tiempo(x.m.segundos))}</td>
      <td>${esc(veces(x.vsSec))}</td>
      <td>${esc(veces(x.vsUno))}</td>
      <td>${esc(pct(x.eficiencia))}</td>
      <td>${x.karpFlatt != null ? esc(num(x.karpFlatt, 3)) : "—"}</td>
      <td>${esc(x.m.peticiones.toLocaleString("es-CR"))}</td>
      <td>${x.m.juegosOk + x.m.juegosParciales} de ${x.m.juegos}</td>
    </tr>`).join("");

  // Textos de Amdahl con los números de esta medición
  if (a.f != null) {
    const limite = a.f > 0 ? veces(1 / a.f) : "sin límite visible";
    const ultimo = a.par.at(-1);
    const prediccion = ultimo ? amdahl(a.f, ultimo.hilos) : null;
    $("#amdahl-resultado").innerHTML = `Ajustando la fórmula a las mediciones del nivel 1 (tomando 1 hilo como base) resulta
      <strong>f ≈ ${esc(num(a.f * 100, 1))} %</strong>: el límite teórico es <strong>${esc(limite)}</strong> más rápido que con 1 hilo.
      ${ultimo ? `Con ${ultimo.hilos} hilos el modelo predice ${esc(veces(prediccion))} y se midió ${esc(veces(a.uno.segundos / ultimo.segundos))}.` : ""}
      La columna Karp–Flatt de la tabla es la misma fracción calculada en cada medición; si crece con los hilos, el costo extra viene de coordinar y de hacer fila en los sitios.`;
  } else {
    $("#amdahl-resultado").textContent = "Hacen falta las mediciones con 1 hilo y con más hilos para ajustar la fórmula.";
  }
  $("#amdahl-preparacion").textContent = valido(m0?.segundosPreparacion) ? `${num(m0.segundosPreparacion, 1)} s` : "unos segundos";
  $("#amdahl-uno").textContent = a.factorNiveles ? ` (${veces(a.factorNiveles)} más rápido)` : "";

  // Gráficas
  const neutro = "var(--tinta-suave)";
  const etiquetasPar = a.par.map((m) => String(m.hilos));
  const base = a.factorNiveles ?? 1;
  const medido = a.par.map((m) => (a.sec ? a.sec.segundos / m.segundos : a.uno ? a.uno.segundos / m.segundos : null));
  const efic = a.par.map((m) => (a.uno ? a.uno.segundos / m.segundos / m.hilos : null));

  graficas.length = 0;
  graficas.push(
    [$("#g-tiempo"), {
      tipo: "barras",
      etiquetas: a.filas.map((x) => (x.m.modo === "secuencial" ? "Sec." : String(x.m.hilos))),
      series: [{ nombre: "Tiempo", valores: a.filas.map((x) => x.m.segundos / 60), color: "var(--serie-medida)" }],
      formatoEje: (t) => num(t, t < 10 && t % 1 ? 1 : 0),
      formatoValor: (v) => `${num(v, v < 10 ? 1 : 0)} min`,
      tituloTooltip: (i) => nombreConfig(a.filas[i].m),
      descripcion: `Tiempo en minutos por configuración: ${a.filas.map((x) => `${nombreConfig(x.m)} ${tiempo(x.m.segundos)}`).join(", ")}`,
    }],
    [$("#g-speedup"), {
      tipo: "lineas",
      etiquetas: etiquetasPar,
      series: [
        { nombre: "Ideal", valores: a.par.map((m) => base * m.hilos), color: neutro, punteada: true, referencia: true },
        ...(a.f != null ? [{ nombre: "Amdahl", valores: a.par.map((m) => base * amdahl(a.f, m.hilos)), color: "var(--serie-modelo)", punteada: true }] : []),
        { nombre: "Medido", valores: medido, color: "var(--serie-medida)" },
      ],
      formatoEje: (t) => `${num(t, 0)}×`,
      formatoValor: (v) => veces(v),
      tituloTooltip: (i) => `${etiquetasPar[i]} ${etiquetasPar[i] === "1" ? "hilo" : "hilos"}`,
      descripcion: `Speedup medido por número de hilos: ${a.par.map((m, i) => `${m.hilos} hilos ${veces(medido[i])}`).join(", ")}`,
    }],
    [$("#g-eficiencia"), {
      tipo: "barras",
      etiquetas: etiquetasPar,
      series: [
        { nombre: "Eficiencia", valores: efic.map((e) => (valido(e) ? e * 100 : null)), color: "var(--serie-medida)" },
        { nombre: "Ideal", valores: efic.map(() => 100), color: neutro, referencia: true },
      ],
      minimoMaximo: 100,
      formatoEje: (t) => `${num(t, 0)} %`,
      formatoValor: (v) => `${num(v, 0)} %`,
      tituloTooltip: (i) => `${etiquetasPar[i]} ${etiquetasPar[i] === "1" ? "hilo" : "hilos"}`,
      descripcion: `Eficiencia por número de hilos: ${a.par.map((m, i) => `${m.hilos} hilos ${pct(efic[i])}`).join(", ")}`,
    }],
  );
  redibujar();
}

function redibujar() {
  for (const [cont, cfg] of graficas) dibujar(cont, cfg);
}

function pintarSelector() {
  const caja = $("#fuentes");
  caja.hidden = fuentes.length < 2;
  caja.innerHTML = fuentes.map((f) => `<button type="button" data-fuente="${f.id}" aria-pressed="${f === actual}">${esc(f.nombre)}</button>`).join("");
}

$("#fuentes").addEventListener("click", (e) => {
  const b = e.target.closest("button[data-fuente]");
  if (!b) return;
  pintar(fuentes.find((f) => f.id === b.dataset.fuente));
  pintarSelector();
});

let pendiente = 0;
new ResizeObserver(() => {
  cancelAnimationFrame(pendiente);
  pendiente = requestAnimationFrame(redibujar);
}).observe($("#rend"));

async function cargar(f) {
  try {
    const r = await fetch(`${f.archivo}?t=${Date.now()}`, { cache: "no-store" });
    if (!r.ok) return null;
    const datos = await r.json();
    return datos?.mediciones?.length ? { ...f, datos } : null;
  } catch {
    return null;
  }
}

(async () => {
  fuentes = (await Promise.all(FUENTES.map(cargar))).filter(Boolean);
  if (!fuentes.length) {
    $("#titular").innerHTML = "";
    $("#sin-datos").hidden = false;
    return;
  }
  $("#contenido").hidden = false;
  pintar(fuentes[0]);
  pintarSelector();
})();
