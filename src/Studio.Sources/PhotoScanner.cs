namespace Studio.Sources;

/// <summary>Recense les photos d'un support (clé USB, carte SD, dossier), DCIM en premier.</summary>
public static class PhotoScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".bmp", ".tif", ".tiff", ".webp",
    };

    private static readonly string[] IgnoredDirectories =
    {
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "AppData", ".thumbnails", "MISC",
    };

    /// <summary>
    /// Nombre de photos qu'un écran peut afficher d'un coup.
    ///
    /// Ce n'est pas une limite de confort : la planche de vignettes n'est pas virtualisée,
    /// chaque photo y coûte une vignette décodée ET son rendu avec le cadre. Un dossier
    /// parent choisi par erreur — un profil utilisateur, la racine d'un disque — en
    /// ramenait des dizaines de milliers et tuait l'application par manque de mémoire,
    /// dans le rendu WPF, sans rien afficher (constaté le 01/08/2026 à 17:47).
    ///
    /// Au-delà, on s'arrête et on le DIT à l'opérateur, qui descend d'un dossier.
    /// </summary>
    public const int MaxAffichable = 1200;

    /// <summary>Toutes les photos d'un support, sous-dossiers compris.</summary>
    public static List<string> Scan(string root, int max = 20000) => Scan(root, recursive: true, max);

    /// <param name="recursive">
    /// false = uniquement les fichiers posés DANS ce dossier. C'est ce que fait l'écran de
    /// navigation : on ouvre un dossier, on voit ce qu'il contient, pas ce que contient
    /// tout le disque en dessous.
    /// </param>
    /// <param name="max">Plafond : au-delà on rend la main, la liste est tronquée.</param>
    public static List<string> Scan(
        string root, bool recursive, int max = 20000, CancellationToken ct = default)
    {
        var results = new List<string>();

        // DCIM d'abord (appareils photo, téléphones) : quand on prend tout un support et
        // que le plafond tombe, mieux vaut qu'il tombe sur les photos du client.
        if (recursive)
        {
            var dcim = Path.Combine(root, "DCIM");
            if (Directory.Exists(dcim))
            {
                Walk(dcim, recursive: true, skipDcim: false, max, results.Add, ct);
                results.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        if (results.Count >= max) return results;

        var reste = new List<string>();
        Walk(root, recursive, skipDcim: recursive, max - results.Count, reste.Add, ct);
        reste.Sort(StringComparer.OrdinalIgnoreCase);
        results.AddRange(reste);
        return results;
    }

    /// <summary>
    /// Compte les photos sans les retenir — de quoi annoncer « 128 photos » sur un dossier
    /// avant de l'ouvrir. <paramref name="max"/> borne le travail : passé ce nombre on
    /// s'arrête et l'appelant affiche « 5000+ ».
    /// </summary>
    public static int Count(
        string folder, bool recursive, int max = int.MaxValue, CancellationToken ct = default) =>
        Walk(folder, recursive, skipDcim: false, max, onPhoto: null, ct);

    /// <summary>
    /// Une photo du dossier, pour l'illustrer : la première par ordre alphabétique, ou à
    /// défaut la première trouvée dans les sous-dossiers. La recherche a un budget — il
    /// s'agit d'une vignette d'aperçu, pas d'un inventaire.
    /// </summary>
    public static string? FirstPhoto(string folder, CancellationToken ct = default)
    {
        var pending = new Queue<string>();
        pending.Enqueue(folder);

        for (var visites = 0; pending.Count > 0 && visites < 200; visites++)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Dequeue();

            var photo = SafeFiles(dir)
                .Where(IsPhoto)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (photo is not null) return photo;

            foreach (var sub in SafeDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                if (!Ignorable(sub))
                    pending.Enqueue(sub);
        }

        return null;
    }

    public static bool IsPhoto(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Parcours ITÉRATIF, jamais récursif : une arborescence profonde — et il y en a sur
    /// les disques clients — débordait la pile, et un débordement de pile ne se rattrape
    /// pas, le processus meurt sur place.
    /// </summary>
    /// <returns>Nombre de photos rencontrées, au plus <paramref name="max"/>.</returns>
    private static int Walk(
        string root, bool recursive, bool skipDcim, int max,
        Action<string>? onPhoto, CancellationToken ct)
    {
        if (max <= 0) return 0;

        var found = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();

            foreach (var file in SafeFiles(folder))
            {
                if (!IsPhoto(file)) continue;
                onPhoto?.Invoke(file);
                if (++found >= max) return found;
            }

            if (!recursive) continue;

            foreach (var sub in SafeDirectories(folder))
            {
                if (Ignorable(sub)) continue;

                // DCIM n'est écarté qu'à la racine du support : il vient d'être parcouru
                // en premier, un sous-dossier nommé DCIM plus bas reste légitime.
                if (skipDcim && folder == root &&
                    Path.GetFileName(sub).Equals("DCIM", StringComparison.OrdinalIgnoreCase))
                    continue;

                pending.Push(sub);
            }
        }

        return found;
    }

    /// <summary>
    /// Dossiers qu'on ne descend pas : dossiers système, dossiers cachés, et surtout les
    /// LIENS. Les jonctions d'un profil Windows (« Mes documents » vers « Documents »)
    /// rebouclent sur elles-mêmes — les suivre est un parcours sans fin.
    /// </summary>
    internal static bool Ignorable(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0) return false;
        if (name.StartsWith('.')) return true;
        if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        try
        {
            var attributs = File.GetAttributes(path);
            if (attributs.HasFlag(FileAttributes.ReparsePoint)) return true;
            if (attributs.HasFlag(FileAttributes.System)) return true;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }

        return false;
    }

    /// <summary>
    /// Fichiers d'un dossier, débarrassés de ce dont aucune imprimante ne fera rien : les
    /// fichiers VIDES — une copie interrompue, une carte défaillante — et les fichiers
    /// cachés ou système, qui ne sont jamais les photos d'un client.
    ///
    /// Le tri se fait ici et pas plus haut parce que <c>DirectoryInfo</c> porte déjà la
    /// taille et les attributs : les lire ne coûte pas un accès disque de plus.
    /// </summary>
    internal static IEnumerable<string> SafeFiles(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => f.Length > 0)
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden)
                            && !f.Attributes.HasFlag(FileAttributes.System))
                .Select(f => f.FullName)
                .ToList();
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    internal static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.EnumerateDirectories(folder).ToList(); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }
}
