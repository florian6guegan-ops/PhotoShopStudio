using System.Drawing.Printing;

namespace Studio.Printing;

/// <summary>Ce à quoi sert une imprimante dans le laboratoire.</summary>
public enum RoleImprimante
{
    /// <summary>Ni l'un ni l'autre : bureautique, PDF, télécopie…</summary>
    Aucun,

    /// <summary>Les agrandissements : jet d'encre grand format.</summary>
    GrandFormat,

    /// <summary>Sublimation thermique : DNP, Citizen, Mitsubishi, Sinfonia, HiTi.</summary>
    Sublimation,

    /// <summary>
    /// Minilab Fuji. Présent dans Windows, mais piloté par son SDK et non par le
    /// spouleur — sa file est branchée sur le port « nul ».
    /// </summary>
    Minilab,
}

/// <summary>Une imprimante vue par Windows, et le rôle qu'on lui prête.</summary>
/// <param name="Nom">Nom de la file Windows, tel qu'il faut le donner au spouleur.</param>
/// <param name="Role">Ce à quoi elle sert.</param>
/// <param name="Motif">Ce qui l'a fait reconnaître — à montrer dans les paramètres.</param>
public sealed record ImprimanteDetectee(string Nom, RoleImprimante Role, string Motif);

/// <summary>
/// Reconnaît les imprimantes du laboratoire parmi les files de Windows.
///
/// <b>Pourquoi.</b> Les machines étaient désignées en dur : « SC-P800 » cherché dans le nom
/// pour les agrandissements, « DS620 » dans le catalogue. Sur le poste de la boutique
/// Windows nomme pourtant la première <c>EPSONFECE59 (SC-P800 Series)</c> — cela marchait
/// par chance. Chez un collègue équipé d'une P700, d'une DS-RX1 ou d'une Citizen, plus
/// rien n'était trouvé, et rien ne le disait.
///
/// <b>On reconnaît par FAMILLE, pas par modèle.</b> Une boutique ne rachète pas la même
/// référence : ce qui se répète, c'est la marque et la gamme. « SureColor », « DS », « CP »
/// couvrent des générations entières, là où « SC-P800 » ne couvre qu'un exemplaire.
///
/// <b>La détection ne décide jamais seule.</b> Elle propose ; le réglage du poste
/// (<c>PosteSettings</c>) l'emporte quand il est renseigné. C'est le filet pour la machine
/// qu'on n'a pas prévue, et il doit exister — aucune liste de motifs ne sera jamais
/// complète.
/// </summary>
public static class DetectionImprimantes
{
    /// <summary>
    /// Files qui ne mènent à aucun papier. Écartées d'abord : « Microsoft Print to PDF »
    /// contient « Print », et « Send to Sawgrass Print Utility » ressemble à une vraie
    /// machine.
    /// </summary>
    private static readonly string[] Virtuelles =
    [
        "print to pdf", "xps document writer", "onenote", "fax", "send to",
        "adobe pdf", "pdfcreator", "microsoft print",
    ];

    /// <summary>
    /// Jet d'encre grand format. <b>Le photocopieur de bureau n'en est pas un</b> : le
    /// Canon iR-ADV du magasin sort du A3 sur papier ordinaire, et proposer un
    /// agrandissement dessus ferait perdre un tirage. On ne retient donc que les gammes
    /// PHOTO, jamais une marque entière.
    /// </summary>
    private static readonly (string Motif, string Explication)[] MotifsGrandFormat =
    [
        ("surecolor", "Epson SureColor"),
        ("sc-p", "Epson SureColor P"),
        ("stylus pro", "Epson Stylus Pro"),
        ("imageprograf", "Canon imagePROGRAF"),
        ("ipf", "Canon imagePROGRAF"),
        ("pro-1000", "Canon PRO"),
        ("pro-300", "Canon PRO"),
        ("pixma pro", "Canon PIXMA PRO"),
        ("designjet", "HP DesignJet"),
    ];

