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

    /// <summary>Le prix d'une planche pour ce pays émetteur.</summary>
    public decimal Pour(string? pays) => EstLaFrance(pays) ? FranceEur : EtrangerEur;

    /// <summary>
    /// Le référentiel écrit le pays en toutes lettres (« France », « Espagne »…) : c'est
    /// donc sur ce libellé qu'on tranche, sans distinction de casse ni d'espaces — un
    /// « france » venu d'une saisie ne doit pas faire payer cinq euros de plus.
    /// </summary>
    public static bool EstLaFrance(string? pays) =>
        string.Equals(pays?.Trim(), "France", StringComparison.OrdinalIgnoreCase);
}
