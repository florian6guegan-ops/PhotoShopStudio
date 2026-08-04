using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;

namespace Studio.Printing;

/// <summary>Ce que Studio doit FAIRE d'un état de machine.</summary>
public enum Conduite
{
    /// <summary>Rien à signaler : on imprime.</summary>
    Continuer,

    /// <summary>
    /// État passager, la machine s'en sortira seule — refroidissement, traitement d'image,
    /// mise à jour. On attend sans rien dire d'alarmant.
    /// </summary>
    Patienter,

    /// <summary>
    /// Il faut un geste de l'opérateur, et il dure deux minutes : rouleau, ruban, capot,
    /// bourrage. La commande part EN ATTENTE et repartira toute seule.
    /// </summary>
    MettreEnAttente,

    /// <summary>
    /// La file du spouleur est bloquée par des travaux qui ne sortiront jamais. Rien ne
    /// repartira tant qu'on ne l'a pas vidée.
    /// </summary>
    ViderLaFile,

    /// <summary>
    /// Inutile d'insister : la commande ne passera pas en l'état, et la réessayer ne fera
    /// que gâcher du temps. C'est une panne, une configuration, ou un tirage refusé.
    /// </summary>
    Arreter,
}

/// <summary>
/// Ce qu'on dit à l'opérateur et ce qu'on fait.
/// </summary>
/// <param name="Conduite">La décision.</param>
/// <param name="Quoi">L'état, en une phrase.</param>
/// <param name="Geste">Ce qu'il y a à faire ; vide quand il n'y a rien à faire.</param>
public sealed record Consigne(Conduite Conduite, string Quoi, string Geste)
{
    /// <summary>Le message complet : l'état, puis le geste.</summary>
    public string Message => Geste.Length == 0 ? Quoi : $"{Quoi} — {Geste}";
}

/// <summary>
/// La conduite à tenir devant chaque état de machine, pour les deux familles de la
/// boutique.
///
/// <b>Pourquoi cette table existe.</b> Les états étaient traduits en français à trois
/// endroits différents — <see cref="DnpStatus.Message"/>, <see cref="DnpSpouleur.Decrire"/>,
/// <see cref="De100JobTracker.Describe"/> — et aucun ne disait ce qu'il FALLAIT FAIRE. Un
/// opérateur devant « erreur signalée par le minilab » ou « Intervention nécessaire » ne
/// sait ni s'il doit attendre, ni s'il doit toucher la machine, ni si sa commande est
/// perdue. Demandé par l'exploitant le 04/08/2026, après une journée passée à deviner.
///
/// La règle qui la gouverne : <b>tout état doit tomber dans l'une des cinq conduites</b>.
/// Un état inconnu vaut <see cref="Conduite.MettreEnAttente"/> — on préfère faire patienter
/// une commande à tort que la déclarer perdue.
/// </summary>
public static class ConduiteMachine
{
    // ————— Minilab Fuji DE100 —————

    /// <summary>L'état global d'une machine du minilab.</summary>
    public static Consigne PourLeMinilab(De100PrinterStatus etat) => etat switch
    {
        De100PrinterStatus.Ready =>
            new(Conduite.Continuer, "Prête", ""),

        De100PrinterStatus.Printing or De100PrinterStatus.Busy =>
            new(Conduite.Patienter, "Occupée", "elle finit ce qu'elle a commencé"),

        // Une machine en veille se réveille au premier tirage : c'est l'état NORMAL d'une
        // machine peu sollicitée, et surtout pas une panne. Mais le réveil prend une
        // dizaine de secondes, pendant lesquelles elle ne répond pas — d'où l'attente.
        De100PrinterStatus.Sleep =>
            new(Conduite.Patienter, "En veille",
                "elle se réveille toute seule au premier tirage"),

        De100PrinterStatus.ErrorProcessingCanBeContinued =>
            new(Conduite.MettreEnAttente, "Erreur, reprise possible",
                "réglez ce que la machine affiche, la commande repartira seule"),

        De100PrinterStatus.ErrorProcessingCannotBeContinued =>
            new(Conduite.Arreter, "Erreur, machine arrêtée",
                "il faut intervenir sur la machine avant de relancer quoi que ce soit"),

        De100PrinterStatus.Offline =>
            new(Conduite.MettreEnAttente, "Hors ligne",
                "vérifiez qu'elle est allumée et raccordée au réseau"),

        _ => new(Conduite.MettreEnAttente, $"État inconnu ({(int)etat})",
                 "regardez l'écran de la machine"),
    };

