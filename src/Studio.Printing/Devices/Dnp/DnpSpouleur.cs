using System.Management;

namespace Studio.Printing.Devices.Dnp;

/// <summary>Ce que le spouleur Windows dit d'une file d'impression DNP.</summary>
public enum EtatFileDnp
{
    /// <summary>Le spouleur n'a rien à en dire — file absente, ou lecture impossible.</summary>
    Inconnu,

    /// <summary>Rien en cours, rien à signaler : un tirage envoyé maintenant partira.</summary>
    Prete,

    /// <summary>Des tirages sont dans la file, ou la machine est en train d'en sortir un.</summary>
    Impression,

    /// <summary>Un tirage attend, mais la file est suspendue : rien ne sortira tant qu'on ne la relance pas.</summary>
    EnPause,

    /// <summary>Windows la déclare hors ligne : câble, alimentation, ou pilote.</summary>
    HorsLigne,

    /// <summary>Papier, ruban, capot, bourrage — quelque chose demande l'opérateur.</summary>
    Erreur,
}

/// <summary>
/// L'état d'une file DNP vu par le spouleur, et ce qu'il lui reste à sortir.
/// </summary>
/// <param name="Nom">Nom de la file Windows, ex. « DP-DS620 ».</param>
/// <param name="Etat">Ce que le spouleur en dit.</param>
/// <param name="PhotosRestantes">
/// Pages encore à sortir, toutes commandes confondues.
///
/// <b>Une page = une photo</b> sur cette machine : <see cref="BitmapPrinter"/> envoie un
/// travail d'une seule page par tirage, et <c>PrintOrchestrator</c> le répète pour chaque
/// exemplaire. Le compte du spouleur est donc directement le nombre de photos qui reste à
/// sortir — c'est ce que l'opérateur regarde pour savoir s'il a le temps de servir
/// quelqu'un d'autre.
/// </param>
/// <param name="TravauxEnAttente">Nombre de travaux dans la file, pour le diagnostic.</param>
/// <param name="Message">Ce qui ne va pas, en clair, ou vide.</param>
public sealed record EtatSpouleurDnp(
    string Nom,
    EtatFileDnp Etat,
    int PhotosRestantes,
    int TravauxEnAttente,
    string Message)
{
    public static EtatSpouleurDnp Inconnu(string nom) =>
        new(nom, EtatFileDnp.Inconnu, 0, 0, "");
}

/// <summary>
/// L'état d'une DNP tel que le SPOULEUR WINDOWS le connaît.
///
/// <b>Pourquoi cette lecture existe.</b> Le SDK DNP (<c>CPPCtrl32.dll</c>) ne peut pas
/// ouvrir la DS620 tant que DiLand tourne — il tient le port USB en exclusif, et le SDK se
/// bloque au lieu de le dire (voir <see cref="DiLandPresence"/>). Or DiLand tourne
/// pratiquement en permanence en boutique : c'est lui qui reçoit les commandes des bornes.
/// L'écran d'état affichait donc « En veille » <b>en continu</b>, machine allumée, prête,
/// et en train de tirer — signalé par l'exploitant le 04/08/2026.
///
/// Le spouleur, lui, répond toujours : c'est par lui que Studio imprime sur cette machine,
/// et il voit ce qu'aucun SDK bloqué ne peut voir — la file, son avancement, et les pannes
/// que le pilote remonte.
///
/// <b>Ce qu'il ne sait pas</b>, et qu'il ne faut pas lui demander : le rouleau restant, le
/// numéro de série, le micrologiciel. Ceux-là ne viennent que du SDK, donc DiLand fermé.
/// </summary>
public static class DnpSpouleur
{
    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Au-delà, on renonce.
    ///
    /// WMI interroge le service spouleur ; une file qui ne répond pas peut le faire
    /// attendre. Un bandeau d'état ne vaut pas de figer l'écran de l'opérateur.
    /// </summary>
    public static readonly TimeSpan Delai = TimeSpan.FromSeconds(3);

