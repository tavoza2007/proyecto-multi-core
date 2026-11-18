// ═══════════════ PrecioPixel · análisis de rendimiento ═══════════════
// Lee las mediciones que deja el comando `dotnet run -- rendimiento` (data/rendimiento.json desde GitHub
// Actions y data/rendimiento-pc.json desde una computadora). Cada configuración se mide varias veces y aquí
// se usa la MEDIANA. Con eso calcula, con las fórmulas del enunciado:
//   speedup    S = T secuencial / T paralelo
//   eficiencia E = S / número de trabajadores (hilos del nivel 1)
// y además Karp–Flatt, el efecto de cada nivel y el ajuste de la ley de Amdahl. Las gráficas son SVG, sin librerías.

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

/** Rango compacto: "66–74 s" o "13,2–15,4 min". */
function rango(min, max) {
  if (!valido(min) || !valido(max)) return "—";
  return max >= 90 ? `${num(min / 60, 1)}–${num(max / 60, 1)} min` : `${Math.round(min)}–${Math.round(max)} s`;
}

function mediana(valores) {
  const v = valores.filter(valido).sort((a, b) => a - b);
  if (!v.length) return null;
  const m = Math.floor(v.length / 2);
  return v.length % 2 ? v[m] : (v[m - 1] + v[m]) / 2;
}

const NOMBRES_NIVEL = { "solo-n1": "Solo nivel 1", "solo-n2": "Solo nivel 2", "solo-n3": "Solo nivel 3" };
const DETALLE_NIVEL = {
  "solo-n1": "juegos a la vez; fuentes y tiendas una por una",
  "solo-n2": "las 3 fuentes de cada juego a la vez; juegos y tiendas uno por uno",
  "solo-n3": "las tiendas de cada juego a la vez; juegos y fuentes uno por uno",
};

function nombreConfig(g) {
  if (g.modo === "secuencial") return "Secuencial";
  if (g.modo === "niveles") return NOMBRES_NIVEL[g.config] ?? `Niveles ${g.niveles}`;
  return `${g.hilos} ${g.hilos === 1 ? "hilo" : "hilos"}`;
}

// ─────────────────────────── cálculo ───────────────────────────
const amdahl = (f, p) => 1 / (f + (1 - f) / p);

/** Junta las repeticiones de cada configuración: mediana del tiempo, mínimo y máximo. */
function agrupar(mediciones) {
  const grupos = new Map();
  for (const m of mediciones.filter((x) => valido(x.segundos) && x.segundos > 0)) {
    const clave = m.config || (m.modo === "secuencial" ? "secuencial" : String(m.hilos));
    if (!grupos.has(clave)) grupos.set(clave, []);
    grupos.get(clave).push(m);
  }
  return [...grupos.entries()].map(([config, reps]) => {
    const tiempos = reps.map((r) => r.segundos).sort((a, b) => a - b);
    const med = mediana(tiempos);
    const central = reps.reduce((a, b) => (Math.abs(b.segundos - med) < Math.abs(a.segundos - med) ? b : a));
    const modo = central.modo === "secuencial" ? "secuencial" : central.modo === "niveles" ? "niveles" : "paralelo";
    return {
      config,
      modo,
      niveles: central.niveles || (modo === "secuencial" ? "ninguno" : "1+2+3"),
      hilos: central.hilos ?? 1,
      nucleos: central.nucleos ?? null,
      segundos: med,
      min: tiempos[0],
      max: tiempos.at(-1),
      reps: reps.length,
      peticiones: mediana(reps.map((r) => r.peticiones)),
      juegos: central.juegos,
      conDatos: (central.juegosOk ?? 0) + (central.juegosParciales ?? 0),
      maquina: central.maquina,
      preparacion: mediana(reps.map((r) => r.segundosPreparacion)),
    };
  });
}

