using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Cadrage identité déduit de deux repères posés par l'opérateur — sommet du crâne et
/// bas du menton — comme le fait DiLand.
///
/// C'est la méthode la plus fiable : la détection automatique se trompe sur les cheveux
/// volumineux, les couvre-chefs et les bébés, alors que ces deux points-là ne se
/// discutent pas. Une photo mal cadrée est refusée au guichet.
/// </summary>
public class IdPhotoMarkerTests
{
    private const int Largeur = 3000;
    private const int Hauteur = 4000;

    private static NormPoint Crane(double y = 0.20) => new(0.5, y);
    private static NormPoint Menton(double y = 0.60) => new(0.5, y);

    [Fact]
    public void La_tete_va_du_crane_au_menton()
    {
        var tete = IdPhotoFr.HeadFromMarkers(Crane(0.20), Menton(0.60));

        Assert.Equal(0.20, tete.Y, 6);
        Assert.Equal(0.40, tete.Height, 6);
    }

    /// <summary>L'opérateur peut poser le menton avant le crâne : l'ordre ne doit pas compter.</summary>
    [Fact]
    public void L_ordre_des_reperes_est_indifferent()
    {
        var normal = IdPhotoFr.HeadFromMarkers(Crane(0.20), Menton(0.60));
        var inverse = IdPhotoFr.HeadFromMarkers(Menton(0.60), Crane(0.20));

        Assert.Equal(normal, inverse);
    }

    [Fact]
    public void L_axe_du_visage_passe_entre_les_deux_reperes()
    {
        // tête légèrement penchée : le crâne et le menton ne sont pas alignés
        var tete = IdPhotoFr.HeadFromMarkers(new NormPoint(0.46, 0.20), new NormPoint(0.54, 0.60));

        Assert.Equal(0.50, tete.CenterX, 6);
    }

    [Fact]
    public void Deux_reperes_confondus_sont_refuses()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => IdPhotoFr.HeadFromMarkers(Crane(0.40), Menton(0.40)));

        Assert.Contains("visage", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // — le cadrage obtenu doit être conforme —

    [Fact]
    public void Le_cadrage_place_la_tete_a_la_hauteur_visee()
    {
        var crane = Crane(0.20);
        var menton = Menton(0.60);
        var tete = IdPhotoFr.HeadFromMarkers(crane, menton);

        var cadre = IdPhotoFr.CropFromMarkers(crane, menton, Largeur, Hauteur);
        var mesure = IdPhotoFr.Check(cadre, tete);

        Assert.Equal(IdPhotoFr.TargetHeadMm, mesure.HeadHeightMm, 1);
        Assert.Equal(IdPhotoFr.TargetCrownMarginMm, mesure.CrownMarginMm, 1);
        Assert.True(mesure.Compliant, "le cadrage déduit des repères doit être conforme");
    }

    [Theory]
    [InlineData(0.10, 0.50)]
    [InlineData(0.25, 0.55)]
    [InlineData(0.30, 0.75)]
    public void Le_cadrage_reste_conforme_quelle_que_soit_la_taille_du_visage(double yCrane, double yMenton)
    {
        var crane = new NormPoint(0.5, yCrane);
        var menton = new NormPoint(0.5, yMenton);

        var cadre = IdPhotoFr.CropFromMarkers(crane, menton, Largeur, Hauteur);
        var mesure = IdPhotoFr.Check(cadre, IdPhotoFr.HeadFromMarkers(crane, menton));

        Assert.True(mesure.HeadHeightOk, $"hauteur de visage obtenue : {mesure.HeadHeightMm:0.0} mm");
        Assert.True(mesure.CrownOk, $"marge au-dessus du crâne : {mesure.CrownMarginMm:0.0} mm");
    }

    /// <summary>Une tête penchée doit rester centrée sur le tirage.</summary>
    [Fact]
    public void Une_tete_penchee_reste_centree()
    {
        var crane = new NormPoint(0.44, 0.22);
        var menton = new NormPoint(0.52, 0.62);

        var cadre = IdPhotoFr.CropFromMarkers(crane, menton, Largeur, Hauteur);
        var mesure = IdPhotoFr.Check(cadre, IdPhotoFr.HeadFromMarkers(crane, menton));

        Assert.True(mesure.CenteredOk, $"décentrage : {mesure.CenterOffsetMm:0.0} mm");
    }

    [Fact]
    public void Le_cadre_garde_les_proportions_35_sur_45()
    {
        var cadre = IdPhotoFr.CropFromMarkers(Crane(), Menton(), Largeur, Hauteur);

        var largeurPx = cadre.Width * Largeur;
        var hauteurPx = cadre.Height * Hauteur;

        Assert.Equal(IdPhotoFr.PhotoWidthMm / IdPhotoFr.PhotoHeightMm, largeurPx / hauteurPx, 3);
    }

    [Fact]
    public void Le_cadre_reste_dans_l_image()
    {
        // visage très haut dans le cadre : le calcul déborderait vers le haut
        var cadre = IdPhotoFr.CropFromMarkers(new NormPoint(0.5, 0.02), new NormPoint(0.5, 0.42),
            Largeur, Hauteur);

        Assert.InRange(cadre.X, 0, 1);
        Assert.InRange(cadre.Y, 0, 1);
        Assert.InRange(cadre.X + cadre.Width, 0, 1);
        Assert.InRange(cadre.Y + cadre.Height, 0, 1);
    }
}
