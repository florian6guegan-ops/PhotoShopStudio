namespace Studio.Core.Domain;

/// <summary>
/// Les finitions de papier, et le SEUL endroit où leurs noms sont écrits.
///
/// <b>Pourquoi une classe pour trois mots.</b> La finition voyage en texte d'un bout à
/// l'autre de la chaîne — la borne l'écrit dans DiLand, l'importateur la pose sur
/// <c>OrderItem.Finish</c>, le tirage la retraduit en surface pour le minilab ou en
/// surlaminage pour la DNP. Trois écritures indépendantes de « Lustré » finiraient par
/// diverger sur un accent, et une commande de client sortirait sur le mauvais rouleau
/// sans que rien ne le dise. Les comparaisons restent insensibles à la casse et aux
/// variantes (voir <c>PrintOrchestrator.FinitionMinilab</c>) : ce sont des noms, pas des
/// codes, et l'opérateur peut en saisir d'autres dans le catalogue.
///
/// <b>Studio.Core et non Studio.Printing</b> : <c>Studio.Store</c> lit DiLand mais ne
/// référence pas <c>Studio.Printing</c>, où vit <c>De100Surface</c>. Le nom est le seul
/// vocabulaire que les deux côtés partagent.
/// </summary>
public static class FinitionPapier
{
    public const string Brillant = "Brillant";
    public const string Lustre = "Lustré";
    public const string Mat = "Mat";

    /// <summary>
    /// La finition qu'un client a choisie à la borne, d'après <c>OrderLine.PaperType</c>
    /// de DiLand. <c>null</c> = code inconnu, et donc aucune exigence : mieux vaut tirer
    /// sur le rouleau chargé que refuser une commande sur un code qu'on ne sait pas lire.
    ///
    /// <b>Les codes sont ceux de Fuji</b>, repris tels quels par DiLand : ce sont les
    /// mêmes valeurs que <c>De100Surface</c> (Glossy = 1, Matte = 2, Luster = 3). Relevé
    /// le 11/08/2026 sur la base de la boutique, sur 62 lignes de bornes réparties du
    /// 01/08 au 10/08 : les 24 lignes en <c>PaperType = 1</c> sont parties sur la machine
    /// A, les 38 en <c>PaperType = 3</c> sur la machine B, sans un seul croisement — et
    /// A porte le brillant, B le lustré. Corroboré côté produits DiLand, où le 21×29,7
    /// annonce <c>ProductPaperTypes = "Luster;Glossy"</c> et le produit DNP « Glossy »
    /// seul, avec toutes ses lignes en <c>PaperType = 1</c>.
    /// </summary>
    public static string? DepuisDiLand(int paperType) => paperType switch
    {
        1 => Brillant,
        2 => Mat,
        3 => Lustre,
        _ => null,
    };

    /// <summary>
    /// La même finition, mais lue dans le <c>Order.xml_p</c> déposé par la borne, où
    /// DiLand l'écrit en toutes lettres — <c>PaperType="Glossy"</c> — et non par son code.
    ///
    /// Les deux lectures existent parce que Studio a deux chemins vers une commande de
    /// borne : la base, et le disque quand DiLand est fermé (voir
    /// <c>DiLandOrderXml</c>). Ils doivent rendre la MÊME finition, sans quoi une commande
    /// changerait de rouleau selon que DiLand tourne ou non.
    ///
    /// <c>Undefined</c>, vide, ou un mot qu'on ne connaît pas : aucune exigence.
    /// </summary>
    public static string? DepuisDiLand(string? paperType) => paperType?.Trim().ToLowerInvariant() switch
    {
        "glossy" => Brillant,
        "matte" or "matt" => Mat,
        "luster" or "lustre" => Lustre,
        _ => null,
    };
}
