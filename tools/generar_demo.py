"""
Genera web/data/deals.json con DATOS DE DEMOSTRACIÓN (precios inventados) a partir de seed/juegos.json,
con exactamente el mismo formato que produce el scraper. Sirve para ver y probar la web sin Firebase
ni scraping. El archivo queda marcado "demo": true y la web muestra un aviso.

    python tools/generar_demo.py
"""
import json, random, datetime as dt, pathlib

RAIZ = pathlib.Path(__file__).resolve().parent.parent
rnd = random.Random(1316)
ahora = dt.datetime.now(dt.timezone.utc).replace(microsecond=0)
iso = lambda d: d.isoformat().replace("+00:00", "Z")

juegos = json.loads((RAIZ / "seed" / "juegos.json").read_text(encoding="utf-8"))
CONSOLAS = {"switch", "switch2", "ps5", "ps4", "xbox-series", "xbox-one", "3ds"}

def fila(tienda, fuente, plataforma, formato, ref, url):
    if ref == 0:
        return dict(tienda=tienda, fuente=fuente, plataforma=plataforma, formato=formato, precioActual=0.0,
                    precioRegular=0.0, descuentoPct=0, enOferta=False, motivoOferta=None, moneda="USD", url=url,
                    tituloEncontrado="", imagenUrl=None, similitud=1.0, consultadoEn=iso(ahora), vigente=True)
    regular = round(ref * rnd.choice([1] * 9 + [0.85]), 2)
    if regular < ref: regular = round(regular) - 0.01 if regular > 1 else regular
    en_oferta = rnd.random() < 0.12
    actual = round(regular * (1 - rnd.choice([.1, .2, .25, .33, .4, .5, .6, .75])), 2) if en_oferta else regular
    if actual > 1: actual = round(actual) - 0.01 if en_oferta else actual
    actual = max(actual, 0.99)
    motivo, desc = None, 0
    if regular > actual + 0.009:
        motivo, desc = "tienda", round((regular - actual) / regular * 100)
    elif actual <= ref * 0.9:
        motivo, desc = "referencia", round((ref - actual) / ref * 100)
    return dict(tienda=tienda, fuente=fuente, plataforma=plataforma, formato=formato, precioActual=actual,
                precioRegular=regular, descuentoPct=desc, enOferta=motivo is not None, motivoOferta=motivo,
                moneda="USD", url=url, tituloEncontrado="", imagenUrl=None, similitud=1.0,
                consultadoEn=iso(ahora - dt.timedelta(minutes=rnd.randint(1, 50))), vigente=True)

