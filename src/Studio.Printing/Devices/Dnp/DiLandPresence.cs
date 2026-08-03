using System.Diagnostics;

namespace Studio.Printing.Devices.Dnp;

/// <summary>
/// DiLand tient-il le port USB de la DNP ?
///
/// DiLand ouvre la DS620 en exclusif et ne la relâche jamais tant qu'il tourne. Le SDK
/// (CPPCtrl32.dll) ne le signale pas : il se bloque, indéfiniment, et c'est le relais
/// 32 bits qui se retrouve figé — constaté le 31/07/2026. Renoncer à la DNP pour toute
/// la session réglait le blocage mais laissait la machine invisible jusqu'au prochain
/// démarrage, alors que fermer DiLand suffit à la rendre disponible.
///
/// On regarde donc le processus plutôt que d'attendre le SDK : c'est immédiat, sans
/// effet de bord, et ça permet de remontrer la DNP dès que DiLand se ferme.
/// </summary>
public static class DiLandPresence
{
    /// <summary>
    /// Processus principal de DiLand Studio 2, sans l'extension — c'est ce que
    /// <see cref="Process.GetProcessesByName(string)"/> attend.
    ///
    /// L'installation en compte une vingtaine (outils, assistants, lanceur), mais un seul
    /// tient le port : celui de l'application elle-même.
    /// </summary>
    public const string ProcessName = "FitEng.DiLand.Studio";

    /// <summary>
    /// Vrai si DiLand tourne en ce moment.
    ///
    /// Ne lève jamais : l'énumération des processus peut échouer sur une session
    /// verrouillée ou un processus qui meurt pendant la lecture, et une imprimante qu'on
    /// n'arrive pas à qualifier ne doit pas empêcher le reste du bandeau de s'afficher.
    /// </summary>
    public static bool IsRunning()
    {
        Process[]? processus = null;
        try
        {
            processus = Process.GetProcessesByName(ProcessName);
            return processus.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            foreach (var p in processus ?? []) p.Dispose();
        }
    }

    /// <summary>
    /// Les DNP que le spouleur Windows connaît, pour les montrer « en veille » quand le
    /// SDK n'en découvre aucune.
    ///
    /// Cette énumération vit CÔTÉ APPLICATION et surtout pas dans le relais 32 bits :
    /// lister les imprimantes peut rester suspendu quand une file ne répond pas, et le
    /// relais sert les commandes une par une. Placée là-bas, elle a figé une commande de
    /// 41 photos pendant douze minutes le 03/08/2026 — la question posée au minilab
    /// attendait derrière une interrogation DNP qui ne revenait jamais.
    /// </summary>
    /// <param name="delai">Au-delà, on renonce : une liste d'imprimantes ne vaut pas une attente.</param>
    public static IReadOnlyList<DnpPrinterInfo> VuesParWindows(TimeSpan? delai = null)
    {
        var budget = delai ?? TimeSpan.FromSeconds(3);

        var lecture = Task.Run(() =>
        {
            var trouvees = new List<DnpPrinterInfo>();

            foreach (var nom in System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>())
            {
                if (!EstUneDnp(nom)) continue;

                trouvees.Add(new DnpPrinterInfo(
                    PortNumber: -1,
                    SerialNumber: "",
                    FirmwareVersion: "",
                    Status: new DnpStatus(0x80000000u),   // injoignable par le SDK
                    MediaRemaining: 0,
                    MediaInitialCount: 0,
                    MediaSize: DnpMediaSize.None,
                    MediaClass: DnpMediaClass.Unknown,
                    QueuedPrints: 0,
                    LifetimePrints: 0,
                    WindowsQueueName: nom));
            }

            return trouvees;
        });

        try
        {
            return lecture.Wait(budget) ? lecture.Result : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Reconnaît une DNP à son nom de file. Les modèles de la gamme commencent tous par
    /// « DP-DS », « DS6 », « DS8 » ou « QW » ; c'est le nom que pose le pilote du
    /// constructeur, celui de cette boutique étant « DP-DS620 ».
    /// </summary>
    private static bool EstUneDnp(string nomDeFile) =>
        nomDeFile.StartsWith("DP-DS", StringComparison.OrdinalIgnoreCase)
        || nomDeFile.StartsWith("DS6", StringComparison.OrdinalIgnoreCase)
        || nomDeFile.StartsWith("DS8", StringComparison.OrdinalIgnoreCase)
        || nomDeFile.StartsWith("QW", StringComparison.OrdinalIgnoreCase);
}
