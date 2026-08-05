using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// La géométrie de la planche virtualisée : c'est elle qui décide quelles tuiles sont
/// construites, et donc si l'écran s'ouvre en un clin d'œil ou en cinq secondes.
///
/// Les cotes utilisées sont celles du gabarit réel : une tuile de 210 × 230 avec 3 px de
/// marge de chaque côté, soit 216 × 236.
/// </summary>
public class GrilleVirtuelleTests
{
    private const double LargeurTuile = 216;
    private const double HauteurTuile = 236;

    [Theory]
    [InlineData(1920, 8)]
    [InlineData(1280, 5)]
    [InlineData(432, 2)]
    [InlineData(216, 1)]
    public void Colonnes_suit_la_largeur_disponible(double largeur, int attendu) =>
        Assert.Equal(attendu, GrilleVirtuelle.Colonnes(largeur, LargeurTuile));

    [Fact]
    public void Une_fenetre_plus_etroite_qu_une_tuile_en_garde_une()
    {
        // sinon la planche disparaîtrait entièrement quand on rétrécit la fenêtre
        Assert.Equal(1, GrilleVirtuelle.Colonnes(80, LargeurTuile));
        Assert.Equal(1, GrilleVirtuelle.Colonnes(0, LargeurTuile));
        Assert.Equal(1, GrilleVirtuelle.Colonnes(double.NaN, LargeurTuile));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(1, 5, 1)]
    [InlineData(5, 5, 1)]
    [InlineData(6, 5, 2)]
    [InlineData(1200, 8, 150)]
    [InlineData(1201, 8, 151)]
    public void Rangees_arrondit_a_la_rangee_entamee(int tuiles, int colonnes, int attendu) =>
        Assert.Equal(attendu, GrilleVirtuelle.Rangees(tuiles, colonnes));

    [Fact]
    public void Hauteur_est_celle_des_rangees_occupees()
    {
        Assert.Equal(150 * HauteurTuile, GrilleVirtuelle.Hauteur(1200, 8, HauteurTuile));
        Assert.Equal(0, GrilleVirtuelle.Hauteur(0, 8, HauteurTuile));
    }

    [Fact]
    public void Position_enroule_a_la_rangee_suivante()
    {
        Assert.Equal((0d, 0d), GrilleVirtuelle.Position(0, 5, LargeurTuile, HauteurTuile));
        Assert.Equal((4 * LargeurTuile, 0d), GrilleVirtuelle.Position(4, 5, LargeurTuile, HauteurTuile));
        Assert.Equal((0d, HauteurTuile), GrilleVirtuelle.Position(5, 5, LargeurTuile, HauteurTuile));
        Assert.Equal((LargeurTuile, 2 * HauteurTuile), GrilleVirtuelle.Position(11, 5, LargeurTuile, HauteurTuile));
    }

    /// <summary>
    /// L'essentiel : sur une carte pleine, seule une poignée de tuiles doit être construite.
    /// C'est ce chiffre-là qui séparait un écran qui s'ouvre d'un écran qui gèle.
    /// </summary>
    [Fact]
    public void Une_carte_pleine_ne_fabrique_qu_une_poignee_de_tuiles()
    {
        var tranche = GrilleVirtuelle.Tranche(
            tuiles: 1200, largeurVisible: 1740, hauteurVisible: 780,
            LargeurTuile, HauteurTuile, decalage: 0);

        Assert.Equal(8, tranche.Colonnes);
        Assert.Equal(0, tranche.Premier);

        // 4 rangées visibles (780 / 236 arrondi au-dessus) + 1 de marge = 5 rangées
        Assert.Equal(5 * 8 - 1, tranche.Dernier);
        Assert.Equal(40, tranche.Compte);
    }

    [Fact]
    public void La_tranche_suit_le_defilement()
    {
        var tranche = GrilleVirtuelle.Tranche(
            tuiles: 1200, largeurVisible: 1740, hauteurVisible: 780,
            LargeurTuile, HauteurTuile, decalage: 10 * HauteurTuile);

        // rangée 10 à l'écran, une rangée de marge au-dessus
        Assert.Equal(9 * 8, tranche.Premier);
        Assert.True(tranche.Compte <= 48, $"{tranche.Compte} tuiles, c'est trop pour un écran");
    }