function analizar(datos) {
  const grupos = agrupar(datos.mediciones);
  const sec = grupos.find((g) => g.modo === "secuencial") ?? null;
  const par = grupos.filter((g) => g.modo === "paralelo").sort((a, b) => a.hilos - b.hilos);
  const orden = ["solo-n3", "solo-n2", "solo-n1"];
  const niv = grupos.filter((g) => g.modo === "niveles").sort((a, b) => orden.indexOf(a.config) - orden.indexOf(b.config));
  const uno = par.find((g) => g.hilos === 1) ?? null;

  // Amdahl sobre el nivel 1 (base: 1 hilo, con los niveles 2 y 3 encendidos): la f que mejor explica
  // los speedups medidos, por mínimos cuadrados.
  let f = null;
  const puntos = uno ? par.filter((g) => g.hilos > 1).map((g) => ({ p: g.hilos, s: uno.segundos / g.segundos })) : [];
  if (puntos.length) {
    let menorError = Infinity;
    for (let i = 0; i <= 4000; i++) {
      const x = i / 4000;
      const error = puntos.reduce((acc, { p, s }) => acc + (s - amdahl(x, p)) ** 2, 0);
      if (error < menorError) { menorError = error; f = x; }
    }
  }

  const filas = [...(sec ? [sec] : []), ...niv, ...par].map((g) => {
    const speedup = sec ? sec.segundos / g.segundos : null;
    const vsUno = uno && g.modo === "paralelo" ? uno.segundos / g.segundos : null;
    // Las corridas «solo nivel 2» y «solo nivel 3» no tienen hilos en el nivel 1: ahí la eficiencia por hilo no aplica
    const sinHilos = g.modo === "niveles" && !g.niveles.split("+").includes("1");
    return {
      g,
      speedup,
      // Fórmula del enunciado: eficiencia = speedup / número de trabajadores (hilos) y también por núcleo de la máquina
      eficiencia: speedup != null && !sinHilos ? speedup / g.hilos : null,
      eficienciaNucleo: speedup != null && g.nucleos && g.modo !== "secuencial" ? speedup / g.nucleos : null,
      vsUno,
      karpFlatt: vsUno != null && g.hilos > 1 ? (1 / vsUno - 1 / g.hilos) / (1 - 1 / g.hilos) : null,
    };
  });

  const mejor = filas.filter((x) => x.g.modo === "paralelo")
    .reduce((a, b) => ((b.speedup ?? b.vsUno ?? 0) > (a?.speedup ?? a?.vsUno ?? 0) ? b : a), null);
  const maxHilos = par.find((g) => g.hilos === (niv.find((n) => n.config === "solo-n1")?.hilos)) ?? par.at(-1) ?? null;
  return { grupos, sec, par, niv, uno, f, filas, mejor, maxHilos };
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
 * cfg: { tipo: "barras" | "lineas", etiquetas, series: [{ nombre, valores, color, punteada, referencia, rangos }],
 *        formatoEje, formatoValor, descripcion, tituloTooltip, minimoMaximo, valores: "extremos" | "todos" }
 * Las series "referencia" (ideal, 100 %) no definen la escala: si se salen del área se recortan.
 * "rangos" ([mín, máx] por punto) dibuja una línea vertical con la variación entre repeticiones.
 */
function dibujar(cont, cfg) {
  const ancho = Math.max(260, cont.clientWidth);
  const alto = cont.clientHeight || 260;
  const mg = { izq: 44, der: cfg.tipo === "lineas" ? 44 : 12, arr: 16, aba: 30 };
  const w = ancho - mg.izq - mg.der;
  const h = alto - mg.arr - mg.aba;

  const propias = cfg.series.filter((s) => !s.referencia);
  const datos = [...propias.flatMap((s) => s.valores), ...propias.flatMap((s) => (s.rangos ?? []).map((r) => r?.[1]))].filter(valido);
  const ticks = ticksBonitos(Math.max(...datos, cfg.minimoMaximo ?? 0) * 1.1);
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

  const bigotes = (serie) => (serie.rangos ?? []).map((r, i) => (r && valido(r[0]) && valido(r[1]) && r[1] > r[0]
    ? `<g class="bigote"><line x1="${x(i)}" x2="${x(i)}" y1="${y(r[0])}" y2="${y(r[1])}"/><line x1="${x(i) - 4}" x2="${x(i) + 4}" y1="${y(r[0])}" y2="${y(r[0])}"/><line x1="${x(i) - 4}" x2="${x(i) + 4}" y1="${y(r[1])}" y2="${y(r[1])}"/></g>`
    : "")).join("");

  if (cfg.tipo === "barras") {
    const serie = cfg.series[0];
    const bw = Math.min(46, banda * 0.6);
    s += serie.valores.map((v, i) => (valido(v)
      ? `<path d="${barra(x(i) - bw / 2, y(v), bw, y(0) - y(v), 4)}" style="fill:${serie.color}"/>` : "")).join("");
    s += bigotes(serie);
    const indices = cfg.valores === "todos" ? serie.valores.map((_, i) => i) : [0, serie.valores.length - 1];
    indices.forEach((i) => {
      const v = serie.valores[i];
      if (!valido(v)) return;
      const arriba = Math.max(v, serie.rangos?.[i]?.[1] ?? v);
      s += `<text class="valor" x="${x(i)}" y="${y(arriba) - 6}" text-anchor="middle">${esc(cfg.formatoValor(v))}</text>`;
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
    s += bigotes(medida);
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
      const lineas = cfg.series.filter((r) => valido(r.valores[i])).map((r) => {
        const rango = r.rangos?.[i];
        const extra = rango && rango[1] > rango[0] ? ` (${cfg.formatoValor(rango[0])} a ${cfg.formatoValor(rango[1])})` : "";
        return `<span><i style="color:${r.color}"></i>${esc(r.nombre)}: ${esc(cfg.formatoValor(r.valores[i]) + extra)}</span>`;
      });
      tip.innerHTML = `<strong>${esc(cfg.tituloTooltip?.(i) ?? cfg.etiquetas[i])}</strong>${lineas.join("<br>")}`;
      const visibles = cfg.series.filter((r) => !r.referencia).map((r) => Math.max(r.valores[i] ?? 0, r.rangos?.[i]?.[1] ?? 0)).filter(valido);
      tip.style.left = `${Math.min(Math.max(x(i), 110), ancho - 110)}px`;
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
  const g0 = a.grupos[0];
  const juegos = g0?.juegos ?? 0;
  const reps = Math.max(...a.grupos.map((g) => g.reps), 1);

  // Titular: el mejor speedup como una etiqueta de precio
  const mejor = a.mejor;
  if (mejor && a.sec) {
    $("#titular").innerHTML = `
      <div class="etiqueta rend__etiqueta"><span class="etiqueta__precio">${esc(veces(mejor.speedup))}</span></div>
      <p>más rápido con <strong>los tres niveles y ${mejor.g.hilos} hilos</strong> que la versión secuencial:
        revisar los ${juegos} juegos en todas las tiendas tardó <strong>${esc(tiempo(mejor.g.segundos))}</strong>
        en lugar de <strong>${esc(tiempo(a.sec.segundos))}</strong>${reps > 1 ? ` (medianas de ${reps} mediciones)` : ""}.</p>`;
  } else if (mejor) {
    $("#titular").innerHTML = `
      <div class="etiqueta rend__etiqueta"><span class="etiqueta__precio">${esc(veces(mejor.vsUno))}</span></div>
      <p>más rápido con <strong>${mejor.g.hilos} hilos</strong> que con 1 hilo (${esc(tiempo(mejor.g.segundos))} para ${juegos} juegos).
        Falta la medición secuencial.</p>`;
  }

  const fecha = fuente.datos.generadoEn ? FMT_FECHA.format(new Date(fuente.datos.generadoEn)) : "";
  $("#contexto").textContent = [`${juegos} juegos`, g0?.maquina, reps > 1 ? `${reps} mediciones por configuración` : "1 medición por configuración", fecha && `medido el ${fecha}`]
    .filter(Boolean).join(" · ");
  $("#metodo-github").hidden = fuente.id !== "github";
  $("#metodo-repeticiones").textContent = reps > 1
    ? `Cada configuración se midió ${reps} veces y se usa la mediana; la línea vertical en las gráficas va del tiempo más rápido al más lento.`
    : "Cada configuración se midió una sola vez.";

  // Tabla
  $("#tabla").innerHTML = a.filas.map((x) => `
    <tr class="${x === mejor ? "destacada" : ""}">
      <td>${esc(nombreConfig(x.g))}</td>
      <td>${esc(x.g.niveles)}</td>
      <td>${esc(tiempo(x.g.segundos))}</td>
      <td>${x.g.reps > 1 ? esc(rango(x.g.min, x.g.max)) : "—"}</td>
      <td>${esc(veces(x.speedup))}</td>
      <td>${esc(pct(x.eficiencia))}</td>
      <td>${esc(pct(x.eficienciaNucleo))}</td>
      <td>${esc(veces(x.vsUno))}</td>
      <td>${x.karpFlatt != null ? esc(num(x.karpFlatt, 3)) : "—"}</td>
      <td>${esc((x.g.peticiones ?? 0).toLocaleString("es-CR"))}</td>
      <td>${esc(`${x.g.conDatos} de ${x.g.juegos}`)}</td>
    </tr>`).join("");

  // Efecto de cada nivel
  const sp = (g) => (a.sec && g ? a.sec.segundos / g.segundos : null);
  const n = Object.fromEntries(a.niv.map((g) => [g.config, g]));
  if (a.sec && a.niv.length) {
    const partes = ["solo-n3", "solo-n2", "solo-n1"].filter((c) => n[c])
      .map((c) => `<li><strong>${esc(NOMBRES_NIVEL[c])}</strong> (${esc(c === "solo-n1" ? `${n[c].hilos} ${DETALLE_NIVEL[c]}` : DETALLE_NIVEL[c])}): ${esc(veces(sp(n[c])))} más rápido que secuencial.</li>`);
    if (a.maxHilos) partes.push(`<li><strong>Los tres niveles juntos</strong> (${a.maxHilos.hilos} hilos): ${esc(veces(sp(a.maxHilos)))}.</li>`);
    $("#efecto-niveles").innerHTML = `<ul>${partes.join("")}</ul>`;
  } else {
    $("#efecto-niveles").innerHTML = "<p>Esta medición no incluye las corridas de un solo nivel.</p>";
  }

  // Textos de Amdahl con los números de esta medición
  if (a.f != null) {
    const limite = a.f > 0 ? veces(1 / a.f) : "sin límite visible";
    const ultimo = a.par.at(-1);
    const prediccion = ultimo ? amdahl(a.f, ultimo.hilos) : null;
    $("#amdahl-resultado").innerHTML = `Ajustando la fórmula a las medianas del nivel 1 (tomando 1 hilo como base, con los niveles 2 y 3 encendidos)
      resulta <strong>f ≈ ${esc(num(a.f * 100, 1))} %</strong>: el límite teórico es <strong>${esc(limite)}</strong> más rápido que con 1 hilo.
      ${ultimo ? `Con ${ultimo.hilos} hilos el modelo predice ${esc(veces(prediccion))} y se midió ${esc(veces(a.uno.segundos / ultimo.segundos))}.` : ""}
      La columna Karp–Flatt de la tabla es esa misma fracción calculada en cada medición; si crece con los hilos, el costo extra viene de coordinar y de hacer fila en los sitios.`;
  } else {
    $("#amdahl-resultado").textContent = "Hacen falta las mediciones con 1 hilo y con más hilos para ajustar la fórmula.";
  }
  $("#amdahl-preparacion").textContent = valido(g0?.preparacion) ? `${num(g0.preparacion, 1)} s` : "unos segundos";
  $("#amdahl-uno").textContent = a.sec && a.uno ? ` (${veces(a.sec.segundos / a.uno.segundos)} más rápido)` : "";

  // Gráficas
  const neutro = "var(--tinta-suave)";
  const etiquetasPar = a.par.map((g) => String(g.hilos));
  const base = a.sec && a.uno ? a.sec.segundos / a.uno.segundos : 1;
  const conSec = [...(a.sec ? [a.sec] : []), ...a.par];
  const speedups = a.par.map((g) => (a.sec ? a.sec.segundos / g.segundos : a.uno ? a.uno.segundos / g.segundos : null));
  const rangosSpeedup = a.par.map((g) => (a.sec ? [a.sec.segundos / g.max, a.sec.segundos / g.min] : null));
  const eficiencias = a.par.map((g, i) => (valido(speedups[i]) && a.sec ? (speedups[i] / g.hilos) * 100 : null));
  const tituloHilos = (i) => `${etiquetasPar[i]} ${etiquetasPar[i] === "1" ? "hilo" : "hilos"}`;
  const efecto = [a.sec, n["solo-n3"], n["solo-n2"], n["solo-n1"], a.maxHilos];
  const etiquetasEfecto = ["Secuencial", "Nivel 3", "Nivel 2", "Nivel 1", "Los tres"];

  graficas.length = 0;
  graficas.push(
    [$("#g-tiempo"), {
      tipo: "barras",
      etiquetas: conSec.map((g) => (g.modo === "secuencial" ? "Sec." : String(g.hilos))),
      series: [{ nombre: "Tiempo (mediana)", valores: conSec.map((g) => g.segundos / 60), rangos: conSec.map((g) => [g.min / 60, g.max / 60]), color: "var(--serie-medida)" }],
      formatoEje: (t) => num(t, t < 10 && t % 1 ? 1 : 0),
      formatoValor: (v) => `${num(v, v < 10 ? 1 : 0)} min`,
      tituloTooltip: (i) => nombreConfig(conSec[i]),
      descripcion: `Tiempo por configuración: ${conSec.map((g) => `${nombreConfig(g)} ${tiempo(g.segundos)}`).join(", ")}`,
    }],
    [$("#g-speedup"), {
      tipo: "lineas",
      etiquetas: etiquetasPar,
      series: [
        { nombre: "Ideal", valores: a.par.map((g) => base * g.hilos), color: neutro, punteada: true, referencia: true },
        ...(a.f != null ? [{ nombre: "Amdahl", valores: a.par.map((g) => base * amdahl(a.f, g.hilos)), color: "var(--serie-modelo)", punteada: true }] : []),
        { nombre: "Medido", valores: speedups, rangos: rangosSpeedup, color: "var(--serie-medida)" },
      ],
      formatoEje: (t) => `${num(t, 0)}×`,
      formatoValor: (v) => veces(v),
      tituloTooltip: tituloHilos,
      descripcion: `Speedup por número de hilos: ${a.par.map((g, i) => `${g.hilos} hilos ${veces(speedups[i])}`).join(", ")}`,
    }],
    [$("#g-eficiencia"), {
      tipo: "barras",
      etiquetas: etiquetasPar,
      series: [
        { nombre: "Eficiencia", valores: eficiencias, color: "var(--serie-medida)" },
        { nombre: "Ideal", valores: eficiencias.map(() => 100), color: neutro, referencia: true },
      ],
      minimoMaximo: 100,
      formatoEje: (t) => `${num(t, 0)} %`,
      formatoValor: (v) => `${num(v, 0)} %`,
      tituloTooltip: tituloHilos,
      descripcion: `Eficiencia (speedup ÷ hilos): ${a.par.map((g, i) => `${g.hilos} hilos ${pct(eficiencias[i] / 100)}`).join(", ")}`,
    }],
    [$("#g-niveles"), {
      tipo: "barras",
      valores: "todos",
      etiquetas: etiquetasEfecto,
      series: [{ nombre: "Speedup", valores: efecto.map((g) => (g ? sp(g) : null)), rangos: efecto.map((g) => (g && a.sec ? [a.sec.segundos / g.max, a.sec.segundos / g.min] : null)), color: "var(--serie-medida)" }],
      formatoEje: (t) => `${num(t, 0)}×`,
      formatoValor: (v) => veces(v),
      tituloTooltip: (i) => (efecto[i] ? nombreConfig(efecto[i]) + (i === 4 ? " (los tres niveles)" : "") : etiquetasEfecto[i]),
      descripcion: `Speedup con un solo nivel en paralelo: ${efecto.map((g, i) => `${etiquetasEfecto[i]} ${veces(g ? sp(g) : null)}`).join(", ")}`,
    }],
  );
  $("#panel-niveles").hidden = !(a.sec && a.niv.length);
  redibujar();
}

function redibujar() {
  for (const [cont, cfg] of graficas) if (!cont.closest("[hidden]")) dibujar(cont, cfg);
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
