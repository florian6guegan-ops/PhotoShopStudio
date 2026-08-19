using Studio.Core.Domain;
using Studio.Printing.Devices.Dnp;

namespace Studio.Printing;

/// <summary>Un format vendu en boutique, et ce qu'il reste à tirer sur le papier chargé.</summary>
/// <param name="Nom">Le nom du produit, tel que l'opérateur le dit au client.</param>
/// <param name="Restants">Tirages encore possibles avec ce qui reste de papier.</param>
public sealed record TiragesRestants(string Nom, int Restants);

/// <summary>
/// Ce qu'il reste à tirer, <b>compté dans les formats de la boutique</b> et non dans ceux du
/// rouleau.
///
/// Demandé le 19/08/2026 : « dans le nombre de tirages restants, il faudrait qu'il affiche les
/// formats du catalogue, pas ceux génériques du rouleau ». L'écran listait ce que le pilote de
/// DiLand connaît — <c>15xS</c>, <c>15xL</c>, <c>15x23</c>, <c>15x40</c> —, une nomenclature de
/// canal que personne ne vend et où le 13×18 ne paraît même pas sur un rouleau de 152.
///
/// L'orchestrateur d'impression tenait déjà ce raisonnement pour expliquer un refus (« on
/// annonce les produits du catalogue qui sortiraient VRAIMENT de ce rouleau… c'est en produits
/// que l'opérateur parle au client ») : il vaut aussi pour compter ce qui reste.
///
/// <b>Un rang par COTES, pas par produit.</b> « 10×15 » et « Bord blanc 10×15 » consomment le
/// même papier et donneraient deux lignes identiques ; le catalogue en compte vingt-six sur le
/// minilab, ce qui remplirait l'écran de répétitions. On garde donc le premier produit de
/// chaque taille, dans l'ordre du catalogue.
/// </summary>
public static class FormatsDuCatalogue
{
    /// <summary>
    /// Tirages restants pour chaque format vendu, sur le rouleau chargé dans cette machine.
    /// </summary>
    /// <param name="catalogue">Les produits ACTIFS du catalogue.</param>
    /// <param name="machineId">La machine regardée : un produit qui en vise une autre est écarté.</param>
    /// <param name="largeurRouleauMm">Largeur du rouleau chargé.</param>
    /// <param name="longueurRestanteMm">Longueur de papier restante, en millimètres.</param>
    public static IReadOnlyList<TiragesRestants> SurLeMinilab(
        IEnumerable<Product> catalogue,
        char machineId,
        int largeurRouleauMm,
        double longueurRestanteMm)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        if (largeurRouleauMm <= 0) return [];
        if (longueurRestanteMm < 0) longueurRestanteMm = 0;