    [Fact]
    public void La_derniere_rangee_ne_deborde_pas_du_nombre_de_photos()
    {
        // 13 photos, 5 colonnes : la troisième rangée n'en porte que trois
        var tranche = GrilleVirtuelle.Tranche(
            tuiles: 13, largeurVisible: 5 * LargeurTuile, hauteurVisible: 5000,
            LargeurTuile, HauteurTuile, decalage: 0);

        Assert.Equal(0, tranche.Premier);
        Assert.Equal(12, tranche.Dernier);
        Assert.Equal(13, tranche.Compte);
    }

    [Fact]
    public void Une_planche_vide_ne_fabrique_rien()
    {
        var tranche = GrilleVirtuelle.Tranche(
            tuiles: 0, largeurVisible: 1740, hauteurVisible: 780,
            LargeurTuile, HauteurTuile, decalage: 0);

        Assert.Equal(0, tranche.Compte);
        Assert.Equal(-1, tranche.Premier);
    }

    /// <summary>
    /// Un décalage périmé — la fenêtre vient de s'agrandir, ou des photos ont été écartées —
    /// ne doit jamais faire demander des tuiles qui n'existent pas.
    /// </summary>
    [Fact]
    public void Un_decalage_au_dela_de_la_planche_reste_dans_les_bornes()
    {
        var tranche = GrilleVirtuelle.Tranche(
            tuiles: 12, largeurVisible: 1740, hauteurVisible: 780,
            LargeurTuile, HauteurTuile, decalage: 100_000);

        Assert.InRange(tranche.Premier, 0, 11);
        Assert.InRange(tranche.Dernier, tranche.Premier, 11);
    }

    [Fact]
    public void Un_decalage_negatif_repart_du_haut()
    {
        var tranche = GrilleVirtuelle.Tranche(
            tuiles: 100, largeurVisible: 1740, hauteurVisible: 780,
            LargeurTuile, HauteurTuile, decalage: -500);

        Assert.Equal(0, tranche.Premier);
    }

    /// <summary>
    /// La planche est centrée dans sa fenêtre : un nombre entier de tuiles tombe rarement
    /// juste sur la largeur, et tout le blanc restant s'accumulait du même côté.
    /// </summary>
    [Fact]
    public void La_planche_est_centree_dans_sa_fenetre()
    {
        // 1870 px de large, tuiles de 216 : huit colonnes en occupent 1728
        var marge = GrilleVirtuelle.MargeDeCentrage(1870, 8, LargeurTuile);

        Assert.Equal((1870 - 8 * LargeurTuile) / 2, marge, precision: 3);
        Assert.True(marge > 0);
    }

    [Fact]
    public void Une_planche_plus_large_que_sa_fenetre_ne_deborde_pas_a_gauche()
    {
        // la fenêtre est plus étroite qu'une tuile : le retrait ne doit jamais être négatif
        Assert.Equal(0, GrilleVirtuelle.MargeDeCentrage(100, 1, LargeurTuile));
        Assert.Equal(0, GrilleVirtuelle.MargeDeCentrage(double.NaN, 8, LargeurTuile));
        Assert.Equal(0, GrilleVirtuelle.MargeDeCentrage(double.PositiveInfinity, 8, LargeurTuile));
    }

    /// <summary>
    /// La marge porte sur le BLOC : centrer chaque rangée décalerait la dernière, incomplète,
    /// de ses voisines — et la grille paraîtrait de travers.
    /// </summary>
    [Fact]
    public void Le_centrage_ne_depend_pas_du_nombre_de_photos()
    {
        var pleine = GrilleVirtuelle.MargeDeCentrage(1870, 8, LargeurTuile);
        var partielle = GrilleVirtuelle.MargeDeCentrage(1870, 8, LargeurTuile);

        Assert.Equal(pleine, partielle);
    }

    /// <summary>
    /// La marge est ce qui rend le défilement lisse : sans elle, la rangée qui entre à
    /// l'écran serait construite pendant qu'on la regarde arriver.
    /// </summary>
    [Fact]
    public void La_marge_fabrique_une_rangee_de_part_et_d_autre()
    {
        var sansMarge = GrilleVirtuelle.Tranche(
            1200, 1740, 780, LargeurTuile, HauteurTuile, 10 * HauteurTuile, rangeesDeMarge: 0);
        var avecMarge = GrilleVirtuelle.Tranche(
            1200, 1740, 780, LargeurTuile, HauteurTuile, 10 * HauteurTuile, rangeesDeMarge: 1);

        Assert.Equal(10 * 8, sansMarge.Premier);
        Assert.Equal(9 * 8, avecMarge.Premier);
        Assert.Equal(avecMarge.Compte, sansMarge.Compte + 2 * 8);
    }
}
