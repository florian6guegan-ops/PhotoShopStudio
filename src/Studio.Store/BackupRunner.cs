using System.Diagnostics;

namespace Studio.Store;

/// <summary>Réglages de sauvegarde (config/backup.json). Destination : dossier, disque ou partage réseau (NAS).</summary>
public sealed class BackupConfig
{
    public bool Enabled { get; set; }
    public string Destination { get; set; } = "";
    public int EveryHours { get; set; } = 24;
}

/// <summary>
/// Sauvegarde du dossier de données par robocopy (miroir, reprise incrémentale).
/// Lancée en tâche de fond au démarrage si l'échéance est passée.
/// </summary>
public static class BackupRunner
{
    /// <summary>Décision pure : la sauvegarde est-elle due ?</summary>
    public static bool IsDue(BackupConfig config, DateTimeOffset? lastRun, DateTimeOffset now) =>
        config.Enabled
        && !string.IsNullOrWhiteSpace(config.Destination)
        && (lastRun is null || now - lastRun >= TimeSpan.FromHours(Math.Max(1, config.EveryHours)));

    /// <summary>Lance robocopy si dû. Renvoie true si une sauvegarde a été lancée et s'est bien terminée.</summary>
    public static bool RunIfDue(string dataRoot, BackupConfig config)
    {
        var marker = Path.Combine(dataRoot, "config", "backup-last-run.txt");
        DateTimeOffset? lastRun = File.Exists(marker)
            && DateTimeOffset.TryParse(File.ReadAllText(marker).Trim(), out var parsed) ? parsed : null;
        if (!IsDue(config, lastRun, DateTimeOffset.Now)) return false;

        // /MIR miroir ; /R:1 /W:5 ne s'acharne pas ; /XD exclut caches et fichiers reconstruisibles
        var args = $"\"{dataRoot}\" \"{config.Destination}\" /MIR /R:1 /W:5 /NP /NFL /NDL " +
                   $"/XD \"{Path.Combine(dataRoot, "cache")}\"";
        using var process = Process.Start(new ProcessStartInfo("robocopy", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();

        // robocopy : 0-7 = succès (avec ou sans copies), ≥8 = erreur
        if (process.ExitCode >= 8) return false;
        File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
        return true;
    }
}