    /// <summary>
    /// L'issue d'une COMMANDE au minilab, motif de la machine compris.
    /// </summary>
    /// <param name="etat">Statut rendu par le SDK.</param>
    /// <param name="motif">
    /// <c>ST_PRINT_INFO.errmsg</c>, en anglais et souvent vide. Quand il est là, il vaut
    /// mieux que toute traduction : c'est la machine qui parle.
    /// </param>
    public static Consigne PourUnTirage(De100OrderStatus etat, string motif = "")
    {
        var dit = (motif ?? "").Trim();

        return etat switch
        {
            De100OrderStatus.Complete =>
                new(Conduite.Continuer, "Tirage sorti", ""),

            De100OrderStatus.PrintWaiting or De100OrderStatus.Printing
                or De100OrderStatus.ImageProcessWaiting or De100OrderStatus.ImageProcessing =>
                new(Conduite.Patienter, "En cours", ""),

            De100OrderStatus.Hold =>
                new(Conduite.MettreEnAttente, "Commande suspendue à la machine",
                    "elle attend un geste sur le minilab lui-même"),

            De100OrderStatus.Busy =>
                new(Conduite.MettreEnAttente, "Minilab occupé, commande non prise",
                    "elle repartira dès qu'il aura fini"),

            De100OrderStatus.Canceled =>
                new(Conduite.Arreter, "Commande annulée", ""),

            // Le cas qui a coûté une journée entière : la machine refuse et ne dit rien.
            // Quand elle dit quelque chose, on le répète mot pour mot ; sinon on nomme la
            // seule piste qui reste, celle qui s'est vérifiée sur le 21×29,7.
            De100OrderStatus.Error when dit.Length > 0 =>
                new(Conduite.Arreter, "Tirage refusé", dit),

            De100OrderStatus.Error =>
                new(Conduite.Arreter, "Tirage refusé sans motif",
                    "la définition de l'image ne correspond probablement pas à ce que la " +
                    "machine attend pour ce format ; voyez le journal, qui compare les deux"),

            _ => new(Conduite.MettreEnAttente, $"État inconnu ({(int)etat})",
                     "regardez l'écran de la machine"),
        };
    }

    /// <summary>
    /// Un événement machine du minilab (bourrage, capot, consommable).
    ///
    /// Le SDK donne le texte en ANGLAIS et il est déjà explicite — « Cartridge cover (left)
    /// open. Close the cartridge cover (left). » On le répète tel quel plutôt que de le
    /// traduire à moitié : c'est ce que l'opérateur lira aussi sur l'écran de la machine.
    /// </summary>
    public static Consigne PourUnEvenement(De100ErrorLevel niveau, string message)
    {
        var dit = (message ?? "").Trim();

        return niveau switch
        {
            De100ErrorLevel.Information or De100ErrorLevel.Warning =>
                new(Conduite.Patienter, dit.Length > 0 ? dit : "Avertissement de la machine", ""),

            De100ErrorLevel.Error =>
                new(Conduite.MettreEnAttente, dit.Length > 0 ? dit : "Erreur de la machine",
                    "la commande repartira seule une fois réglé"),

            De100ErrorLevel.SystemError or De100ErrorLevel.SoftwareError =>
                new(Conduite.Arreter, dit.Length > 0 ? dit : "Erreur système de la machine",
                    "redémarrez le minilab ; si cela revient, appelez le SAV"),

            _ => new(Conduite.Patienter, dit, ""),
        };
    }

