namespace PrecioPixel.Config;

/// <summary>Raíz de configuración. Se llena desde appsettings.json + variables de entorno (prefijo PP_).</summary>
public sealed class Opciones
{
    public FirebaseOpciones Firebase { get; set; } = new();
    public ParalelismoOpciones Paralelismo { get; set; } = new();
    public RedOpciones Red { get; set; } = new();
    public OfertasOpciones Ofertas { get; set; } = new();
    public TiendasOpciones Tiendas { get; set; } = new();
    public FuentesOpciones Fuentes { get; set; } = new();
    public SalidaOpciones Salida { get; set; } = new();
}

public sealed class FirebaseOpciones
{
    public string ProjectId { get; set; } = "";
    public string ColeccionJuegos { get; set; } = "juegos";
    public string ColeccionResultados { get; set; } = "resultados";
    public string ColeccionMeta { get; set; } = "meta";
}

public sealed class ParalelismoOpciones
{
    /// <summary>Nivel 1. 0 = automático (núcleos lógicos × 2, porque el trabajo mezcla red y CPU).</summary>
    public int MaxJuegosEnParalelo { get; set; }

    /// <summary>Límite de peticiones simultáneas a un mismo sitio, para no ser bloqueados.</summary>
    public int MaxPeticionesPorHost { get; set; } = 4;

    public int JuegosEnParaleloEfectivo =>
        MaxJuegosEnParalelo > 0 ? MaxJuegosEnParalelo : Environment.ProcessorCount * 2;
}

public sealed class RedOpciones
{
    public int TimeoutSegundos { get; set; } = 20;
    public int Reintentos { get; set; } = 3;
    public int BackoffBaseMs { get; set; } = 600;
    public int FallosParaAbrirCircuito { get; set; } = 6;
    public int CircuitoAbiertoSegundos { get; set; } = 90;
    public string UserAgent { get; set; } = "Mozilla/5.0";
    public string Pais { get; set; } = "us";
    public string Idioma { get; set; } = "en-us";
}

public sealed class OfertasOpciones
{
    /// <summary>Si la tienda no reporta descuento, se considera oferta cuando el precio está este % por debajo del precio de referencia del repo.</summary>
    public double UmbralContraReferenciaPct { get; set; } = 10;

    /// <summary>Similitud mínima (0..1) entre el título del repo y el encontrado en la tienda.</summary>
    public double SimilitudMinimaTitulo { get; set; } = 0.6;
}

public sealed class TiendaOpciones
{
    public bool Habilitada { get; set; } = true;
}

public sealed class TiendasOpciones
{
    public TiendaOpciones Steam { get; set; } = new();
    public TiendaOpciones Gog { get; set; } = new();
    public TiendaOpciones NintendoEshop { get; set; } = new();
    public TiendaOpciones PlayStationStore { get; set; } = new();
    public TiendaOpciones Amazon { get; set; } = new();
}

public sealed class FuentesOpciones
{
    public TiendaOpciones Metacritic { get; set; } = new();
    public TiendaOpciones HowLongToBeat { get; set; } = new();
}

public sealed class SalidaOpciones
{
    /// <summary>Ruta opcional donde además se exporta un JSON (modo demo de la web / respaldo).</summary>
    public string ExportarJson { get; set; } = "";
}
