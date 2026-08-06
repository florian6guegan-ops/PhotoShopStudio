using System.Text;

namespace Studio.Printing;

/// <summary>
/// Un réglage du pilote lu dans un DEVMODE, et ce qu'il faut en penser.
/// </summary>
/// <param name="Reglage">Nom interne du pilote, ex. <c>PRINTBUFFCONTROL</c>.</param>
/// <param name="Valeur">Option retenue, ex. <c>PBC_NONCLEAR</c>.</param>
/// <param name="Libelle">Ce que l'opérateur lit — vide si l'on ne connaît pas ce réglage.</param>
/// <param name="Avertissement">
/// Ce que ce choix risque de coûter sur le papier, ou vide s'il n'y a rien à dire.
/// </param>
public sealed record ReglagePilote(string Reglage, string Valeur, string Libelle, string Avertissement)
{
    public bool Connu => Libelle.Length > 0;
    public bool Inquietant => Avertissement.Length > 0;
}

/// <summary>
/// Lit — et seulement lit — ce qu'un DEVMODE capturé contient.
///
/// <b>Pourquoi cela existe.</b> Un DEVMODE est un bloc de mille deux cents octets opaques.
/// On le capture au dialogue du pilote, on le rejoue à chaque tirage, et personne ne peut
/// dire ce qu'il y a dedans : ni l'opérateur, ni celui qui dépanne à distance. Quand la
/// DS620 sort des tirages avec les couleurs décalées, la première question — « sur quels
/// réglages tire-t-elle ? » — n'avait aucune réponse.
///
/// <b>Ce qu'on lit.</b> Les pilotes Unidrv, dont celui de la DS620, rangent leurs choix à la
/// fin du DEVMODE sous la forme d'une suite de chaînes ASCII terminées par un zéro, par
/// paires : nom du réglage, puis option retenue. Ce sont exactement les noms du fichier
/// <c>.GPD</c> du pilote (<c>OVERCOATTYPE</c> / <c>OPTYPE_LUSTER</c>…).
///
/// <b>On n'écrit JAMAIS.</b> Les octets privés d'un pilote ne se modifient pas à la main :
/// les chaînes ne sont qu'une table de noms, et la sélection réelle vit ailleurs dans le
/// bloc. Changer un réglage passe par le dialogue du pilote, et par lui seul.
/// </summary>
public static class LectureDevMode
{
    /// <summary>Position des champs publics dont on a besoin (voir la structure DEVMODEW).</summary>
    private const int OffsetDmSize = 68;
    private const int OffsetDmDriverExtra = 70;

    /// <summary>
    /// En dessous, une suite d'octets imprimables n'est pas un nom de réglage mais du
    /// hasard. Le plus court qui nous intéresse — <c>PC</c>, une taille de papier — en fait
    /// deux, mais il n'arrive jamais en tête de paire.
    /// </summary>
    private const int LongueurMinimale = 2;

    /// <summary>
    /// Les réglages du pilote qu'on sait nommer, dans l'ordre où ils apparaissent. Vide si
    /// le DEVMODE n'en porte aucun — beaucoup de pilotes n'en mettent pas, et ce n'est pas
    /// une anomalie.
    /// </summary>
    /// <remarks>
    /// <b>On CHERCHE les noms qu'on connaît, on ne compte pas les chaînes deux par deux.</b>
    /// Le bloc privé ne commence pas par un réglage : le DEVMODE réel de la DS620 l'ouvre
    /// par les marqueurs d'Unidrv (<c>DINU"</c>, <c>SMTJ</c>, <c>RESDLL</c>…), et le
    /// découpage par paires décalait donc tout d'un cran — il annonçait
    /// « OPTYPE_LUSTER = PRINTBUFFCONTROL ». Chercher les noms rend la lecture indifférente
    /// à ce qui précède, à ce qui s'intercale, et à un pilote qui en ajouterait demain.
    /// </remarks>
    public static IReadOnlyList<ReglagePilote> Lire(byte[]? devMode)
    {
        var chaines = Chaines(devMode);
        var lus = new List<ReglagePilote>();

        for (var i = 0; i + 1 < chaines.Count; i++)
        {
            if (!Connus.Contains(chaines[i])) continue;

            // l'option retenue suit immédiatement le nom du réglage
            var (libelle, avertissement) = Traduire(chaines[i], chaines[i + 1]);
            lus.Add(new ReglagePilote(chaines[i], chaines[i + 1], libelle, avertissement));

            i++; // la valeur vient d'être consommée : elle n'est pas un réglage
        }

        return lus;
    }

    /// <summary>
    /// Les réglages qu'on sait reconnaître — ceux du fichier <c>DPDS620.GPD</c> qui
    /// décident de ce qui sort sur le papier.
    ///
    /// Une liste FERMÉE, et c'est voulu : un pilote publie une vingtaine de chaînes dont la
    /// moitié ne sont que des chemins de bibliothèque. Les montrer toutes noierait les
    /// trois qui comptent.
    /// </summary>
    private static readonly HashSet<string> Connus = new(StringComparer.Ordinal)
    {
        "Orientation",
        "Resolution",
        "PrintMargin",
        "OVERCOATTYPE",
        "PRINTBUFFCONTROL",
        "CUTTERCONTROL",
        "PaperSize",
        "MediaType",
        "ColorMode",
    };

