using System.Management;

namespace Studio.Printing;

/// <summary>Ce que la machine peut faire, maintenant.</summary>
public enum PrinterReadyState
{
    /// <summary>Elle accepte un tirage.</summary>
    Ready,

    /// <summary>
    /// Elle existe mais ne peut pas tirer : bourrage, capot ouvert, plus de papier, en
    /// pause, hors ligne. La situation se règle sur la machine et se rétablit d'elle-même.
    /// </summary>
    NotReady,

    /// <summary>La file n'existe plus : produit mal configuré, pilote retiré.</summary>
    Missing,
}

/// <param name="State">Ce que la machine peut faire.</param>
/// <param name="Reason">Ce qu'il faut dire à l'opérateur. Vide quand tout va bien.</param>
public sealed record PrinterCondition(PrinterReadyState State, string Reason)
{
    public bool CanPrint => State == PrinterReadyState.Ready;

    public static readonly PrinterCondition Prete = new(PrinterReadyState.Ready, "");
}

/// <summary>
/// L'imprimante peut-elle tirer maintenant ?
///
/// Sert à mettre une commande EN ATTENTE plutôt qu'à la faire échouer : un capot ouvert
/// ou un rouleau à changer dure deux minutes, et pendant ce temps le travail doit patienter,
/// pas se perdre. Voir <see cref="PendingPrintQueue"/>.
///
/// On lit le spouleur Windows plutôt que les SDK constructeur : c'est la seule source
/// commune aux trois familles de machines de la boutique, et c'est elle qui refusera ou
/// acceptera le travail de toute façon.
/// </summary>
public static class PrinterReadiness
{
    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Codes d'erreur de <c>Win32_Printer.DetectedErrorState</c> qui empêchent un tirage,
    /// avec ce qu'ils veulent dire pour l'opérateur.
    ///
    /// 0 (Unknown) et 2 (No Error) ne sont pas des pannes. Beaucoup de pilotes laissent
    /// l'état à 0 en permanence : on ne peut donc pas exiger 2 pour imprimer.
    /// </summary>
    private static readonly Dictionary<int, string> Pannes = new()
    {
        [1] = "autre erreur",
        [3] = "bourrage papier",
        [4] = "plus de papier",
        [5] = "bac de sortie plein",
        [6] = "problème d'entraînement du papier",
        [7] = "hors ligne",
        [8] = "intervention nécessaire",
        [9] = "bac à chutes plein",
        [10] = "capot ouvert",
        [11] = "problème de consommable",
        [12] = "plus d'encre ou de ruban",
        [13] = "encre ou ruban presque épuisé",
        [14] = "mémoire saturée",
    };

    /// <summary>
    /// État de la file Windows nommée. Une machine qu'on n'arrive pas à interroger est
    /// déclarée PRÊTE : refuser un tirage sur un doute bloquerait la boutique pour une
    /// requête WMI capricieuse, alors que l'envoi, lui, dira la vérité.
    /// </summary>
    public static PrinterCondition Check(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return new PrinterCondition(PrinterReadyState.Missing, "aucune imprimante n'est indiquée pour ce produit");

        try
        {
            // On énumère et on compare EN C#, plutôt que de filtrer en WQL : un nom
            // d'imprimante contenant une apostrophe (« Atelier d'en haut ») cassait la
            // requête, et l'échec silencieux la faisait passer pour prête. Les postes
            // comptent une dizaine de files : l'énumération ne coûte rien.
            using var recherche = new ManagementObjectSearcher(new SelectQuery("Win32_Printer"));
            using var resultats = recherche.Get();

            foreach (var objet in resultats.Cast<ManagementObject>())
            {
                using (objet)
                {
                    if (Lire(objet, "Name") is not string nom
                        || !nom.Equals(printerName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return Juger(objet, printerName);
                }
            }

            return new PrinterCondition(PrinterReadyState.Missing,
                $"l'imprimante « {printerName} » n'existe pas sur ce poste");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"État de « {printerName} » illisible ({ex.Message}) : on la suppose prête.");
            return PrinterCondition.Prete;
        }
    }

    private static PrinterCondition Juger(ManagementObject imprimante, string printerName)
    {
        if (Lire(imprimante, "WorkOffline") is true)
            return Pas("elle est marquée « hors ligne » dans Windows");

        // en pause : les travaux s'empilent dans la file sans jamais sortir
        if (Lire(imprimante, "PrinterState") is uint etat && (etat & 0x1) != 0)
            return Pas("son impression est en pause");

        if (Lire(imprimante, "DetectedErrorState") is ushort code
            && Pannes.TryGetValue(code, out var panne))
            return Pas(panne);

        return PrinterCondition.Prete;

        PrinterCondition Pas(string raison) =>
            new(PrinterReadyState.NotReady, $"« {printerName} » : {raison}");
    }

    private static object? Lire(ManagementObject objet, string propriete)
    {
        try { return objet[propriete]; }
        catch (ManagementException) { return null; }
    }
}
