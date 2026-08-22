using Studio.Core.Domain;

namespace Studio.Core.Catalog;

/// <summary>
/// Une machine dont on règle la couleur d'un seul geste, et les mots pour en parler.
///
/// Le profil ICC est une propriété de PRODUIT dans le catalogue, mais la couleur ne dépend
/// pas du produit : elle dépend de la MACHINE et de son papier. Ce descripteur est ce qui
/// relie les deux — il dit quels produits sortent d'une machine donnée, et comment l'écran
/// la nomme.
/// </summary>
/// <param name="Cle">Nom court, pour le journal.</param>
/// <param name="Sortie">
/// La machine avec son article, pour se glisser dans une phrase : « tout ce qui sort
/// <i>de la DNP</i> ». Écrit ici plutôt que recomposé à l'écran — « de le minilab » est le
/// genre de faute qu'on ne voit qu'en boutique.
/// </param>
/// <param name="Titre">Titre de l'écran de réglage.</param>
/// <param name="Explication">Ce que le profil décide, en une phrase, en haut de l'écran.</param>
/// <param name="AideProfils">Comment reconnaître les bons profils dans la liste.</param>
/// <param name="Rien">Ce qu'on dit quand aucun produit du poste ne sort de cette machine.</param>
/// <param name="SansProfil">
/// Ce qu'on dit quand la machine n'applique AUCUN profil. Propre à chaque machine : sur la
/// DNP c'est un écart visible avec DiLand, sur le DE100 c'est du sRGB présumé.
/// </param>
/// <param name="Reconnait">Vrai si ce produit sort de cette machine.</param>
public sealed record MachineCouleur(
    string Cle,
    string Sortie,
    string Titre,
    string Explication,
    string AideProfils,
    string Rien,
    string SansProfil,
    Func<Product, bool> Reconnait)
{
    /// <summary>La DNP à sublimation — planches d'identité, E-Photo, 10×15.</summary>
    public static readonly MachineCouleur Dnp = new(
        Cle: "DNP",
        Sortie: "de la DNP",
        Titre: "Profil couleur de la DNP",
        Explication:
        "Le profil décide de la couleur qui sort de la DS620 : chair plus ou moins chaude, " +
        "contraste, saturation. Il s'applique à TOUT ce qui sort de cette machine — planches " +
        "d'identité, E-Photo, 10×15 —, parce que la couleur dépend de la machine et de son " +
        "papier, pas du produit.",
        AideProfils:
        "Les profils du pilote DNP et ceux livrés par DiLand se nomment DS620…icc ou " +
        "DS620…icm. « Vivid » sature davantage ; le profil au nom simple est le rendu neutre " +
        "du fabricant. Changer de profil ne demande pas de redémarrer : le tirage suivant en " +
        "tient compte.",
        Rien:
        "Aucun produit de ce poste ne sort sur une imprimante DNP : il n'y a rien à régler ici.",
        SansProfil:
        "Aucun profil : la couleur est laissée au pilote, ce qui donne un rendu plus plat que "  +
        "DiLand sur la même machine.",
        Reconnait: p => p.Output == ProductOutput.Printer && ImprimanteDnp.EstUneDnp(p.PrinterName));

    /// <summary>
    /// Le minilab Fuji DE100 — tous les tirages ordinaires de la boutique.
    ///
    /// ⚠ <b>Un seul profil pour les deux machines, et c'est un COMPROMIS assumé.</b> Sur le
    /// DE100 la finition n'est pas un surlaminage comme sur la DNP : c'est le ROULEAU
    /// chargé, et Fuji livre un profil par papier (« DE100 Glossy », « DE100 Lustre »,
    /// « DE100 Photo Matte »…). La boutique tient la machine A en brillant et la B en
    /// lustré ; le rendu exact demanderait donc un profil par surface.
    ///
    /// Ce n'est pas faisable aujourd'hui : les tirages sont RENDUS avant que la machine ne
    /// soit choisie, et les commandes arrivent sans finition (<c>Finish</c> vaut null sur
    /// toutes celles du poste). Au moment où l'on applique le profil, on ne sait pas encore
    /// sur quel rouleau la photo sortira. Un profil pour la machine reste très au-dessus de
    /// l'état actuel — aucun profil du tout, donc du sRGB présumé sur les 26 produits.
    /// </summary>
    public static readonly MachineCouleur MinilabFuji = new(
        Cle: "minilab DE100",
        Sortie: "du minilab DE100",
        Titre: "Profil couleur du minilab DE100",
        Explication:
        "Le profil décide de la couleur qui sort du DE100 : chair plus ou moins chaude, " +
        "contraste, saturation. Aujourd'hui les tirages partent SANS aucun profil, donc en " +
        "sRGB présumé. Il s'applique à tout ce qui sort du minilab, parce que la couleur " +
        "dépend de la machine et de son papier, pas du produit.",
        AideProfils:
        "Le pilote Fuji installe ses profils dans Windows : ils se nomment « DE100 Glossy », " +
        "« DE100 Lustre », « DE100 Photo Matte »… ⚠ Il y en a un PAR PAPIER, et les deux " +
        "machines ne portent pas le même rouleau (A en brillant, B en lustré) : le profil " +
        "choisi ici s'applique aux deux. Prenez celui du rouleau que vous utilisez le plus. " +
        "Changer de profil ne demande pas de redémarrer.",
        Rien:
        "Aucun produit de ce poste ne sort sur le minilab Fuji : il n'y a rien à régler ici.",
        SansProfil:
        "Aucun profil : les tirages partent en sRGB présumé, sans aucune gestion des couleurs.",
        Reconnait: p => p.Output == ProductOutput.FujiMinilab);
}

