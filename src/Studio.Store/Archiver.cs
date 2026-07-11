namespace Studio.Store;

/// <summary>
/// Archivage des commandes anciennes : déplace les dossiers de mois entiers révolus
/// de orders/ vers archive/ (même volume : déplacement instantané, jamais de copie).
/// </summary>
public static class Archiver
{
    /// <summary>Déplace les dossiers de commandes plus vieux que <paramref name="olderThanDays"/> jours.</summary>
    /// <returns>Nombre de dossiers de commandes archivés.</returns>
    public static int ArchiveOldOrders(string ordersRoot, string archiveRoot, int olderThanDays = 90)
    {
        if (!Directory.Exists(ordersRoot)) return 0;
        var cutoff = DateTime.Now.Date.AddDays(-olderThanDays);
        var moved = 0;

        foreach (var yearDir in Directory.EnumerateDirectories(ordersRoot))
        {
            if (!int.TryParse(Path.GetFileName(yearDir), out var year)) continue;
            foreach (var monthDir in Directory.EnumerateDirectories(yearDir))
            {
                if (!int.TryParse(Path.GetFileName(monthDir), out var month)) continue;

                foreach (var orderDir in Directory.EnumerateDirectories(monthDir))
                {
                    // nom : yyyyMMdd-NNN-xxxxxxxx → la date fait foi
                    var name = Path.GetFileName(orderDir);
                    if (name.Length < 8 || !DateTime.TryParseExact(name[..8], "yyyyMMdd",
                            null, System.Globalization.DateTimeStyles.None, out var day))
                        continue;
                    if (day >= cutoff) continue;

                    var target = Path.Combine(archiveRoot, year.ToString("0000"), month.ToString("00"), name);
                    if (Directory.Exists(target)) continue; // déjà archivé (reprise après incident)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(orderDir, target);
                    moved++;
                }

                if (!Directory.EnumerateFileSystemEntries(monthDir).Any())
                    Directory.Delete(monthDir);
            }
            if (!Directory.EnumerateFileSystemEntries(yearDir).Any())
                Directory.Delete(yearDir);
        }
        return moved;
    }
}
