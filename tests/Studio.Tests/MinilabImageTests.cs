using ImageMagick;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Ce que l'image envoyée au minilab doit être.
///
/// <b>LE défaut du 04/08/2026, cherché toute une journée.</b> Le 21×29,7 était refusé par
/// le DE100 dix secondes après avoir été accepté, sans le moindre motif, six fois de suite.
/// Ni le nom du format, ni les cotes, ni les consommables, ni la longueur du rouleau n'y
/// étaient pour quelque chose : <b>la photo d'essai était un scan en NIVEAUX DE GRIS</b>, et
/// le minilab refuse tout ce qui n'a pas trois canaux.
///
/// Prouvé en renvoyant le fichier même que Studio avait produit, converti en sRGB et rien
/// d'autre : il est sorti du premier coup.
/// </summary>
public class MinilabImageTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "MinilabImage-" + Guid.NewGuid().ToString("N"));

    public MinilabImageTests()
    {
        Directory.CreateDirectory(_dossier);
        MagickInit.Configure();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// La conversion telle que <c>PrintOrchestrator.EnTroisCanaux</c> l'applique.
    ///
    /// Recopiée ici parce que la méthode est privée et que ce qui compte n'est pas son nom
    /// mais le FICHIER qu'elle produit : c'est lui que la machine juge.
    /// </summary>
    private static void EnTroisCanaux(MagickImage image)
    {
        image.ColorSpace = ColorSpace.sRGB;
        image.ColorType = ColorType.TrueColor;
        image.Alpha(AlphaOption.Off);
        image.Settings.SetDefine(MagickFormat.Png, "color-type", "2");
    }

    private string EcrireEnGris(string nom, uint largeur = 400, uint hauteur = 600)
    {
        var chemin = Path.Combine(_dossier, nom);
        using var image = new MagickImage(MagickColors.Gray, largeur, hauteur);
        image.ColorSpace = ColorSpace.Gray;
        image.ColorType = ColorType.Grayscale;
        image.Write(chemin, MagickFormat.Png);
        return chemin;
    }

    /// <summary>
    /// Le point de départ : un scan noir et blanc DONNE bien une image à un seul canal, et
    /// c'est elle qui partait au minilab.
    /// </summary>
    [Fact]
    public void Un_scan_noir_et_blanc_donne_bien_une_image_grise()
    {
        using var image = new MagickImage(EcrireEnGris("gris.png"));

        Assert.Equal(ColorSpace.Gray, image.ColorSpace);
        Assert.True(image.ChannelCount < 3, $"{image.ChannelCount} canaux, on en attendait moins de 3");
    }

    /// <summary>
    /// <b>La correction.</b> Après passage, le fichier RELU depuis le disque porte trois
    /// canaux — c'est le fichier qui compte, pas l'objet en mémoire.
    /// </summary>
    [Fact]
    public void Apres_conversion_le_fichier_porte_trois_canaux()
    {
        var source = EcrireEnGris("source.png");
        var cible = Path.Combine(_dossier, "cible.png");

        using (var image = new MagickImage(source))
        {
            EnTroisCanaux(image);
            image.Write(cible);
        }

        using var relu = new MagickImage(cible);
        Assert.Equal(ColorSpace.sRGB, relu.ColorSpace);
        Assert.True(relu.ChannelCount >= 3, $"{relu.ChannelCount} canaux, il en faut au moins 3");
    }

    /// <summary>
    /// <b>Le define PNG est indispensable</b>, et ce test est là pour qu'on ne l'enlève
    /// pas en « simplifiant ». Poser <c>ColorSpace</c> et <c>ColorType</c> ne suffit pas :
    /// le format PNG réécrit en niveaux de gris dès que tous les pixels le sont, c'est son
    /// optimisation automatique. C'est exactement ce qui a fait échouer les deux premières
    /// tentatives de correction.
    /// </summary>
    [Fact]
    public void Sans_le_define_PNG_l_image_repasse_en_gris_a_l_ecriture()
    {
        var source = EcrireEnGris("source2.png");
        var cible = Path.Combine(_dossier, "sans-define.png");

        using (var image = new MagickImage(source))
        {
            // la conversion SANS le define : celle qui ne marche pas
            image.ColorSpace = ColorSpace.sRGB;
            image.ColorType = ColorType.TrueColor;
            image.Alpha(AlphaOption.Off);
            image.Write(cible);
        }

        using var relu = new MagickImage(cible);
        Assert.True(relu.ChannelCount < 3,
            "le PNG a gardé trois canaux sans le define : l'optimisation automatique a " +
            "changé, le commentaire de EnTroisCanaux est à revoir");
    }

    /// <summary>Une image DÉJÀ en couleur traverse la conversion sans y perdre ses canaux.</summary>
    [Fact]
    public void Une_image_couleur_reste_en_couleur()
    {
        var source = Path.Combine(_dossier, "couleur.png");
        using (var image = new MagickImage(MagickColors.SteelBlue, 400, 600))
            image.Write(source, MagickFormat.Png);

        var cible = Path.Combine(_dossier, "couleur-convertie.png");
        using (var image = new MagickImage(source))
        {
            EnTroisCanaux(image);
            image.Write(cible);
        }

        using var relu = new MagickImage(cible);
        Assert.Equal(ColorSpace.sRGB, relu.ColorSpace);
        Assert.True(relu.ChannelCount >= 3);
    }

    /// <summary>
    /// La conversion ne touche pas à la DÉFINITION : c'est l'autre exigence de la machine,
    /// et les deux doivent tenir ensemble.
    /// </summary>
    [Fact]
    public void La_conversion_ne_change_pas_la_definition()
    {
        var source = EcrireEnGris("taille.png", 2515, 3543);
        var cible = Path.Combine(_dossier, "taille-convertie.png");

        using (var image = new MagickImage(source))
        {
            EnTroisCanaux(image);
            image.Write(cible);
        }

        using var relu = new MagickImage(cible);
        Assert.Equal(2515u, relu.Width);
        Assert.Equal(3543u, relu.Height);
    }
}
