namespace PrecioPixel.Motor;

/// <summary>
/// Qué niveles de paralelismo están encendidos en una corrida.
///   Nivel 1: los juegos (Parallel.ForEachAsync con <see cref="Hilos"/> juegos a la vez)
///   Nivel 2: las fuentes de un juego (precios ‖ Metacritic ‖ HowLongToBeat)
///   Nivel 3: las consultas dentro de una fuente (las tiendas y las consolas de Metacritic)
/// La corrida normal enciende los tres. El análisis de rendimiento los apaga uno por uno para medir cuánto aporta cada nivel.
/// </summary>
public sealed record ModoParalelismo(bool Nivel1, bool Nivel2, bool Nivel3, int Hilos)
{
    public static ModoParalelismo Secuencial { get; } = new(false, false, false, 1);

    public static ModoParalelismo Completo(int hilos) => new(true, true, true, Math.Max(1, hilos));

    /// <summary>"secuencial", "paralelo" (los tres niveles) o "niveles" (solo algunos).</summary>
    public string Nombre => (Nivel1, Nivel2, Nivel3) switch
    {
        (false, false, false) => "secuencial",
        (true, true, true) => "paralelo",
        _ => "niveles",
    };

    /// <summary>"1+2+3", "3", "ninguno"…</summary>
    public string Niveles =>
        string.Join("+", new[] { (Nivel1, "1"), (Nivel2, "2"), (Nivel3, "3") }.Where(n => n.Item1).Select(n => n.Item2)) is { Length: > 0 } t
            ? t
            : "ninguno";

    public override string ToString() => Nombre switch
    {
        "secuencial" => "secuencial (ningún nivel en paralelo)",
        "paralelo" => $"paralelo · niveles 1+2+3 · {Hilos} juegos a la vez",
        _ => $"solo nivel {Niveles}" + (Nivel1 ? $" · {Hilos} juegos a la vez" : ""),
    };
}