/// <summary>
/// Le profil ICC appliqué à TOUT ce qui sort d'une machine, lu et posé d'un seul geste.
///
/// <b>Pourquoi ce réglage existe.</b> Le profil couleur est une propriété de PRODUIT, et il
/// se choisit donc produit par produit dans le Catalogue — écran que Studio Photo Identité
/// n'a pas, et que personne n'ouvre pour vingt-six lignes qui doivent de toute façon dire
/// la même chose. Or la couleur ne dépend pas du produit : elle dépend de la MACHINE et de
/// son papier. Trois produits sortent de la DS620 dans ces boutiques — la planche
/// d'identité, l'E-Photo et le 10×15 — et seule la planche portait un profil : les deux
/// autres partaient en sRGB présumé, sur la même machine et le même rouleau. Signalé le
/// 18/08/2026, en comparant une planche sortie de Studio et la même sortie de DiLand.
///
/// Le DE100 était dans le même cas, en pire : ses vingt-six produits n'avaient AUCUN profil.
/// Voir <see cref="MachineCouleur.MinilabFuji"/> pour ce que ce réglage peut, et ne peut
/// pas, faire de son côté.
///
/// <b>Les finitions sont remises à zéro.</b> Le profil d'une finition l'emporte sur celui du
/// produit (voir <c>PrintOrchestrator.IccPath</c>) : la laisser en place donnerait un
/// réglage qui paraît pris et ne sort pas. Sur une DNP la finition n'est que le
/// surlaminage — brillant ou mat —, elle ne change pas la colorimétrie : un seul profil
/// pour la machine est le bon modèle.
/// </summary>
public static class ProfilCouleurMachine
{
    /// <summary>Les produits du catalogue qui sortent sur <paramref name="machine"/>.</summary>
    public static IReadOnlyList<Product> Produits(IEnumerable<Product> tous, MachineCouleur machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        return tous.Where(machine.Reconnait).ToList();
    }

    /// <summary>
    /// Ce que la machine applique aujourd'hui.
    /// </summary>
    /// <param name="Profil">
    /// Le fichier ICC partagé, ou null quand il n'y en a pas — ou que les produits ne
    /// s'accordent pas.
    /// </param>
    /// <param name="Accord">
    /// Vrai quand tous les produits de la machine portent la même chose. Faux, l'écran doit
    /// le DIRE : c'est exactement le cas où la machine sort deux couleurs et où personne ne
    /// comprend pourquoi.
    /// </param>
    public readonly record struct Reglage(string? Profil, bool Accord);

    /// <summary>
    /// Le profil réellement appliqué à chaque produit — celui de sa finition s'il en a une,
    /// le sien sinon — puis le verdict : tout le monde d'accord, ou non.
    /// </summary>
    public static Reglage Lire(IEnumerable<Product> produits)
    {
        var poses = produits.SelectMany(Effectifs).ToList();
        if (poses.Count == 0) return new Reglage(null, Accord: true);

        var premier = poses[0];
        var accord = poses.All(p => string.Equals(p, premier, StringComparison.OrdinalIgnoreCase));

        return new Reglage(accord ? premier : null, accord);
    }

    /// <summary>
    /// Pose <paramref name="profil"/> (null = aucun, le pilote fait la couleur) sur tous les
    /// produits, et efface les profils de finition qui l'auraient masqué.
    /// </summary>
    /// <returns>Les produits réellement modifiés — ce que l'écran annonce à l'opérateur.</returns>
    public static IReadOnlyList<Product> Appliquer(IEnumerable<Product> produits, string? profil)
    {
        var voulu = string.IsNullOrWhiteSpace(profil) ? null : profil;
        var changes = new List<Product>();

        foreach (var produit in produits)
        {
            var avant = Effectifs(produit).ToList();

            produit.IccProfile = voulu;
            foreach (var finition in produit.Finishes ?? [])
                finition.IccProfile = null;

            if (avant.Count != 1 || !string.Equals(avant[0], voulu, StringComparison.OrdinalIgnoreCase))
                changes.Add(produit);
        }

        return changes;
    }

    /// <summary>
    /// Les profils que ce produit applique VRAIMENT. Une finition qui en porte un couvre
    /// celui du produit ; un produit à plusieurs finitions peut donc en appliquer plusieurs.
    /// </summary>
    private static IEnumerable<string?> Effectifs(Product produit)
    {
        var finitions = (produit.Finishes ?? []).ToList();
        if (finitions.Count == 0) return [Vide(produit.IccProfile)];

        return finitions.Select(f => Vide(f.IccProfile) ?? Vide(produit.IccProfile));
    }

    private static string? Vide(string? nom) => string.IsNullOrWhiteSpace(nom) ? null : nom;
}
