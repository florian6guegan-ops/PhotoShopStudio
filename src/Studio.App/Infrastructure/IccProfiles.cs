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

    /// <summary>Un profil proposé à l'opérateur : ce qu'il lit, et le fichier derrière.</summary>
    /// <param name="Name">Nom du fichier, tel qu'il s'affiche dans la liste.</param>
    /// <param name="Path">Chemin complet — c'est lui qui sert à convertir.</param>
    /// <param name="FromCatalog">Vrai si le profil vient du catalogue, faux s'il vient de Windows.</param>
    public sealed record Entry(string Name, string Path, bool FromCatalog)
    {
        public string Label => FromCatalog ? Name : $"{Name}  (Windows)";

        /// <summary>
        /// Ce que la liste déroulante affiche. Une liste qui mêle « (aucun) » et des profils ne
        /// peut pas s'appuyer sur DisplayMemberPath : c'est donc ici que le libellé se décide,
        /// faute de quoi l'opérateur lirait « Entry { Name = …, Path = … } ».
        /// </summary>
        public override string ToString() => Label;
    }

    /// <summary>Profils déjà importés dans le catalogue, par nom de fichier.</summary>
    public static List<string> List(string catalogDir) =>
        Available(catalogDir).Where(e => e.FromCatalog).Select(e => e.Name).ToList();

    /// <summary>
    /// TOUS les profils utilisables : ceux du catalogue, puis ceux que les pilotes ont fait
    /// installer par Windows.
    ///
    /// Les seconds manquaient, et le catalogue <c>catalog/icc</c> n'existe sur aucun poste
    /// tant qu'on n'y a rien importé : la liste de la boîte d'agrandissement était donc vide,
    /// « (aucun) » pour seul choix — alors que les treize profils SC-P800 étaient posés par le
    /// pilote Epson depuis le début, dans le dossier couleur du spouleur.
    ///
    /// Le catalogue prime en cas d'homonymie : c'est lui que l'atelier a choisi.
    /// </summary>
    public static List<Entry> Available(string catalogDir)
    {
        var vues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profils = new List<Entry>();

        foreach (var (dir, duCatalogue) in
                 new[] { (CatalogIccDir(catalogDir), true), (WindowsColorDir, false) })
        {
            foreach (var fichier in Lister(dir))
            {
                var nom = System.IO.Path.GetFileName(fichier);
                if (!vues.Add(nom)) continue;
                profils.Add(new Entry(nom, fichier, duCatalogue));
            }
        }

        return profils
            .OrderByDescending(e => e.FromCatalog)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> Lister(string dir)
    {
        if (!Directory.Exists(dir)) return [];

        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // dossier illisible (droits, lecteur absent) : on se passe de ses profils
            return [];
        }
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
