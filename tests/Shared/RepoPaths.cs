namespace CartonPrinting.Testing;

/// <summary>Rutas del repositorio para leer spec/ en tiempo de prueba (nunca se copia a src/).</summary>
public static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "spec", "contracts", "state-machine.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("No se encontró la raíz del repositorio (spec/contracts/state-machine.json).");
    });

    public static string Root => RootLazy.Value;

    public static string Spec => Path.Combine(Root, "spec");

    public static string SpecFile(params string[] parts) => Path.Combine([Spec, .. parts]);
}