    // ————— DNP DS620 —————

    /// <summary>
    /// L'état d'une DNP vu par SON SDK. Ne vaut que DiLand fermé : il tient le port USB en
    /// exclusif le reste du temps — voir <see cref="DnpSpouleur"/>.
    /// </summary>
    public static Consigne PourLaDnp(DnpStatus etat)
    {
        ArgumentNullException.ThrowIfNull(etat);

        if (etat.IsCommunicationFailure)
            return new(Conduite.MettreEnAttente, etat.Message,
                "fermez DiLand s'il tourne : il tient le port USB en exclusif");

        if (etat.IsReady) return new(Conduite.Continuer, etat.Message, "");

        if (etat.IsBusy) return new(Conduite.Patienter, etat.Message, "");

        if (etat.NeedsOperator)
            return new(Conduite.MettreEnAttente, etat.Message,
                "la commande repartira toute seule une fois le consommable changé");

        if (etat.IsFault)
            return new(Conduite.Arreter, etat.Message,
                "éteignez et rallumez la machine ; si cela revient, appelez le SAV");

        return new(Conduite.MettreEnAttente, etat.Message, "regardez la machine");
    }

    /// <summary>
    /// L'état d'une file d'impression Windows — la seule lecture possible quand DiLand
    /// tient le port USB, c'est-à-dire presque toujours en boutique.
    /// </summary>
    /// <param name="etat">Ce que le spouleur dit de la file.</param>
    /// <param name="pagesEnFile">Pages en attente.</param>
    /// <param name="minutesSansProgres">
    /// Depuis combien de temps la file n'a pas bougé. Au-delà de
    /// <see cref="MinutesAvantDeViderLaFile"/>, les travaux sont morts : le 04/08/2026,
    /// trois d'entre eux ont bloqué la DS620 pendant deux heures alors qu'elle se
    /// déclarait prête et sans erreur.
    /// </param>
    public static Consigne PourLaFile(EtatFileDnp etat, int pagesEnFile, int minutesSansProgres = 0)
    {
        // Une file qui n'avance plus l'emporte sur tout le reste : c'est le seul cas où la
        // machine ment. Elle se dit prête, ne signale aucune erreur, et rien ne sort.
        if (pagesEnFile > 0 && minutesSansProgres >= MinutesAvantDeViderLaFile)
            return new(Conduite.ViderLaFile,
                $"{pagesEnFile} tirage(s) bloqués depuis {minutesSansProgres} min",
                "videz la file depuis le bandeau des machines, puis réimprimez depuis " +
                "« Commandes du jour »");

        return etat switch
        {
            EtatFileDnp.Prete => new(Conduite.Continuer, "Prête à imprimer", ""),

            EtatFileDnp.Impression => new(Conduite.Patienter,
                pagesEnFile > 0 ? $"Impression en cours, {pagesEnFile} restante(s)" : "Impression en cours",
                ""),

            EtatFileDnp.EnPause => new(Conduite.MettreEnAttente, "File en pause",
                "relancez l'impression dans les fenêtres d'impression de Windows"),

            EtatFileDnp.HorsLigne => new(Conduite.MettreEnAttente, "Hors ligne",
                "vérifiez l'alimentation et le câble USB"),

            EtatFileDnp.Erreur => new(Conduite.MettreEnAttente, "Intervention nécessaire",
                "la commande repartira seule une fois la machine dégagée"),

            _ => new(Conduite.MettreEnAttente, "État inconnu", "regardez la machine"),
        };
    }

    /// <summary>
    /// Au-delà, une file qui n'a pas bougé est considérée comme morte.
    ///
    /// Large à dessein : une DS620 sort un 10×15 en une quinzaine de secondes, mais une
    /// grosse commande peut la faire souffler. Dix minutes sans le moindre progrès, en
    /// revanche, ne s'expliquent par rien de normal.
    /// </summary>
    public const int MinutesAvantDeViderLaFile = 10;
}
