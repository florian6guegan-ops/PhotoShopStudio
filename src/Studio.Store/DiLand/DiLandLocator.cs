using System.Diagnostics;

namespace Studio.Store.DiLand;

/// <summary>
/// Retrouve le dépôt DiLand sur CE poste, sans qu'on ait à le lui dire.
///
/// <b>Pourquoi.</b> Le chemin était écrit en dur — « C:\Program Files (x86)\DiLand
/// Studio 2\… ». Il est juste sur le poste de la boutique et faux partout ailleurs : une
/// installation sur D:, une version « DiLand Studio 3 », un Windows en 64 bits qui range
/// ailleurs, et Studio n'ouvrait plus une seule commande de borne. C'est le premier
/// obstacle à donner l'application à quelqu'un d'autre.
///
/// Quatre pistes, de la plus sûre à la plus large. La première qui porte une base gagne,
/// et l'on n'en essaie pas d'autre.
/// </summary>
public static class DiLandLocator
{
    /// <summary>
    /// Sous-chemin du dépôt à l'intérieur du dossier d'installation de DiLand.
    /// C'est lui qui contient <c>Database.db</c> et le dossier <c>Orders</c>.
    /// </summary>
    private const string SousCheminDepot = @"Data\AllUsersData\Repositories\Default";

    /// <summary>Le processus de DiLand, quand il tourne. Sans l'extension.</summary>
    private const string ProcessusDiLand = "FitEng.DiLand.Studio";

    /// <summary>
    /// Noms de dossier d'installation connus, du plus récent au plus ancien.
    ///
    /// La boutique tourne sous « DiLand Studio 2 » ; les autres sont là pour les postes
    /// qu'on n'a pas sous les yeux. Un nom inconnu se règle à la main dans les paramètres —
    /// c'est le filet, et il faut qu'il existe.
    /// </summary>
    private static readonly string[] NomsConnus =
    [
        "DiLand Studio 4",
        "DiLand Studio 3",
        "DiLand Studio 2",
        "DiLand Studio",
        "DiLand",
    ];

    /// <summary>
    /// Le dépôt à utiliser, ou <c>null</c> si l'on n'a rien trouvé.
    /// </summary>
    /// <param name="cheminConfigure">
    /// Ce que l'opérateur a réglé dans les paramètres. <b>Il l'emporte toujours</b> : lui
    /// seul sait où est son installation, et une détection qui passerait devant son choix
    /// serait impossible à contourner.
    ///
    /// Accepte aussi bien le dossier d'INSTALLATION que le dépôt lui-même : on ne peut pas
    /// demander à quelqu'un de retenir « Data\AllUsersData\Repositories\Default ».
    /// </param>
    public static string? Trouver(string? cheminConfigure = null)
    {
        if (!string.IsNullOrWhiteSpace(cheminConfigure)
            && DepotDe(cheminConfigure) is { } regle)
            return regle;

        return DepuisLeProcessus() ?? DansLesEmplacementsHabituels() ?? SurLesAutresDisques();
    }

    /// <summary>
    /// Ce qu'on rend quand rien n'est trouvé : l'emplacement habituel, pour que le message
    /// d'erreur nomme un chemin plausible au lieu d'un vide.
    /// </summary>
    public static string TrouverOuDefaut(string? cheminConfigure = null) =>
        Trouver(cheminConfigure) ?? DiLandRepository.DefaultRoot;

    /// <summary>
    /// Le dépôt correspondant à un chemin, qu'on lui ait donné le dossier d'installation
    /// de DiLand ou le dépôt directement. <c>null</c> si ni l'un ni l'autre ne tient.
    /// </summary>
    public static string? DepotDe(string chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return null;

        var nettoye = chemin.Trim().TrimEnd('\\', '/');

        // déjà le dépôt ?
        if (EstUnDepot(nettoye)) return nettoye;

        // le dossier d'installation ?
        var candidat = Path.Combine(nettoye, SousCheminDepot);
        return EstUnDepot(candidat) ? candidat : null;
    }

    /// <summary>
    /// Un dossier est un dépôt s'il porte la base OU le dossier des commandes.
    ///
    /// <b>L'un OU l'autre, et c'est voulu</b> : DiLand fermé depuis longtemps peut avoir
    /// purgé sa base alors que les dossiers de commandes sont toujours là — Studio sait
    /// encore en tirer les photos (voir <c>DiLandImporter.Stage</c>). Exiger les deux
    /// ferait déclarer « introuvable » un dépôt parfaitement exploitable.
    /// </summary>
    public static bool EstUnDepot(string chemin)
    {
        try
        {
            return File.Exists(Path.Combine(chemin, "Database.db"))
                || Directory.Exists(Path.Combine(chemin, "Orders"));
        }
        catch (Exception)
        {
            // chemin syntaxiquement invalide, lecteur déconnecté, droits refusés
            return false;
        }
    }

    /// <summary>
    /// La piste la plus sûre : DiLand est lancé, on lui demande d'où il tourne.
    ///
    /// Aucune supposition sur le nom du dossier ni sur le disque — c'est ce qui couvre les
    /// installations qu'on n'a pas prévues.
    /// </summary>
    private static string? DepuisLeProcessus()
    {
        try
        {
            foreach (var processus in Process.GetProcessesByName(ProcessusDiLand))
            {
                using (processus)
                {
                    var executable = processus.MainModule?.FileName;
                    if (executable is null) continue;

                    var dossier = Path.GetDirectoryName(executable);
                    if (dossier is not null && DepotDe(dossier) is { } depot) return depot;
                }
            }
        }
        catch (Exception)
        {
            // MainModule refuse de répondre pour un processus 32 bits vu d'un 64 bits, ou
            // sans les droits : ce n'est qu'une piste parmi d'autres
        }

        return null;
    }

    /// <summary>Les deux « Program Files », avec les noms d'installation connus.</summary>
    private static string? DansLesEmplacementsHabituels()
    {
        string[] bases =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ];

        foreach (var racine in bases.Where(b => !string.IsNullOrEmpty(b)))
            foreach (var nom in NomsConnus)
                if (DepotDe(Path.Combine(racine, nom)) is { } depot)
                    return depot;

        return null;
    }

    /// <summary>
    /// Dernier recours : la racine des autres disques fixes.
    ///
    /// On ne balaie PAS les disques en profondeur — parcourir un disque entier prendrait
    /// des minutes au démarrage. Seules les racines sont regardées, ce qui couvre le cas
    /// courant « installé sur D: » sans rien coûter.
    /// </summary>
    private static string? SurLesAutresDisques()
    {
        DriveInfo[] disques;
        try { disques = DriveInfo.GetDrives(); }
        catch (Exception) { return null; }

        foreach (var disque in disques)
        {
            if (disque.DriveType != DriveType.Fixed) continue;

            bool pret;
            try { pret = disque.IsReady; }
            catch (Exception) { continue; }
            if (!pret) continue;

            foreach (var nom in NomsConnus)
            {
                if (DepotDe(Path.Combine(disque.RootDirectory.FullName, nom)) is { } direct)
                    return direct;

                if (DepotDe(Path.Combine(disque.RootDirectory.FullName, "Program Files (x86)", nom))
                    is { } x86)
                    return x86;

                if (DepotDe(Path.Combine(disque.RootDirectory.FullName, "Program Files", nom))
                    is { } x64)
                    return x64;
            }
        }

        return null;
    }
}