    /// <summary>
    /// L'état d'une file, ou <see cref="EtatSpouleurDnp.Inconnu"/> si le spouleur n'a rien
    /// donné. Ne lève jamais : c'est un affichage, il ne doit rien empêcher.
    /// </summary>
    public static EtatSpouleurDnp Lire(string nomDeFile)
    {
        if (string.IsNullOrWhiteSpace(nomDeFile)) return EtatSpouleurDnp.Inconnu(nomDeFile ?? "");

        var lecture = Task.Run(() => Interroger(nomDeFile));

        try
        {
            return lecture.Wait(Delai) ? lecture.Result : EtatSpouleurDnp.Inconnu(nomDeFile);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Spouleur : lecture de « {nomDeFile} » impossible — {ex.Message}");
            return EtatSpouleurDnp.Inconnu(nomDeFile);
        }
    }

    private static EtatSpouleurDnp Interroger(string nomDeFile)
    {
        try
        {
            var (restantes, travaux, enPause) = LireLaFile(nomDeFile);
            return LireLImprimante(nomDeFile, restantes, travaux, enPause);
        }
        catch (ManagementException ex)
        {
            Log?.Invoke($"Spouleur : WMI a refusé « {nomDeFile} » — {ex.Message}");
            return EtatSpouleurDnp.Inconnu(nomDeFile);
        }
        catch (UnauthorizedAccessException)
        {
            return EtatSpouleurDnp.Inconnu(nomDeFile);
        }
    }

    /// <summary>
    /// Ce qui reste dans la file, en PAGES.
    ///
    /// <c>TotalPages − PagesPrinted</c> et non le nombre de travaux : un travail en cours
    /// est à moitié sorti, et le compter entier ferait stagner l'affichage. Un travail dont
    /// le pilote ne renseigne pas les pages compte pour un — mieux vaut un de trop qu'un
    /// tirage invisible.
    /// </summary>
    private static (int Restantes, int Travaux, bool EnPause) LireLaFile(string nomDeFile)
    {
        var restantes = 0;
        var travaux = 0;
        var enPause = false;

        // Win32_PrintJob.Name vaut « <file>, <numéro> » : c'est la seule clé qui rattache
        // un travail à sa file.
        using var recherche = new ManagementObjectSearcher(
            new SelectQuery("Win32_PrintJob", ClauseDeFile(nomDeFile),
                ["Name", "TotalPages", "PagesPrinted", "StatusMask"]));

        foreach (var travail in recherche.Get().Cast<ManagementObject>())
        {
            using (travail)
            {
                if (travail["Name"] is not string nom) continue;
                if (!nom.StartsWith(nomDeFile + ",", StringComparison.OrdinalIgnoreCase)) continue;

                travaux++;

                var total = Entier(travail["TotalPages"]);
                var sorties = Entier(travail["PagesPrinted"]);
                restantes += total > 0 ? Math.Max(0, total - sorties) : 1;

                // bit 0x0001 = PAUSED, non traduit — contrairement à JobStatus, qui est du
                // texte localisé et qu'on ne peut donc pas comparer
                if ((Entier(travail["StatusMask"]) & 0x0001) != 0) enPause = true;
            }
        }

        return (restantes, travaux, enPause);
    }

    /// <summary>
    /// Le tri des travaux confié à WMI plutôt que fait ici, sur tout ce qu'il a renvoyé.
    ///
    /// <b>Ce n'est pas une coquetterie de requête.</b> Cette lecture est sur le chemin
    /// d'impression : <c>CadenceSpouleur</c> l'appelle avant CHAQUE photo, et
    /// <c>PagesSorties</c> une seconde fois juste après. Sans clause, WMI construisait et
    /// transmettait un objet par travail de TOUT le poste — DiLand en laisse couramment
    /// plusieurs dizaines dans la file de la DS620 — pour n'en garder ensuite qu'une
    /// poignée. Sur une commande de six cents photos, cela se compte en milliers d'objets
    /// COM créés et détruits pour rien, pendant que la machine attend du papier.
    ///
    /// <b>La clause ne fait que PRÉ-trier</b> : le <c>StartsWith</c> qui suit reste la règle
    /// qui tranche. C'est ce qui permet de renoncer à la clause sans conséquence quand le
    /// nom porte un caractère que <c>LIKE</c> interpréterait (<c>%</c>, <c>_</c>, crochets) —
    /// on lit alors tout, comme avant, plutôt que de risquer de manquer un travail.
    /// </summary>
    private static string? ClauseDeFile(string nomDeFile)
    {
        if (nomDeFile.AsSpan().IndexOfAny("%_[]") >= 0) return null;

        return $"Name LIKE '{nomDeFile.Replace("'", "''")},%'";
    }

