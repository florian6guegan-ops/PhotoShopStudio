using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Capacité d'une planche d'identité selon l'orientation du tirage.
///
/// La boutique veut des planches de 8, avec 4 et 6 également proposés. Ce n'est pas un
/// réglage libre : c'est de la géométrie. Sur un 10×15 en portrait, huit vignettes de
/// 35×45 ne rentrent pas — il faut la planche en paysage.
/// </summary>
public class IdSheetCapacityTests
{
    private const int Dpi = 300;

    private static int Px(double mm) => MmPx.ToPixels(mm, Dpi);

    private static int Capacite(double largeurMm, double hauteurMm) =>
        IdSheetLayout.MaxCopies(Px(largeurMm), Px(hauteurMm), Px(35), Px(45), Px(2));

    [Fact]
    public void Un_10x15_en_portrait_ne_tient_que_six_vignettes()
    {
        Assert.Equal(6, Capacite(102, 152));
    }

    /// <summary>C'est la raison du passage en paysage : 4 colonnes au lieu de 2.</summary>
    [Fact]
    public void Un_10x15_en_paysage_en_tient_huit()
    {
        Assert.Equal(8, Capacite(152, 102));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Les_trois_planches_proposees_tiennent_en_paysage(int vignettes)
    {
        var disposition = IdSheetLayout.Layout(
            Px(152), Px(102), Px(35), Px(45), Px(2), vignettes);

        Assert.Equal(vignettes, disposition.Cells.Count);
        Assert.True(disposition.Columns * disposition.Rows >= vignettes);
    }

    [Fact]
    public void La_planche_de_huit_se_range_en_quatre_colonnes_sur_deux_rangees()
    {
        var disposition = IdSheetLayout.Layout(Px(152), Px(102), Px(35), Px(45), Px(2), 8);

        Assert.Equal(4, disposition.Columns);
        Assert.Equal(2, disposition.Rows);
    }

    [Fact]
    public void Les_vignettes_restent_dans_la_planche()
    {
        var largeur = Px(152);
        var hauteur = Px(102);
        var disposition = IdSheetLayout.Layout(largeur, hauteur, Px(35), Px(45), Px(2), 8);

        Assert.All(disposition.Cells, cellule =>
        {
            Assert.InRange(cellule.X, 0, largeur);
            Assert.InRange(cellule.Y, 0, hauteur);
            Assert.InRange(cellule.Right, 0, largeur);
            Assert.InRange(cellule.Bottom, 0, hauteur);
        });
    }

    /// <summary>Neuf vignettes ne tiennent nulle part sur un 10×15 : le refus doit être net.</summary>
    [Fact]
    public void Au_dela_de_huit_la_planche_est_refusee()
    {
        Assert.Throws<InvalidOperationException>(
            () => IdSheetLayout.Layout(Px(152), Px(102), Px(35), Px(45), Px(2), 9));
    }

    /// <summary>Le bloc de vignettes doit être centré : les marges se coupent au massicot.</summary>
    [Fact]
    public void Le_bloc_est_centre_sur_la_planche()
    {
        var largeur = Px(152);
        var hauteur = Px(102);
        var disposition = IdSheetLayout.Layout(largeur, hauteur, Px(35), Px(45), Px(2), 8);

        var gauche = disposition.Cells.Min(c => c.X);
        var droite = largeur - disposition.Cells.Max(c => c.Right);
        var haut = disposition.Cells.Min(c => c.Y);
        var bas = hauteur - disposition.Cells.Max(c => c.Bottom);

        Assert.True(Math.Abs(gauche - droite) <= 1, $"marges gauche {gauche} / droite {droite}");
        Assert.True(Math.Abs(haut - bas) <= 1, $"marges haut {haut} / bas {bas}");
    }
}
