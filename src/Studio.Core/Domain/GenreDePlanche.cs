namespace Studio.Core.Domain;

/// <summary>
/// Ce qu'on vend sous le mot « planche », à l'écran d'identité.
///
/// Les trois sortent du même parcours et du même cadrage : c'est au moment d'engager le
/// papier qu'elles divergent. Le genre voyage donc depuis la tuile de l'écran de choix
/// jusqu'à <c>TirageIdentite</c>, sans que rien entre les deux n'ait à s'en soucier.
///
/// <b>Pourquoi un genre et pas trois produits du catalogue.</b> Le catalogue décrit du
/// PAPIER — une machine, un format, un profil ICC, un DEVMODE. Ces trois-là sortent du même
/// papier sur la même machine ; ce qui change est la façon de le remplir et le prix. Trois
/// produits obligeraient à tenir en phase trois copies du même papier — la leçon des deux
/// planches françaises, qui se doublaient déjà à l'écran (voir <c>IdPhotoView</c>).
/// </summary>
public enum GenreDePlanche
{
    /// <summary>
    /// La planche d'identité : la même photo, répétée autant de fois que le papier en
    /// porte. C'est ce que la boutique tire toute la journée, et le comportement de tout ce
    /// qui a été écrit avant la rentrée 2026.
    /// </summary>
    Standard,

    /// <summary>
    /// La planche de la RENTRÉE : quelques photos d'identité et un portrait en grand, sur
    /// LA MÊME feuille. Voir <c>PlancheRentree</c> pour la géométrie.
    /// </summary>
    Rentree,

    /// <summary>
    /// La planche ordinaire, plus un tirage 10×15 du même visage sur une feuille à part.
    ///
    /// Deux feuilles, une seule commande et un seul prix : le client repart avec sa planche
    /// à découper et une photo à donner. C'est la différence avec <see cref="Rentree"/>, où
    /// tout tient sur une feuille et où le portrait est donc plus petit.
    /// </summary>
    PlancheEtTirage,
}

/// <summary>
/// Ce que la boutique vend sous le nom de « planche de rentrée ».
///
/// La géométrie vit dans <c>Studio.Imaging.Geometry.PlancheRentree</c> — c'est du dessin.
/// Ce qui est ici est du COMMERCE : combien de photos d'identité la planche porte, et sur
/// quel papier part le tirage qui accompagne la planche. Le catalogue et les raccourcis en
/// ont besoin, et ni l'un ni l'autre ne connaît le dessin.
/// </summary>
public static class PlancheDeRentree
{
    /// <summary>
    /// Quatre photos d'identité et un portrait : le format demandé pour la rentrée 2026.
    ///
    /// C'est un DÉFAUT, pas une limite — l'opérateur peut le descendre à trois pour
    /// agrandir le portrait, et la géométrie accepte tout ce qui tient.
    /// </summary>
    public const int IdentitesParDefaut = 4;

    /// <summary>
    /// Le papier du tirage qui accompagne la planche, pour
    /// <see cref="GenreDePlanche.PlancheEtTirage"/> : le 10×15 de la DNP, celui qui sort de
    /// la même machine que la planche — le client repart avec ses deux feuilles en même
    /// temps, sans attendre le minilab.
    ///
    /// Un code de catalogue, cherché à l'impression et non figé : un poste qui a nommé son
    /// 10×15 autrement se rattrape sur les cotes. Voir <c>TirageIdentite</c>.
    /// </summary>
    public const string CodeDuTirage = "10x15-dnp";
}