    private static EtatSpouleurDnp LireLImprimante(
        string nomDeFile, int restantes, int travaux, bool travauxEnPause)
    {
        using var recherche = new ManagementObjectSearcher(
            new SelectQuery("Win32_Printer",
                $"Name = '{nomDeFile.Replace("'", "''")}'",
                ["Name", "PrinterStatus", "WorkOffline", "DetectedErrorState"]));

        var imprimante = recherche.Get().Cast<ManagementObject>().FirstOrDefault();
        if (imprimante is null) return EtatSpouleurDnp.Inconnu(nomDeFile);

        using (imprimante)
        {
            var (etat, panne) = Decider(
                Entier(imprimante["PrinterStatus"]),
                imprimante["WorkOffline"] as bool? ?? false,
                Entier(imprimante["DetectedErrorState"]),
                restantes,
                travauxEnPause);

            return new EtatSpouleurDnp(nomDeFile, etat, restantes, travaux, panne);
        }
    }

    /// <summary>
    /// La décision d'état, séparée de WMI pour être vérifiable.
    ///
    /// <b>L'ordre compte</b> : une machine en panne reste en panne même si des travaux
    /// patientent derrière, et c'est la panne qu'il faut lire en premier. De même, une file
    /// en pause n'est pas « en cours d'impression » alors que rien n'en sortira.
    /// </summary>
    internal static (EtatFileDnp Etat, string Message) Decider(
        int printerStatus, bool workOffline, int detectedErrorState,
        int restantes, bool travauxEnPause)
    {
        var panne = Panne(detectedErrorState);

        var etat =
            workOffline || printerStatus == StatutHorsLigne ? EtatFileDnp.HorsLigne
            : panne.Length > 0 ? EtatFileDnp.Erreur
            : travauxEnPause ? EtatFileDnp.EnPause
            : restantes > 0 || printerStatus is StatutImpression or StatutPrechauffage
                ? EtatFileDnp.Impression
            // Arrivé ici, on SAIT que : le spouleur répond, la file n'est pas hors ligne,
            // le pilote ne signale aucune panne, et rien n'attend. Un tirage envoyé
            // maintenant partirait — c'est la définition de « prête ». « Autre » et
            // « inconnu » sont donc traités comme « prête » plutôt que comme un état
            // mystérieux : beaucoup de pilotes ne renseignent pas mieux que ça, et
            // afficher « état inconnu » sur une machine qui marche est précisément ce
            // qu'on reproche à l'ancien « en veille ».
            : printerStatus is StatutPret or StatutAutre or StatutInconnu ? EtatFileDnp.Prete
            : EtatFileDnp.Inconnu;

        return (etat, panne);
    }

    // Win32_Printer.PrinterStatus — les seules valeurs sur lesquelles on s'appuie.
    private const int StatutAutre = 1;
    private const int StatutInconnu = 2;
    private const int StatutPret = 3;
    private const int StatutImpression = 4;
    private const int StatutPrechauffage = 5;
    private const int StatutHorsLigne = 7;

    /// <summary>
    /// <c>Win32_Printer.PrinterState</c> n'est PAS lu, et c'est délibéré.
    ///
    /// La DP-DS620 de la boutique le laisse à 0, ce que la documentation traduit par
    /// « Paused » — alors qu'elle est allumée, prête, et qu'elle imprime. Beaucoup de
    /// pilotes ne le renseignent pas ; s'y fier remettrait exactement le défaut qu'on
    /// corrige. Relevé le 04/08/2026 : <c>PrinterStatus = 3</c> (prête),
    /// <c>WorkOffline = False</c>, <c>PrinterState = 0</c>.
    ///
    /// <c>DetectedErrorState</c>, lui, est fiable et non traduit.
    /// </summary>
    private static string Panne(int detectedErrorState) => detectedErrorState switch
    {
        3 => "Bourrage papier.",
        4 => "Plus de papier.",
        5 => "Bac de sortie plein.",
        6 => "Problème de papier.",
        7 => "Machine hors ligne.",
        8 => "Intervention demandée sur la machine.",
        9 => "Ruban épuisé.",
        10 => "Capot ouvert.",
        11 => "Erreur du service.",
        _ => "",   // 0 « inconnu » et 2 « aucune erreur » : rien à signaler
    };

