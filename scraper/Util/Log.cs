namespace PrecioPixel;

/// <summary>Log de consola thread-safe con hora y número de hilo (útil para ver el paralelismo en vivo).</summary>
public static class Log
{
    private static readonly object Candado = new();
    public static bool Detallado { get; set; }

    public static void Info(string msg) => Escribir(msg, ConsoleColor.Gray);
    public static void Ok(string msg) => Escribir(msg, ConsoleColor.Green);
    public static void Advertencia(string msg) => Escribir(msg, ConsoleColor.Yellow);
    public static void Error(string msg) => Escribir(msg, ConsoleColor.Red);
    public static void Titulo(string msg) => Escribir(msg, ConsoleColor.Cyan);

    public static void Debug(string msg)
    {
        if (Detallado) Escribir(msg, ConsoleColor.DarkGray);
    }

    private static void Escribir(string msg, ConsoleColor color)
    {
        lock (Candado)
        {
            var anterior = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss.fff} h{Environment.CurrentManagedThreadId,3}] ");
            Console.ForegroundColor = color;
            Console.WriteLine(msg);
            Console.ForegroundColor = anterior;
        }
    }
}
