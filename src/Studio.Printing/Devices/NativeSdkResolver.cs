using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Studio.Printing.Devices;

/// <summary>
/// Indique au chargeur .NET où trouver les bibliothèques natives des machines.
///
/// Ni le SDK Fuji ni le SDK DNP ne sont installés à côté de Studio : ils vivent dans le
/// dossier de DiLand. Sans cette indication, le chargement échoue alors que la
/// bibliothèque est bien présente sur le poste.
///
/// Le point délicat : .NET n'accepte qu'UN SEUL résolveur par assembly. Deux pilotes qui
/// en installeraient chacun un se neutraliseraient — le second appel lève. On centralise
/// donc ici, avec une table des bibliothèques connues.
/// </summary>
public static class NativeSdkResolver
{
    private static readonly ConcurrentDictionary<string, string> Dossiers =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Verrou = new();
    private static bool _installe;

    /// <summary>Déclare le dossier où trouver une bibliothèque native.</summary>
    public static void Register(string dllName, string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dllName);
        ArgumentException.ThrowIfNullOrEmpty(directory);

        Dossiers[dllName] = directory;
        Install(typeof(NativeSdkResolver).Assembly);
    }

    /// <summary>Dossier déclaré pour une bibliothèque, ou null.</summary>
    public static string? DirectoryOf(string dllName) =>
        Dossiers.TryGetValue(dllName, out var dossier) ? dossier : null;

    /// <summary>Vrai si la bibliothèque est présente à l'endroit déclaré.</summary>
    public static bool Exists(string dllName) =>
        DirectoryOf(dllName) is { } dossier && File.Exists(Path.Combine(dossier, dllName));

    private static void Install(Assembly assembly)
    {
        lock (Verrou)
        {
            if (_installe) return;
            _installe = true;

            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
            {
                if (!Dossiers.TryGetValue(name, out var dossier)) return IntPtr.Zero;

                var chemin = Path.Combine(dossier, name);
                return File.Exists(chemin) && NativeLibrary.TryLoad(chemin, out var handle)
                    ? handle
                    : IntPtr.Zero;
            });
        }
    }

    /// <summary>
    /// Emplacements probables des SDK machines : la variable d'environnement d'abord,
    /// puis le dossier de l'application, puis l'installation de DiLand.
    /// </summary>
    public static IEnumerable<string> ProbeDirectories(string variableEnvironnement)
    {
        var declare = Environment.GetEnvironmentVariable(variableEnvironnement);
        if (!string.IsNullOrWhiteSpace(declare)) yield return declare;

        yield return AppContext.BaseDirectory;

        foreach (var programFiles in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "DiLand Studio 2");
        }
    }

    /// <summary>Cherche une bibliothèque et la déclare si elle est trouvée.</summary>
    public static string? Locate(string dllName, string variableEnvironnement)
    {
        foreach (var dossier in ProbeDirectories(variableEnvironnement))
        {
            if (!File.Exists(Path.Combine(dossier, dllName))) continue;
            Register(dllName, dossier);
            return dossier;
        }
        return null;
    }
}
