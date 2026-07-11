using System.Management;

namespace Studio.Sources;

public sealed record RemovableDrive(string RootPath, string Label, long TotalBytes);

/// <summary>
/// Surveille l'insertion/retrait de supports amovibles (clé USB, carte SD,
/// disque externe) via les événements WMI de volume.
/// </summary>
public sealed class RemovableDriveWatcher : IDisposable
{
    private ManagementEventWatcher? _watcher;

    /// <summary>Déclenché à chaque insertion ou retrait, avec la liste à jour des supports.</summary>
    public event Action<IReadOnlyList<RemovableDrive>>? DrivesChanged;

    public void Start()
    {
        // EventType 2 = arrivée, 3 = retrait
        var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3");
        _watcher = new ManagementEventWatcher(query);
        _watcher.EventArrived += (_, _) => DrivesChanged?.Invoke(GetDrives());
        _watcher.Start();
    }

    /// <summary>Supports amovibles (et disques externes) actuellement montés et lisibles.</summary>
    public static IReadOnlyList<RemovableDrive> GetDrives()
    {
        var drives = new List<RemovableDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                if (!drive.IsReady) continue;
                if (drive.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue; // jamais le disque système
                if (drive.Name.StartsWith("D:", StringComparison.OrdinalIgnoreCase) && drive.DriveType == DriveType.Fixed)
                    continue; // D: est le disque de données du poste, pas un support client

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? (drive.DriveType == DriveType.Removable ? "Support amovible" : "Disque")
                    : drive.VolumeLabel;
                drives.Add(new RemovableDrive(drive.RootDirectory.FullName, $"{label} ({drive.Name.TrimEnd('\\')})", drive.TotalSize));
            }
            catch (IOException) { /* support éjecté pendant l'énumération */ }
            catch (UnauthorizedAccessException) { }
        }
        return drives;
    }

    public void Dispose()
    {
        _watcher?.Stop();
        _watcher?.Dispose();
    }
}
