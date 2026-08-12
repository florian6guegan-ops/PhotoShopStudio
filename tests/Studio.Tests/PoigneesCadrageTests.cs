using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le cadrage tiré par un coin, sur l'écran des photos d'identité.
///
/// Il ne se réglait qu'à la molette. Les quatre carrés ajoutés le 11/08/2026 le rendent
/// saisissable à la souris, et ce qu'ils promettent tient en une phrase : <b>le coin
/// opposé ne bouge pas</b>. Sans cela, le cadre fuit sous le curseur et il faut le
/// rattraper au déplacement — c'est précisément ce qu'on voulait éviter.
/// </summary>
public class PoigneesCadrageTests
{
    private const int LargeurImage = 4000;
    private const int HauteurImage = 6000;

    /// <summary>Le format d'une photo d'identité française : 35 × 45.</summary>
    private const double Format = 35.0 / 45.0;

    private static CropSpec Depart() =>
        CropMath.Zoom(CropMath.CenterCrop(LargeurImage, HauteurImage, Format), 0.6,
            LargeurImage, HauteurImage, Format);

    /// <summary>On tire le coin haut-gauche : le bas-droit reste où il est.</summary>
    [Fact]
    public void Tirer_un_coin_laisse_le_coin_oppose_en_place()
    {
        var depart = Depart();

        var serre = CropMath.ZoomDepuisUnCoin(
            depart, 0.7, ancreX: 1, ancreY: 1, LargeurImage, HauteurImage, Format);

        Assert.Equal(depart.X + depart.Width, serre.X + serre.Width, 6);
        Assert.Equal(depart.Y + depart.Height, serre.Y + serre.Height, 6);
        Assert.True(serre.Width < depart.Width, "le cadre devait se resserrer");
    }

    /// <summary>Et symétriquement, en tirant le coin bas-droit c'est le haut-gauche qui tient.</summary>
    [Fact]
    public void L_ancre_haut_gauche_tient_aussi()
    {
        var depart = Depart();

        var serre = CropMath.ZoomDepuisUnCoin(
            depart, 0.8, ancreX: 0, ancreY: 0, LargeurImage, HauteurImage, Format);

        Assert.Equal(depart.X, serre.X, 6);
        Assert.Equal(depart.Y, serre.Y, 6);
    }

    /// <summary>
    /// Une encoche de côté tire le bord OPPOSÉ comme point fixe, et le milieu des deux
    /// autres bords ne bouge pas non plus : le cadre s'ouvre en éventail depuis ce côté-là.
    /// </summary>
    [Fact]
    public void Tirer_un_cote_laisse_le_cote_oppose_en_place()
    {
        var depart = Depart();

        // on tire le bord gauche : le bord droit tient, à mi-hauteur
        var elargi = CropMath.ZoomDepuisUnCoin(
            depart, 1.2, ancreX: 1, ancreY: 0.5, LargeurImage, HauteurImage, Format);

        Assert.Equal(depart.X + depart.Width, elargi.X + elargi.Width, 6);

        // et le cadre reste centré sur la même ligne d'horizon
        Assert.Equal(depart.Y + depart.Height / 2, elargi.Y + elargi.Height / 2, 6);
        Assert.True(elargi.Width > depart.Width, "le cadre devait s'élargir");
    }

    /// <summary>
    /// <b>Le format ne se négocie jamais</b>, pas même en tirant un côté. Les proportions
    /// viennent du document visé : une photo d'identité étirée est refusée au guichet. Une
    /// encoche de côté ne déforme donc pas — elle redimensionne, et l'autre dimension suit.
    /// </summary>
    [Fact]
    public void Tirer_un_cote_ne_deforme_pas_le_cadre()
    {
        var depart = Depart();
        var avant = depart.Width / depart.Height;

        foreach (var (x, y) in new[] { (0.5, 1.0), (0.5, 0.0), (1.0, 0.5), (0.0, 0.5) })
        {
            var tire = CropMath.ZoomDepuisUnCoin(
                depart, 1.3, x, y, LargeurImage, HauteurImage, Format);

            Assert.Equal(avant, tire.Width / tire.Height, 4);
        }
    }

    /// <summary>
    /// <b>Le format ne se négocie jamais.</b> Les proportions viennent du document visé :
    /// une photo d'identité étirée est refusée au guichet.
    /// </summary>
    [Fact]
    public void Le_format_du_document_est_preserve()
    {
        var depart = Depart();
        var avant = depart.Width / depart.Height;

        foreach (var facteur in new[] { 0.5, 0.9, 1.3, 2.0 })
        {
            var tire = CropMath.ZoomDepuisUnCoin(
                depart, facteur, 1, 1, LargeurImage, HauteurImage, Format);

            Assert.Equal(avant, tire.Width / tire.Height, 4);
        }
    }

    /// <summary>
    /// Tirer vers l'extérieur ne fait jamais sortir le cadre de l'image : la butée est celle
    /// du zoom à la molette, on ne la refait pas ici.
    /// </summary>
    [Fact]
    public void Le_cadre_ne_sort_jamais_de_l_image()
    {
        var depart = Depart();

        var enorme = CropMath.ZoomDepuisUnCoin(
            depart, 50, 1, 1, LargeurImage, HauteurImage, Format);

        Assert.True(enorme.X >= -0.000001, $"bord gauche à {enorme.X}");
        Assert.True(enorme.Y >= -0.000001, $"bord haut à {enorme.Y}");
        Assert.True(enorme.X + enorme.Width <= 1.000001, "bord droit hors de l'image");
        Assert.True(enorme.Y + enorme.Height <= 1.000001, "bord bas hors de l'image");
    }

    /// <summary>Le grossissement reste borné, comme à la molette : pas de cadre microscopique.</summary>
    [Fact]
    public void Le_resserrement_reste_borne()
    {
        var max = CropMath.CenterCrop(LargeurImage, HauteurImage, Format);

        var minuscule = CropMath.ZoomDepuisUnCoin(
            max, 0.001, 1, 1, LargeurImage, HauteurImage, Format);

        Assert.True(minuscule.Width >= max.Width * CropMath.MinZoomShare - 0.000001,
            $"le cadre est descendu à {minuscule.Width} pour un plancher de " +
            $"{max.Width * CropMath.MinZoomShare}");
    }
}