    private static int Entier(object? valeur) => valeur switch
    {
        int i => i,
        uint u => (int)u,
        ushort us => us,
        short s => s,
        long l => (int)l,
        _ => 0,
    };

    /// <summary>
    /// Supprime tout ce qui attend dans la file Windows d'une imprimante.
    ///
    /// <b>Le geste de dernier recours</b>, et le seul qui débloque certaines situations :
    /// le 04/08/2026, trois travaux sont restés deux heures dans la file de la DS620 sans
    /// jamais imprimer une page — deux d'entre eux venus de DiLand. La machine se déclarait
    /// prête, aucune erreur n'était signalée, et rien ne sortait. Il fallait passer par les
    /// fenêtres d'impression de Windows pour s'en sortir.
    ///
    /// <b>Ce qui est supprimé ne revient pas.</b> L'appelant DOIT donc demander
    /// confirmation, et dire ce qu'il efface. Les tirages perdus se refont depuis
    /// « Commandes du jour ».
    /// </summary>
    /// <param name="nomDeFile">Nom de la file Windows.</param>
    /// <returns>Le nombre de travaux supprimés, ou -1 si la file n'a pas répondu.</returns>
    public static int Vider(string nomDeFile)
    {
        if (string.IsNullOrWhiteSpace(nomDeFile)) return -1;

        // ce qu'il y avait AVANT : après la purge, la file est vide et on ne pourrait plus
        // le dire à l'opérateur
        var (restantes, travaux, _) = LireLaFile(nomDeFile);

        // Chaque travail est supprimé PAR SON CHEMIN, un par un.
        //
        // `Win32_Printer.CancelAllJobs` semblait plus direct, mais il échoue avec
        // « Operation is not valid due to the current state of the object » dès que
        // l'instance vient d'une requête à propriétés restreintes : sans son chemin
        // complet, WMI refuse d'invoquer une méthode dessus. Constaté le 04/08/2026.
        //
        // La suppression travail par travail a de toute façon deux avantages : elle
        // fonctionne aussi quand un seul travail est fautif, et elle dit combien elle en a
        // réellement retiré.
        var supprimes = 0;
        var echecs = 0;

        try
        {
            using var recherche = new ManagementObjectSearcher(
                new SelectQuery("Win32_PrintJob", ClauseDeFile(nomDeFile)));

            foreach (var travail in recherche.Get().Cast<ManagementObject>())
            {
                using (travail)
                {
                    if (travail["Name"] is not string nom) continue;
                    if (!nom.StartsWith(nomDeFile + ",", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        travail.Delete();
                        supprimes++;
                    }
                    catch (Exception ex)
                    {
                        // un travail que Windows tient encore : on continue, les autres
                        // partiront quand même
                        echecs++;
                        Log?.Invoke($"Spouleur : « {nom} » n'a pas pu être supprimé — {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Spouleur : impossible de lire la file « {nomDeFile} » — {ex.Message}");
            return -1;
        }

        Log?.Invoke($"Spouleur : file « {nomDeFile} » — {supprimes} travail/travaux supprimé(s) " +
                    $"sur {travaux}, {restantes} page(s) qui ne sortiront pas" +
                    (echecs > 0 ? $", {echecs} refus" : "") + ".");

        return supprimes;
    }

    /// <summary>Le libellé montré à l'opérateur, avec ce qui reste à sortir.</summary>
    public static string Decrire(EtatSpouleurDnp etat)
    {
        ArgumentNullException.ThrowIfNull(etat);

        return etat.Etat switch
        {
            EtatFileDnp.Impression => etat.PhotosRestantes switch
            {
                0 => "Impression en cours",
                1 => "Impression en cours — 1 photo restante",
                _ => $"Impression en cours — {etat.PhotosRestantes} photos restantes",
            },
            EtatFileDnp.EnPause => etat.PhotosRestantes <= 1
                ? "File en pause — 1 photo en attente"
                : $"File en pause — {etat.PhotosRestantes} photos en attente",
            EtatFileDnp.Prete => "Prête à imprimer",
            EtatFileDnp.HorsLigne => "Hors ligne",
            EtatFileDnp.Erreur => etat.Message.Length > 0 ? etat.Message : "Intervention nécessaire",
            _ => "État inconnu",
        };
    }
}