    /// <summary>Sublimation thermique : les cinq constructeurs qu'on croise en boutique.</summary>
    private static readonly (string Motif, string Explication)[] MotifsSublimation =
    [
        ("ds620", "DNP DS620"),
        ("ds820", "DNP DS820"),
        ("ds-rx1", "DNP DS-RX1"),
        ("dsrx1", "DNP DS-RX1"),
        ("qw410", "DNP QW410"),
        ("dp-ds", "DNP"),
        ("citizen", "Citizen"),
        ("cx-02", "Citizen CX-02"),
        ("mitsubishi", "Mitsubishi"),
        ("cp-d70", "Mitsubishi CP-D70"),
        ("cp-k60", "Mitsubishi CP-K60"),
        ("sinfonia", "Sinfonia"),
        ("s2145", "Sinfonia S2145"),
        ("hiti", "HiTi"),
    ];

    /// <summary>Minilab Fuji — reconnu, mais piloté par son SDK.</summary>
    private static readonly (string Motif, string Explication)[] MotifsMinilab =
    [
        ("de100", "Fujifilm DE100"),
        ("frontier", "Fujifilm Frontier"),
        ("dx100", "Fujifilm DX100"),
    ];

    /// <summary>
    /// Toutes les files de Windows, avec le rôle qu'on leur prête.
    ///
    /// Rend une liste vide plutôt que de lever : un spouleur en panne ne doit pas empêcher
    /// l'écran des paramètres de s'ouvrir — c'est justement là qu'on vient quand rien ne
    /// marche.
    /// </summary>
    public static IReadOnlyList<ImprimanteDetectee> Detecter()
    {
        List<string> files;
        try
        {
            files = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        }
        catch (Exception)
        {
            return [];
        }

        return [.. files.Select(nom => new ImprimanteDetectee(nom, RoleDe(nom), MotifDe(nom)))];
    }

    /// <summary>Les files qui tiennent ce rôle, la plus plausible d'abord.</summary>
    public static IReadOnlyList<string> Pour(RoleImprimante role) =>
        [.. Detecter().Where(i => i.Role == role).Select(i => i.Nom)];

    /// <summary>
    /// La file à employer pour un rôle : le réglage du poste s'il est renseigné ET
    /// toujours présent, la détection sinon.
    ///
    /// <b>La vérification de présence n'est pas une politesse</b> : une imprimante
    /// débranchée ou renommée laisserait un réglage qui ne désigne plus rien, et
    /// l'impression échouerait en nommant une machine absente. Mieux vaut retomber sur la
    /// détection, qui verra la nouvelle.
    /// </summary>
    public static string? Choisir(RoleImprimante role, string? reglage)
    {
        var toutes = Detecter();

        if (!string.IsNullOrWhiteSpace(reglage)
            && toutes.Any(i => i.Nom.Equals(reglage, StringComparison.OrdinalIgnoreCase)))
            return reglage;

        return toutes.FirstOrDefault(i => i.Role == role)?.Nom;
    }

    /// <summary>Le rôle d'une file, d'après son nom.</summary>
    public static RoleImprimante RoleDe(string nom)
    {
        if (string.IsNullOrWhiteSpace(nom)) return RoleImprimante.Aucun;

        var minuscule = nom.ToLowerInvariant();

        // les virtuelles d'abord : « Microsoft Print to PDF » et « Send to Sawgrass Print
        // Utility » se feraient prendre pour des machines
        if (Virtuelles.Any(minuscule.Contains)) return RoleImprimante.Aucun;

        if (MotifsMinilab.Any(m => minuscule.Contains(m.Motif))) return RoleImprimante.Minilab;
        if (MotifsSublimation.Any(m => minuscule.Contains(m.Motif))) return RoleImprimante.Sublimation;
        if (MotifsGrandFormat.Any(m => minuscule.Contains(m.Motif))) return RoleImprimante.GrandFormat;

        return RoleImprimante.Aucun;
    }

    /// <summary>Ce qui a fait reconnaître la machine, en clair, ou une chaîne vide.</summary>
    public static string MotifDe(string nom)
    {
        if (string.IsNullOrWhiteSpace(nom)) return "";

        var minuscule = nom.ToLowerInvariant();
        if (Virtuelles.Any(minuscule.Contains)) return "";

        foreach (var (motif, explication) in MotifsMinilab.Concat(MotifsSublimation).Concat(MotifsGrandFormat))
            if (minuscule.Contains(motif))
                return explication;

        return "";
    }
}
