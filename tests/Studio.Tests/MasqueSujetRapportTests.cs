using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le garde-fou qui refuse d'étirer un masque sur une image d'un autre cadre.
///
/// <b>Arcueil, 17/08/2026, commande 17-006.</b> Une planche d'identité est sortie avec un
/// chevron clair derrière les épaules et une démarcation nette en travers du front — alors
/// que l'aperçu était parfait. Aucun fond n'était demandé : c'était la correction du sujet
/// (+0,45 d'exposition) posée à côté du sujet.
///
/// La cause : la clé du cache des masques est le FICHIER (chemin, taille, date), pas la
/// géométrie. Le masque rangé pendant le réglage est celui de la photo ENTIÈRE ; l'impression
/// demande celui de la CASE recadrée, et <c>MasqueALaTaille</c> l'étirait avec
/// <c>IgnoreAspectRatio</c> sans jamais vérifier que les deux allaient ensemble.
///
/// Ce qui doit être partagé — le même cadre à deux tailles — reste partagé : c'est là qu'est
/// le gain de 14,5 s par photo mesuré à Créteil le 12/08/2026, et le perdre ramènerait le
/// repli mémoire de la carte graphique.
/// </summary>
public class MasqueSujetRapportTests
{
    /// <summary>
    /// <b>Le cas réel du 17/08.</b> Photo entière redressée 4000×6016 (rapport 0,665) contre
    /// la case d'identité 35×45 recadrée (rapport 0,778) : à refuser.
    /// </summary>
    [Fact]
    public void Le_masque_de_la_photo_entiere_est_refuse_pour_la_case_recadree()
    {
        // la case telle que la planche la rend : 35×45 mm à 300 ppp
        Assert.False(MasqueSujet.RapportsCompatibles(4000, 6016, 413, 531));
    }

    /// <summary>
    /// <b>Et ce qui doit continuer de passer.</b> Le masque calculé sur l'aperçu du cadrage
    /// (grand côté 1600) sert la planche pleine résolution : même cadre, deux tailles, les
    /// rapports ne diffèrent que par les arrondis entiers.
    /// </summary>
    [Theory]
    [InlineData(1064u, 1600u, 4000u, 6016u)] // aperçu de la photo entière -> pleine définition
    [InlineData(4000u, 6016u, 1064u, 1600u)] // et l'inverse
    [InlineData(413u, 531u, 2851u, 3664u)]   // la case, de l'aperçu à l'impression
    public void Le_meme_cadre_a_deux_tailles_reste_partage(uint lm, uint hm, uint li, uint hi)
    {
        Assert.True(MasqueSujet.RapportsCompatibles(lm, hm, li, hi));
    }

    /// <summary>
    /// Un masque à l'identique passe, évidemment — c'est le cas de loin le plus fréquent.
    /// </summary>
    [Fact]
    public void Les_memes_dimensions_passent()
    {
        Assert.True(MasqueSujet.RapportsCompatibles(2851, 3664, 2851, 3664));
    }

    /// <summary>
    /// <b>Le quart de tour est refusé.</b> Une photo prise à la verticale porte une
    /// orientation EXIF — celle du 17/08 valait 8 — et le fichier reste en paysage tant que
    /// personne ne l'a redressé. Un masque portrait étiré sur du paysage est le pire cas :
    /// le sujet se retrouve couché.
    /// </summary>
    [Theory]
    [InlineData(4000u, 6016u, 6016u, 4000u)]
    [InlineData(6016u, 4000u, 4000u, 6016u)]
    public void Un_masque_tourne_d_un_quart_est_refuse(uint lm, uint hm, uint li, uint hi)
    {
        Assert.False(MasqueSujet.RapportsCompatibles(lm, hm, li, hi));
    }

    /// <summary>
    /// Le carré et le portrait ne se confondent pas.
    /// </summary>
    [Fact]
    public void Le_carre_ne_sert_pas_un_portrait()
    {
        Assert.False(MasqueSujet.RapportsCompatibles(1000, 1000, 413, 531));
    }

    /// <summary>
    /// Une dimension nulle ne doit ni passer ni diviser par zéro : on refuse, sans lever.
    /// </summary>
    [Theory]
    [InlineData(0u, 100u, 100u, 100u)]
    [InlineData(100u, 0u, 100u, 100u)]
    [InlineData(100u, 100u, 0u, 100u)]
    [InlineData(100u, 100u, 100u, 0u)]
    public void Une_dimension_nulle_est_refusee_sans_lever(uint lm, uint hm, uint li, uint hi)
    {
        Assert.False(MasqueSujet.RapportsCompatibles(lm, hm, li, hi));
    }

    /// <summary>
    /// La tolérance est bien de deux pour cent : un écart d'un pour cent passe, un écart de
    /// cinq pour cent non. Fixer ce contour empêche qu'un réglage « pour faire passer un
    /// cas » n'ouvre la porte à un recadrage.
    /// </summary>
    [Theory]
    [InlineData(1000u, 1000u, 1010u, 1000u, true)]  // +1 %
    [InlineData(1000u, 1000u, 1050u, 1000u, false)] // +5 %
    public void La_tolerance_tient_a_deux_pour_cent(uint lm, uint hm, uint li, uint hi, bool attendu)
    {
        Assert.Equal(attendu, MasqueSujet.RapportsCompatibles(lm, hm, li, hi));
    }
}
