namespace Studio.Core.Domain;

/// <summary>
/// Le prix d'une planche de photos d'identité, qui dépend du DOCUMENT et non du papier.
///
/// <b>Pourquoi ce n'est pas un prix de produit.</b> Une planche d'identité sort du même
/// papier, sur la même machine, quel que soit le pays : c'est le même produit du catalogue.
/// Ce qui change est le TRAVAIL — une norme étrangère demande de retrouver le gabarit du
/// pays, de vérifier la hauteur de visage sur des bornes qu'on ne connaît pas par cœur, et
/// de recommencer si le guichet refuse. La boutique la facture donc 15 € contre 10 €.
///
/// Mettre deux produits au catalogue pour cela reviendrait à dupliquer papier, machine,
/// profil ICC et gabarit pour une seule différence de prix — et à devoir les tenir en phase.
/// </summary>
public sealed class TarifsIdentite
{
    /// <summary>Planche d'un document français.</summary>
    public decimal FranceEur { get; set; } = 10m;

    /// <summary>Planche d'un document étranger.</summary>
    public decimal EtrangerEur { get; set; } = 15m;

    /// <summary>
    /// La planche de la RENTRÉE : quatre photos d'identité et un portrait, sur la même
    /// feuille. Onze euros, fixés par l'exploitant le 20/08/2026.
    ///
    /// <b>Un prix unique, quel que soit le pays.</b> C'est un produit de saison qu'on vend
    /// à des familles, pas une démarche administrative : la majoration « document
    /// étranger » n'a pas d'objet ici — elle paie la recherche d'une norme exotique, et la
    /// photo de rentrée n'en a pas.
    /// </summary>
    public decimal RentreeEur { get; set; } = 11m;

    /// <summary>
    /// La planche ordinaire ET un tirage 10×15 du même visage : douze euros, même raison
    /// et même date que <see cref="RentreeEur"/>.
    /// </summary>
    public decimal PlancheEtTirageEur { get; set; } = 12m;

    /// <summary>Le prix d'une planche pour ce pays émetteur.</summary>
    public decimal Pour(string? pays) => EstLaFrance(pays) ? FranceEur : EtrangerEur;

    /// <summary>
    /// Le prix de CE genre de planche, pour ce pays.
    ///
    /// Les deux formats de la rentrée ont leur propre prix, le même pour tous les pays ;
    /// la planche ordinaire garde le sien, qui dépend du document. Voir
    /// <see cref="GenreDePlanche"/>.
    /// </summary>
    public decimal Pour(string? pays, GenreDePlanche genre) => genre switch
    {
        GenreDePlanche.Rentree => RentreeEur,
        GenreDePlanche.PlancheEtTirage => PlancheEtTirageEur,
        _ => Pour(pays),
    };

    /// <summary>
    /// Le référentiel écrit le pays en toutes lettres (« France », « Espagne »…) : c'est
    /// donc sur ce libellé qu'on tranche, sans distinction de casse ni d'espaces — un
    /// « france » venu d'une saisie ne doit pas faire payer cinq euros de plus.
    /// </summary>
    public static bool EstLaFrance(string? pays) =>
        string.Equals(pays?.Trim(), "France", StringComparison.OrdinalIgnoreCase);
}
