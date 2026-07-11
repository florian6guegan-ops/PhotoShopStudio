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
        "Program Files (x86)", "AppData", ".thumbnails", "MISC",
    };

    /// <summary>
    /// Énumère les photos, dossier DCIM d'abord (appareils photo / téléphones),
    /// en ignorant les dossiers système. S'arrête à <paramref name="max"/> fichiers.
    /// </summary>
    public static List<string> Scan(string root, int max = 20000)
    {
        var results = new List<string>();

        var dcim = Path.Combine(root, "DCIM");
        if (Directory.Exists(dcim))
            ScanFolder(dcim, results, max);

        if (results.Count < max)
            ScanFolder(root, results, max, skipDcim: true);

        return results;
    }

    public static bool IsPhoto(string path) => Extensions.Contains(Path.GetExtension(path));

    private static void ScanFolder(string folder, List<string> results, int max, bool skipDcim = false)
    {
        if (results.Count >= max) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var file in files)
        {
            if (results.Count >= max) return;
            if (IsPhoto(file)) results.Add(file);
        }

        IEnumerable<string> subFolders;
        try
        {
            subFolders = Directory.EnumerateDirectories(folder);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var sub in subFolders)
        {
            var name = Path.GetFileName(sub);
            if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (skipDcim && name.Equals("DCIM", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith('.')) continue;
            ScanFolder(sub, results, max);
        }
    }
}