    /// <summary>Ce qui mérite d'être dit à l'opérateur, ou une liste vide.</summary>
    public static IReadOnlyList<string> Avertissements(byte[]? devMode) =>
        Lire(devMode).Where(r => r.Inquietant).Select(r => r.Avertissement).ToList();

    /// <summary>Une ligne lisible par réglage connu, pour l'écran et le journal.</summary>
    public static IReadOnlyList<string> Resume(byte[]? devMode) =>
        Lire(devMode).Where(r => r.Connu).Select(r => r.Libelle).ToList();

    /// <summary>
    /// Les chaînes ASCII terminées par un zéro qui vivent dans la partie PRIVÉE du bloc.
    ///
    /// On commence après <c>dmSize</c> : la partie publique porte le nom de l'imprimante en
    /// UTF-16 et des entiers, dont rien ne nous intéresse ici.
    /// </summary>
    private static List<string> Chaines(byte[]? devMode)
    {
        var trouvees = new List<string>();
        if (devMode is null) return trouvees;

        if (devMode.Length < OffsetDmDriverExtra + 2) return trouvees;

        var dmSize = BitConverter.ToUInt16(devMode, OffsetDmSize);
        var extra = BitConverter.ToUInt16(devMode, OffsetDmDriverExtra);

        // un DEVMODE qui s'annonce plus grand qu'il n'est : on s'en tient à ce qu'on a
        var debut = Math.Min(dmSize, devMode.Length);
        var fin = Math.Min(dmSize + extra, devMode.Length);

        var courante = new StringBuilder();

        for (var i = debut; i < fin; i++)
        {
            var octet = devMode[i];

            // ASCII imprimable, et rien d'autre : les octets d'un entier passeraient
            // sinon pour des lettres
            if (octet is >= 0x20 and <= 0x7E)
            {
                courante.Append((char)octet);
                continue;
            }

            if (courante.Length >= LongueurMinimale) trouvees.Add(courante.ToString());
            courante.Clear();
        }

        if (courante.Length >= LongueurMinimale) trouvees.Add(courante.ToString());

        return trouvees;
    }

    /// <summary>Les réglages de <see cref="Connus"/>, dits en français.</summary>
    private static (string Libelle, string Avertissement) Traduire(string reglage, string valeur) =>
        (reglage, valeur) switch
        {
            ("Resolution", "Option1") => ("Qualité d'impression : High-speed",
                "La DS620 tire en mode RAPIDE (dialogue du pilote → Graphique → « Qualité " +
                "d'impression »). C'est le mode où l'entraînement du papier est le plus " +
                "sollicité, donc celui où les passages de couleur risquent le plus de se " +
                "décaler. À passer sur « High-quality »."),

            ("Resolution", "Option2") => ("Qualité d'impression : High-quality", ""),

            // Le dialogue du pilote appelle ce réglage « Réessayer l'impression », et c'est
            // sous ce nom-là qu'il faut le chercher — pas sous « tampon ». Réessayer suppose
            // de GARDER l'image dans la mémoire de la machine, d'où le nom interne.
            ("PRINTBUFFCONTROL", "PBC_NONCLEAR") =>
                ("Réessayer l'impression : activé (l'image reste en mémoire)",
                "La DS620 garde l'image en mémoire d'un tirage à l'autre (dialogue du " +
                "pilote → Caractéristiques de l'imprimante → « Réessayer l'impression » = " +
                "Activer). Ce qui restait du tirage précédent peut alors se voir sur le " +
                "suivant — un fantôme décalé de la même photo. À passer sur « Désactiver »."),

            ("PRINTBUFFCONTROL", "PBC_CLEAR") =>
                ("Réessayer l'impression : désactivé (mémoire vidée entre deux tirages)", ""),

            // Les noms internes du pilote NE DISENT PAS la finition : son dialogue affiche
            // « Brillant » là où le DEVMODE porte OPTYPE_LUSTER (relevé sur copie d'écran le
            // 06/08/2026). On rend donc le nom brut plutôt qu'une traduction inventée — se
            // tromper de finition sur un tirage coûte la feuille.
            ("OVERCOATTYPE", var finition) =>
                ($"Finition de surcouchage (nom pilote : {finition})", ""),

            ("CUTTERCONTROL", "CUT_STANDARD") => ("Découpe : standard", ""),
            ("CUTTERCONTROL", "CUT_2INCH") => ("Découpe : 2 pouces", ""),

            ("PrintMargin", "MarginOn") => ("Bordure : avec marge", ""),
            ("PrintMargin", "MarginOff") => ("Bordure : sans marge (bord à bord)", ""),

            ("ColorMode", "24bpp") => ("Couleur : 24 bits", ""),
            ("ColorMode", "Mono") => ("Couleur : noir et blanc",
                "Ce produit est réglé en NOIR ET BLANC dans le pilote. Les tirages sortiront " +
                "sans couleur, quelle que soit la photo."),

            ("Orientation", "PORTRAIT") => ("Orientation : portrait", ""),
            ("Orientation", "LANDSCAPE_CC270") => ("Orientation : paysage", ""),

            ("PaperSize", var papier) => ($"Format papier du pilote : {papier}", ""),

            _ => ("", ""),
        };
}