        return catalogue
            .Where(p => p.Output == ProductOutput.FujiMinilab)
            .Where(p => PourCetteMachine(p, machineId))
            .Where(p => Tient(p, largeurRouleauMm))
            .GroupBy(Cotes)
            .Select(g => new
            {
                Nom = Representant(g),
                Consomme = LongueurConsommeeMm(g.Key.Petit, g.Key.Grand, largeurRouleauMm),
            })
            .Select(x => new TiragesRestants(
                x.Nom,
                x.Consomme <= 0 ? 0 : (int)(longueurRestanteMm / x.Consomme)))
            .OrderByDescending(f => f.Restants)
            .ThenBy(f => f.Nom, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// Tirages restants pour chaque format vendu sur cette DNP.
    ///
    /// <b>Le compteur de la machine parle en FEUILLES, la boutique en tirages.</b> Sur un
    /// rouleau 15×20, un 10×15 est coupé en deux : « 138 restants » annoncé par la DS620, ce
    /// sont 276 photos d'identité. C'est la même règle que celle qui décide de la découpe à
    /// l'impression — <see cref="DnpDriver.TailleDeTirage"/> — et il fallait qu'elle serve
    /// aussi à compter, sans quoi l'écran sous-estime de moitié ce qui reste.
    /// </summary>
    /// <param name="catalogue">Les produits ACTIFS du catalogue.</param>
    /// <param name="nomImprimante">
    /// La file Windows de cette DNP, telle que les produits la nomment — ou <c>null</c>.
    ///
    /// ⚠ <b>Elle est le plus souvent inconnue, et c'est normal</b> : <c>WindowsQueueName</c>
    /// n'est renseigné QUE quand la machine est vue par le spouleur seul, donc jamais quand
    /// le SDK répond, c'est-à-dire dans le cas courant. Sans nom, on ne filtre pas dessus :
    /// tout produit qui sort par une file d'impression et qui tient sur ce rouleau compte.
    /// Les postes de la boutique n'ont qu'une DNP chacun.
    /// </param>
    /// <param name="rouleau">Le format chargé.</param>
    /// <param name="feuillesRestantes">Ce que la machine annonce : des feuilles entières.</param>
    public static IReadOnlyList<TiragesRestants> SurLaDnp(
        IEnumerable<Product> catalogue,
        string? nomImprimante,
        DnpMediaSize rouleau,
        int feuillesRestantes)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        if (feuillesRestantes < 0) feuillesRestantes = 0;
        if (DnpDriver.CotesEnPouces(rouleau) is not { } cotesRouleau) return [];

        return catalogue
            .Where(p => p.Output == ProductOutput.Printer)
            .Where(p => string.IsNullOrWhiteSpace(nomImprimante)
                        || p.PrinterName.Equals(nomImprimante, StringComparison.OrdinalIgnoreCase))
            .Where(p => TientSurLeRouleau(p, cotesRouleau))
            .GroupBy(Cotes)
            .Select(g => new TiragesRestants(
                Representant(g),
                feuillesRestantes * ParFeuille(g.Key, rouleau)))
            .OrderByDescending(f => f.Restants)
            .ThenBy(f => f.Nom, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// Longueur de rouleau qu'un tirage consomme réellement.
    ///
    /// La photo se pose en travers du rouleau et l'on consomme l'autre côté — le plus petit
    /// des deux dès que le rouleau est assez large pour le grand. Un 10×15 sur du 203 mm ne
    /// mange que 102 mm, et c'est bien ce que fait la machine (relevé d'Arcueil, 13/08/2026 :
    /// « 203×102 mm » pour un 10×15). Compter le grand côté sous-estimerait d'un tiers.
    /// </summary>
    public static int LongueurConsommeeMm(double petitCoteMm, double grandCoteMm, int largeurRouleauMm)
    {
        var petit = Math.Min(petitCoteMm, grandCoteMm);
        var grand = Math.Max(petitCoteMm, grandCoteMm);

        return (int)Math.Round(grand <= largeurRouleauMm + Tolerance ? petit : grand);
    }

    /// <summary>
    /// Combien de tirages de ce format dans UNE feuille du rouleau.
    ///
    /// Deux quand la machine coupe, un sinon. On ne devine pas : on demande à la règle qui
    /// décide de la découpe pour de vrai.
    /// </summary>
    private static int ParFeuille((double Petit, double Grand) cotes, DnpMediaSize rouleau) =>
        DnpDriver.TailleDeTirage(rouleau, cotes.Grand / MmParPouce, cotes.Petit / MmParPouce) == rouleau
            ? 1
            : 2;

    /// <summary>
    /// Le produit tient-il sur ce rouleau DNP ? La DNP ne fait pas de bandes blanches : elle
    /// attend une trame aux cotes du format, donc les deux côtés doivent tenir.
    ///
    /// <b>⚠ « Tenir » se juge au FOND PERDU près.</b> La planche d'identité de la boutique fait
    /// 156,1 × 105 mm là où un 6×4 en fait 152,4 × 101,6 : elle déborde de trois à quatre
    /// millimètres, exprès, pour qu'aucun liseré blanc ne subsiste après la coupe. Une
    /// tolérance au millimètre l'aurait déclarée intirable sur le rouleau où elle sort tous les
    /// jours.
    /// </summary>
    private static bool TientSurLeRouleau(Product produit, (double Rouleau, double Longueur) cotes)
    {
        var (petit, grand) = Cotes(produit);
        var large = Math.Max(cotes.Rouleau, cotes.Longueur) * MmParPouce;
        var etroit = Math.Min(cotes.Rouleau, cotes.Longueur) * MmParPouce;

        return petit <= etroit + ToleranceFondPerduMm && grand <= large + ToleranceFondPerduMm;
    }

    /// <summary>
    /// Règle du minilab : le tirage sort si le côté qui se pose en travers du rouleau y tient.
    /// Ce n'est PAS une égalité — un 13×18 sort sur du 152, avec des bandes blanches.
    /// </summary>
    private static bool Tient(Product produit, int largeurRouleauMm) =>
        Math.Min(produit.WidthMm, produit.HeightMm) <= largeurRouleauMm + Tolerance;

    /// <summary>
    /// Un produit sans machine désignée sort de n'importe laquelle : il compte donc pour
    /// toutes. Seul un produit épinglé sur une AUTRE machine est écarté.
    /// </summary>
    private static bool PourCetteMachine(Product produit, char machineId) =>
        string.IsNullOrWhiteSpace(produit.MinilabMachineId)
        || produit.MinilabMachineId.Trim().Equals(
            machineId.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lequel des produits de cette taille donne son nom à la ligne.
    ///
    /// Le format NU d'abord : « Bord blanc 21×29,7 » et « 21×29,7 » consomment le même papier,
    /// mais c'est le second qui nomme la taille — le premier nomme une variante. Sur le
    /// catalogue de la boutique, l'ordre du fichier mettait le bord blanc devant et l'écran
    /// annonçait donc une largeur de marge là où on attendait un format.
    /// </summary>
    private static string Representant(IEnumerable<Product> memeTaille) =>
        memeTaille.OrderBy(p => p.ABordBlanc ? 1 : 0).First().Name;

    /// <summary>Les cotes rangées, pour que deux produits de même taille se regroupent.</summary>
    private static (double Petit, double Grand) Cotes(Product produit) =>
        (Math.Round(Math.Min(produit.WidthMm, produit.HeightMm), 1),
         Math.Round(Math.Max(produit.WidthMm, produit.HeightMm), 1));

    private const double MmParPouce = 25.4;

    /// <summary>Les cotes du catalogue sont au dixième de millimètre ; le papier ne l'est pas.</summary>
    private const double Tolerance = 1.0;

    /// <summary>
    /// De combien un tirage sans marge a le droit de déborder de son papier — voir
    /// <see cref="TientSurLeRouleau"/>. La planche d'identité déborde de 3,7 mm.
    /// </summary>
    private const double ToleranceFondPerduMm = 5.0;
}
