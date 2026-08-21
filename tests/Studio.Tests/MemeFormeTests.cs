using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Deux produits cadrent-ils dans la MÊME FORME — au liseré et à la taille près ?
///
/// <b>Ce qu'on protège.</b> C'est cette règle qui décide si le recadrage de l'opérateur
/// survit à un changement de format. Il ne survivait pas : le cadre était refait à chaque
/// changement de produit, et passer un 20×25 en « bord blanc 20×25 » jetait le recadrage
/// posé à la main sur chaque photo — pour un liseré de cinq millimètres sur le même papier.
/// Signalé au comptoir le 21/08/2026.
///
/// Se tromper dans l'autre sens serait pire : garder un cadrage entre deux formes
/// vraiment différentes sort un tirage mal cadré, sans que rien ne le dise. Les deux sens
/// sont donc éprouvés.
/// </summary>
public class MemeFormeTests
{
    private static Product Papier(string code, double largeur, double hauteur, double liseré = 0) =>
        new() { Code = code, Name = code, WidthMm = largeur, HeightMm = hauteur, BorderMm = liseré };

    // — ce qu'il FAUT garder —

    /// <summary>
    /// Le cas signalé : même papier, un liseré en plus. La fenêtre passe de 203 × 256 à
    /// 193 × 246, soit 1,06 % de rapport.
    /// </summary>
    [Fact]
    public void Un_bord_blanc_garde_la_forme_de_son_format()
    {
        Assert.True(Product.MemeForme(
            Papier("20x25", 203, 256),
            Papier("bord-blanc-20x25", 203, 256, liseré: 5)));
    }

    /// <summary>
    /// Le pire écart du catalogue parmi ceux qu'il faut garder : 3,45 % sur le 10×15, le
    /// format le plus petit — donc celui où cinq millimètres pèsent le plus lourd.
    /// </summary>
    [Fact]
    public void Le_plus_petit_format_a_liseré_garde_encore_sa_forme()
    {
        Assert.True(Product.MemeForme(
            Papier("10x15", 102, 152),
            Papier("bord-blanc-10x15", 102, 152, liseré: 5)));
    }

    /// <summary>
    /// Un agrandissement proportionnel est le même format à une autre taille : un 10×15 et
    /// un 20×30 sont tous deux en deux tiers, et le cadrage de l'un vaut pour l'autre.
    /// </summary>
    [Fact]
    public void Un_agrandissement_proportionnel_garde_la_forme()
    {
        Assert.True(Product.MemeForme(Papier("10x15", 102, 152), Papier("20x30", 204, 304)));
    }

    [Fact]
    public void Un_carre_reste_un_carre_a_toutes_les_tailles()
    {
        Assert.True(Product.MemeForme(Papier("10x10", 102, 102), Papier("50x50", 508, 508)));
    }

    // — ce qu'il faut REFAIRE —

    /// <summary>
    /// La première forme vraiment différente du catalogue : 6,28 % d'écart. Elle doit
    /// tomber du bon côté du seuil, sans quoi le tirage sortirait au cadrage d'un autre
    /// format.
    /// </summary>
    [Fact]
    public void Deux_formats_differents_ne_partagent_pas_leur_cadrage()
    {
        Assert.False(Product.MemeForme(Papier("13x18", 127, 178), Papier("20x30", 204, 304)));
    }

    [Fact]
    public void Un_carre_et_un_rectangle_n_ont_rien_en_commun()
    {
        Assert.False(Product.MemeForme(Papier("10x10", 102, 102), Papier("10x15", 102, 152)));
    }

    /// <summary>
    /// Le portrait et le paysage du même papier sont deux formes : le cadrage de l'un
    /// couché sur l'autre perdrait tout le sujet.
    /// </summary>
    [Fact]
    public void Le_portrait_et_le_paysage_sont_deux_formes()
    {
        Assert.False(Product.MemeForme(Papier("10x15", 102, 152), Papier("15x10", 152, 102)));
    }

    // — les cas limites —

    [Fact]
    public void Un_produit_absent_ne_ressemble_a_rien()
    {
        Assert.False(Product.MemeForme(null, Papier("10x15", 102, 152)));
        Assert.False(Product.MemeForme(Papier("10x15", 102, 152), null));
    }

    /// <summary>Deux fois rien, c'est deux fois la même chose : il n'y a pas de cadre à refaire.</summary>
    [Fact]
    public void Deux_absences_se_ressemblent()
    {
        Assert.True(Product.MemeForme(null, null));
    }

    /// <summary>
    /// Un produit sans cotes ne dit rien de sa forme. On repart alors d'un cadre neuf,
    /// plutôt que de garder un cadrage sur la foi d'une division par zéro.
    /// </summary>
    [Fact]
    public void Un_produit_sans_cotes_ne_dit_rien_de_sa_forme()
    {
        Assert.False(Product.MemeForme(Papier("vide", 0, 0), Papier("10x15", 102, 152)));
    }

    /// <summary>Un produit se ressemble à lui-même, liseré compris.</summary>
    [Fact]
    public void Un_produit_se_ressemble()
    {
        var papier = Papier("bord-blanc-10x15", 102, 152, liseré: 5);
        Assert.True(Product.MemeForme(papier, papier));
    }
}
