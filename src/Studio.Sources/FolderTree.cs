namespace Studio.Sources;

/// <summary>Un dossier tel que l'écran de navigation le montre.</summary>
public sealed record FolderNode(string Path, string Name);

/// <summary>Une entrée du panneau de gauche : un disque ou un dossier usuel.</summary>
public sealed record FolderShortcut(string Path, string Label, string Icon);

/// <summary>
/// De quoi PARCOURIR l'arborescence, comme DiLand : les dossiers d'un dossier, le chemin
/// jusqu'à lui, et les points de départ usuels.
///
/// Sans cela, l'opérateur n'avait qu'une boîte Windows qui rend un dossier et rien
/// d'autre : impossible de voir ce qu'il y a dedans avant de le choisir, donc impossible
/// de choisir juste. On tombait sur un dossier sans photos — ou pire, sur un dossier
/// parent dont le scan ramenait tout un disque.
/// </summary>
public static class FolderTree
{
    /// <summary>Dans quel ordre présenter les sous-dossiers.</summary>
    public enum TriDossiers
    {
        /// <summary>
        /// Le plus récemment modifié d'abord. <b>C'est le défaut</b> : les photos qu'on
        /// vient de recevoir sont celles qu'on cherche, et un tri alphabétique les enfouit
        /// au milieu de dossiers vieux de plusieurs mois.
        /// </summary>
        PlusRecent,

        /// <summary>Ordre alphabétique — pour retrouver un dossier dont on connaît le nom.</summary>
        Nom,
    }

    /// <summary>
    /// Sous-dossiers visibles d'un dossier.
    ///
    /// La date retenue est celle de la DERNIÈRE ÉCRITURE du dossier, pas de sa création :
    /// un dossier créé la semaine dernière et rempli ce matin doit remonter en tête. Un
    /// dossier dont la date est illisible part au fond plutôt que de faire échouer la
    /// lecture — c'est le cas d'un support retiré en pleine énumération.
    /// </summary>
    public static List<FolderNode> SubFolders(string path, TriDossiers tri = TriDossiers.PlusRecent)
    {
        var nodes = new List<FolderNode>();
        foreach (var sub in PhotoScanner.SafeDirectories(path))
        {
            if (PhotoScanner.Ignorable(sub)) continue;

            try
            {
                if (File.GetAttributes(sub).HasFlag(FileAttributes.Hidden)) continue;
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            nodes.Add(new FolderNode(sub, Path.GetFileName(sub)));
        }

        if (tri == TriDossiers.Nom)
        {
            nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return nodes;
        }

        var dates = new Dictionary<string, DateTime>(nodes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes) dates[node.Path] = DerniereEcriture(node.Path);

        nodes.Sort((a, b) =>
        {
            var ordre = dates[b.Path].CompareTo(dates[a.Path]);
            return ordre != 0
                ? ordre
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return nodes;
    }

    /// <summary>Date de dernière écriture, ou <see cref="DateTime.MinValue"/> si illisible.</summary>
    private static DateTime DerniereEcriture(string path)
    {
        try { return Directory.GetLastWriteTime(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>Le dossier au-dessus, ou null si l'on est déjà à la racine d'un disque.</summary>
    public static string? Parent(string path)
    {
        try { return Directory.GetParent(path)?.FullName; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Le chemin décomposé en étapes cliquables : « D: › Photos › Mariage ». Remonter de
    /// trois niveaux d'un coup est le geste courant, et le bouton « parent » ne le fait pas.
    /// </summary>
    public static List<FolderNode> Breadcrumb(string path)
    {
        var etapes = new List<FolderNode>();

        for (var dir = SafeInfo(path); dir is not null; dir = dir.Parent)
        {
            // à la racine, Name vaut « D:\ » : on l'allège en « D: »
            var nom = dir.Parent is null ? dir.Name.TrimEnd('\\', '/') : dir.Name;
            etapes.Add(new FolderNode(dir.FullName, nom));
        }

        etapes.Reverse();
        return etapes;
    }

    private static DirectoryInfo? SafeInfo(string path)
    {
        try { return new DirectoryInfo(path); }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Points de départ : les disques d'abord — c'est par là qu'arrivent les photos des
    /// clients, clé, carte ou CD — puis les dossiers usuels du poste.
    /// </summary>
    public static List<FolderShortcut> Shortcuts()
    {
        var raccourcis = new List<FolderShortcut>();

        foreach (var drive in SafeDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed
                    or DriveType.CDRom or DriveType.Network))
                    continue;

                var lettre = drive.Name.TrimEnd('\\', '/');
                var nom = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? DefaultLabel(drive.DriveType)
                    : drive.VolumeLabel;

                raccourcis.Add(new FolderShortcut(
                    drive.RootDirectory.FullName, $"{nom} ({lettre})", Icon(drive.DriveType)));
            }
            catch (IOException) { }             // support éjecté pendant l'énumération
            catch (UnauthorizedAccessException) { }
        }

        foreach (var (dossier, libelle, icone) in UserFolders())
            if (!string.IsNullOrEmpty(dossier) && Directory.Exists(dossier))
                raccourcis.Add(new FolderShortcut(dossier, libelle, icone));

        return raccourcis;
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<(string Path, string Label, string Icon)> UserFolders()
    {
        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Images", "🖼");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Bureau", "🖥");
        yield return (Path.Combine(profil, "Downloads"), "Téléchargements", "⬇");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents", "📄");
    }

    private static string DefaultLabel(DriveType type) => type switch
    {
        DriveType.Removable => "Support amovible",
        DriveType.CDRom => "CD / DVD",
        DriveType.Network => "Dossier réseau",
        _ => "Disque",
    };

    private static string Icon(DriveType type) => type switch
    {
        DriveType.Removable => "🔌",
        DriveType.CDRom => "💿",
        DriveType.Network => "🌐",
        _ => "💾",
    };
}
