namespace PrecioPixel.Util;

/// <summary>Ubica la raíz del proyecto (la carpeta que contiene seed/) sin importar desde dónde se ejecute.</summary>
public static class Rutas
{
    public static string Raiz { get; } = BuscarRaiz();

    private static string BuscarRaiz()
    {
        foreach (var inicio in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(inicio);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "seed", "juegos.json"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Resolver(string ruta) => Path.IsPathRooted(ruta) ? ruta : Path.GetFullPath(Path.Combine(Raiz, ruta));
}
