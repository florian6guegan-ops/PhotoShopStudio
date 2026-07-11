using System.Text;

namespace Studio.Store;

/// <summary>
/// Écritures de fichiers jamais partielles : on écrit un .tmp complet puis on
/// l'échange atomiquement (File.Replace). En cas de coupure de courant, on
/// retrouve soit l'ancienne version intacte, soit la nouvelle — jamais un fichier tronqué.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    /// <summary>Lit le fichier ; si absent mais qu'un .tmp complet traîne (crash entre écriture et échange), l'ignore.</summary>
    public static string? ReadAllTextOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
