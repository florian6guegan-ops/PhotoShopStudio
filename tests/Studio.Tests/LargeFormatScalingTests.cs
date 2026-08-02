using Studio.Printing.LargeFormat;

namespace Studio.Tests;

/// <summary>
/// La résolution d'envoi des agrandissements.
///
/// Elle existe parce que <c>Graphics.DrawImage</c> sur un contexte d'imprimante, avec une
/// interpolation de qualité demandée, rééchantillonne à la définition du PÉRIPHÉRIQUE :
/// 1440 ppp sur la SC-P800, soit plus d'un milliard de pixels pour un 50×70. On réduit donc
/// nous-mêmes, en amont, comme le fait Photoshop — mais jamais au-delà de ce que la source
/// contient.
/// </summary>
public class LargeFormatScalingTests
{
    // le cas courant de l'atelier : un rendu 50×70 à 300 ppp
    private const int W = 5906;
    private const int H = 8268;

    /// <summary>Définition annoncée par le pilote Epson SC-P800.</summary>
    private const int SC_P800 = 1440;

    [Fact]
    public void A_trois_cents_ppp_rien_n_est_reechantillonne()
    {
        var (largeur, hauteur, dpi) = LargeFormatPrinter.TailleDEnvoi(W, H, 300, SC_P800);

        Assert.Equal(W, largeur);
        Assert.Equal(H, hauteur);
        Assert.Equal(300, dpi, 3);
    }

    [Fact]
    public void Une_source_plus_fine_que_le_plafond_est_ramenee_a_trois_cent_soixante()
    {
        // même image tirée à 40 % : 750 ppp effectifs
        var (largeur, hauteur, dpi) = LargeFormatPrinter.TailleDEnvoi(W, H, 750, SC_P800);

        Assert.Equal(360, dpi, 0);
        Assert.Equal((int)Math.Round(W * 360.0 / 750), largeur);
        Assert.Equal((int)Math.Round(H * 360.0 / 750), hauteur);
    }

    [Fact]
    public void On_n_agrandit_jamais_pour_atteindre_le_plafond()
    {
        // image pauvre tirée en grand : 120 ppp effectifs. Fabriquer les pixels manquants
        // coûterait le même prix qu'avant et n'apporterait rien — c'est le travail du pilote.
        var (largeur, hauteur, dpi) = LargeFormatPrinter.TailleDEnvoi(W, H, 120, SC_P800);

        Assert.Equal(W, largeur);
        Assert.Equal(H, hauteur);
        Assert.Equal(120, dpi, 3);
    }

    [Fact]
    public void Le_plafond_suit_le_pilote_quand_il_est_plus_bas()
    {
        // une file à 300 ppp ne consomme pas plus : inutile de lui envoyer du 360
        var (_, _, dpi) = LargeFormatPrinter.TailleDEnvoi(W, H, 750, deviceDpi: 300);

        Assert.Equal(300, dpi, 0);
    }

    [Fact]
    public void Une_definition_inconnue_retombe_sur_le_plafond()
    {
        // PrinterResolution.X négatif (le pilote répond « Élevée » au lieu d'un nombre)
        var (_, _, dpi) = LargeFormatPrinter.TailleDEnvoi(W, H, 750, deviceDpi: 0);

        Assert.Equal(360, dpi, 0);
    }

    /// <summary>
    /// Le placement en millimètres ne doit pas bouger d'un cheveu : c'est la contrepartie
    /// de la réduction. Réduire les pixels d'un facteur k revient à diviser la résolution
    /// annoncée par k — et la résolution rendue suit l'arrondi de la largeur, pas le facteur
    /// théorique, sans quoi le tirage se décalerait de la fraction de pixel perdue.
    /// </summary>
    [Fact]
    public void La_taille_physique_du_tirage_est_conservee()
    {
        const double effectif = 750;
        var avant = W / effectif * 25.4;

        var (largeur, _, dpi) = LargeFormatPrinter.TailleDEnvoi(W, H, effectif, SC_P800);
        var apres = largeur / dpi * 25.4;

        Assert.Equal(avant, apres, 6);
    }

    [Fact]
    public void Un_ecart_negligeable_ne_declenche_pas_de_reechantillonnage()
    {
        // 363 ppp pour un plafond à 360 : rééchantillonner 48 Mpx pour un pour cent d'écart
        // coûterait plus cher que le gain
        var (largeur, hauteur, _) = LargeFormatPrinter.TailleDEnvoi(W, H, 363, SC_P800);

        Assert.Equal(W, largeur);
        Assert.Equal(H, hauteur);
    }

    [Fact]
    public void Un_placement_degenere_ne_touche_a_rien()
    {
        var (largeur, hauteur, dpi) = LargeFormatPrinter.TailleDEnvoi(W, H, 0, SC_P800);

        Assert.Equal(W, largeur);
        Assert.Equal(H, hauteur);
        Assert.Equal(0, dpi);
    }
}