resultados = []
for j in juegos:
    ref, pl, titulo = j["precioReferencia"], j["plataformas"], j["titulo"]
    q = titulo.replace(" ", "+")
    precios = []
    if "pc" in pl:
        url = f"https://store.steampowered.com/app/{j['steamAppId']}/" if j["steamAppId"] else f"https://store.steampowered.com/search/?term={q}"
        if j["steamAppId"] or rnd.random() < .7:
            precios.append(fila("Steam", "Steam", "pc", "digital", ref, url))
        if ref > 0 and rnd.random() < .6:
            gog = j["id"].replace("-", "_")
            precios.append(fila("GOG", "GOG", "pc", "digital", ref, f"https://www.gog.com/en/game/{gog}"))
    for p in ("switch2", "switch"):
        if p in pl:
            precios.append(fila("Nintendo eShop", "Nintendo eShop", p, "digital", ref + (10 if p == "switch2" and ref >= 59 else 0),
                                f"https://www.nintendo.com/us/search/#q={q}"))
    for p in ("ps5", "ps4"):
        if p in pl:
            precios.append(fila("PlayStation Store", "PlayStation Store", p, "digital", ref, f"https://store.playstation.com/en-us/search/{q}"))
    if "fisico" in j["formatos"]:
        for p in [x for x in pl if x in CONSOLAS][:2]:
            if rnd.random() < .8:
                precios.append(fila("Amazon", "Amazon", p, "fisico", ref, f"https://www.amazon.com/s?k={q}"))

    # Algunas tiendas "caídas" para mostrar precios no vigentes
    if precios and rnd.random() < .06:
        v = rnd.choice(precios); v["vigente"] = False
        v["consultadoEn"] = iso(ahora - dt.timedelta(hours=rnd.randint(7, 30)))

    precios.sort(key=lambda r: r["precioActual"])
    mejor = next((r for r in precios if r["vigente"]), precios[0] if precios else None)
    plats_mc = {p: max(40, min(99, rnd.gauss(82, 8))) for p in pl}
    plats_mc = {p: int(s) for p, s in plats_mc.items()}
    principal = round(rnd.uniform(4, 60), 1)
    minimo = round(mejor["precioActual"] * rnd.choice([1, .9, .8, .75, .7]), 2) if mejor else None

    # Portadas de demostración: solo se conocen sin internet las de Steam (su dirección depende del id).
    # Con el scraper real, cada juego toma su portada de la tienda, Metacritic o HowLongToBeat.
    sid = j["steamAppId"]
    portadas = [
        f"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{sid}/header.jpg",
        f"https://cdn.cloudflare.steamstatic.com/steam/apps/{sid}/header.jpg",
        f"https://cdn.cloudflare.steamstatic.com/steam/apps/{sid}/capsule_616x353.jpg",
    ] if sid else []

    resultados.append(dict(
        id=j["id"], titulo=titulo,
        imagenUrl=portadas[0] if portadas else None, imagenes=portadas,
        plataformas=pl, tipo=j["tipo"], formatos=j["formatos"], genero=j["genero"], anio=j["anio"],
        fechaLanzamiento=str(j["anio"]), precioReferencia=ref, precios=precios,
        mejorPrecio=mejor["precioActual"] if mejor else None,
        precioRegular=(ref if mejor and mejor["motivoOferta"] == "referencia" else mejor["precioRegular"]) if mejor else None,
        descuentoPct=mejor["descuentoPct"] if mejor else 0, enOferta=bool(mejor and mejor["enOferta"]),
        mejorTienda=mejor["tienda"] if mejor else None, tiendas=sorted({r["tienda"] for r in precios}),
        metacritic=dict(score=max(plats_mc.values()), porPlataforma=plats_mc,
                        url=f"https://www.metacritic.com/game/{j['metacriticSlug'] or j['id']}/", fuente="Metacritic"),
        hltb=dict(principal=principal, principalExtras=round(principal * rnd.uniform(1.3, 1.9), 1),
                  completista=round(principal * rnd.uniform(2, 3.5), 1), todosEstilos=round(principal * rnd.uniform(1.2, 1.8), 1),
                  url="https://howlongtobeat.com/", tituloEncontrado=titulo),
        minimoHistorico=minimo, fechaMinimoHistorico=iso(ahora - dt.timedelta(days=rnd.randint(1, 300))) if minimo else None,
        igualaMinimoHistorico=bool(mejor and mejor["enOferta"] and minimo == mejor["precioActual"]),
        actualizadoEn=iso(ahora), estado="ok" if all(r["vigente"] for r in precios) else "parcial",
        errores=[], duracionMs=rnd.randint(600, 4200),
    ))

resumen = dict(inicio=iso(ahora - dt.timedelta(seconds=94)), fin=iso(ahora), modo="paralelo", juegos=len(resultados),
               juegosOk=sum(r["estado"] == "ok" for r in resultados), juegosParciales=sum(r["estado"] == "parcial" for r in resultados),
               juegosSinDatos=0, peticionesHttp=0, reintentos=0, hilosDistintos=0, nucleos=0, maxJuegosEnParalelo=0,
               duracionSegundos=94.0, fuentesCaidas=[])

salida = RAIZ / "web" / "data" / "deals.json"
salida.parent.mkdir(parents=True, exist_ok=True)
salida.write_text(json.dumps(dict(generadoEn=iso(ahora), demo=True, resumen=resumen, juegos=resultados), ensure_ascii=False), encoding="utf-8")
print(f"{len(resultados)} juegos de demostración → {salida}")
