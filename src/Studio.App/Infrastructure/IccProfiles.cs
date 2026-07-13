using System.IO;

namespace Studio.App.Infrastructure;

/// <summary>
/// Profils ICC du catalogue (catalog/icc). Les profils des imprimantes sont fournis par
/// leurs pilotes et installés par Windows dans le dossier « couleur » du spouleur : on les
/// y importe une fois, puis on les attache à un produit ou à une finition.
/// </summary>
public static class IccProfiles
{
    /// <summary>Où Windows installe les profils livrés par les pilotes (DS620-R0.icc, DE100 Lustre.icc…).</summary>
    public static string WindowsColorDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");

    public static string CatalogIccDir(string catalogDir) => Path.Combine(catalogDir, "icc");

    /// <summary>Profils déjà importés dans le catalogue, par nom de fichier.</summary>
    public static List<string> List(string catalogDir)
    {
        var dir = CatalogIccDir(catalogDir);
        if (!Directory.Exists(dir)) return new List<string>();

        return Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Copie un profil dans le catalogue et renvoie son nom de fichier.</summary>
    public static string Import(string catalogDir, string sourcePath)
    {
        var dir = CatalogIccDir(catalogDir);
        Directory.CreateDirectory(dir);

        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(dir, fileName);
        // le catalogue doit rester autonome : on copie, on ne pointe pas vers le dossier Windows
        File.Copy(sourcePath, target, overwrite: true);
        return fileName;
    }
}
