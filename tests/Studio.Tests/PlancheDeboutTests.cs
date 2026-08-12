using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// La planche tirée DEBOUT quand le papier y tient davantage.
///
/// <b>Pourquoi elle existe.</b> Un carré de 50 mm — passeport américain, albanais — ne
/// tient qu'une rangée sur les 105 mm d'un 10×15 couché dès qu'on garde la place d'écrire
/// la date : trois photos. Le même papier debout en porte deux rangées, donc quatre, et la
/// bande y respire. Rien ne justifiait de tirer toujours dans le même sens.
///
/// <b>Ce que ces essais protègent avant tout</b> : le fichier remis à la machine garde le
/// format qu'elle attend, au pixel près. Une planche qui sortirait aux cotes inversées
/// serait refusée par le canal à format fixe du minilab — sans le moindre motif, comme le
/// 21×29,7 du 04/08/2026.
/// </summary>
public class PlancheDeboutTests : IDisposable
{
    private const int Dpi = 300;

    private static int Px(double mm) => MmPx.ToPixels(mm, Dpi);

    /// <summary>Le 10×15 de la boutique, débord compris.</summary>
    private static readonly int PapierL = Px(156.1);
    private static readonly int PapierH = Px(105);

    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "StudioDebout-" + Guid.NewGuid().ToString("N"));

    public PlancheDeboutTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private string Photo()
    {
        var chemin = Path.Combine(_dossier, "source.png");
        using var image = new MagickImage(new MagickColor("#7A5C44"), 600, 600);
        image.Write(chemin);
        return chemin;
    }

    private static SheetFooter Bande() =>
        new(new DateTime(2026, 8, 11, 17, 0, 0), "PHOTOS CONFORMES");

    /// <summary>Le carré de 50 mm : trois photos couché, quatre debout.</summary>
    [Fact]
    public void Un_carre_de_50_tient_une_photo_de_plus_debout()
    {
        var reserve = SheetFooterLayout.ReserveMinimalePx(Bande(), Dpi);

        var (copies, debout) = IdSheetLayout.MeilleureCapacite(
            PapierL, PapierH, Px(50), Px(50), Px(0.2), reserve);

        Assert.Equal(4, copies);
        Assert.True(debout, "le papier doit être tourné pour en tenir quatre");
    }

    /// <summary>
    /// <b>La planche française ne tourne pas.</b> Elle tient déjà huit photos couchée, et
    /// changer le sens du papier sur le produit courant de la boutique serait le pire des
    /// effets de bord : l'opérateur massicote toujours pareil.
    /// </summary>
    [Fact]
    public void La_planche_francaise_reste_couchee()
    {
        var reserve = SheetFooterLayout.ReserveMinimalePx(Bande(), Dpi);

        var (copies, debout) = IdSheetLayout.MeilleureCapacite(
            PapierL, PapierH, Px(35), Px(45), Px(0.2), reserve);

        Assert.Equal(8, copies);
        Assert.False(debout, "la planche française n'a aucune raison de tourner");
    }

    /// <summary>À capacité égale, on garde le sens du papier.</summary>
    [Fact]
    public void A_capacite_egale_le_papier_ne_tourne_pas()
    {
        var (_, debout) = IdSheetLayout.MeilleureCapacite(
            Px(100), Px(100), Px(30), Px(30), Px(1));

        Assert.False(debout);
    }

    /// <summary>
    /// <b>Le contrat qui compte pour la machine</b> : quelle que soit l'orientation choisie
    /// pour composer, le fichier sort AUX COTES DU PRODUIT. C'est ce que le minilab exige.
    /// </summary>
    [Fact]
    public void Le_fichier_garde_toujours_les_cotes_du_produit()
    {
        var source = Photo();
        var sortie = Path.Combine(_dossier, "planche.png");

        // quatre photos : ne tiennent que sur le papier debout
        ImagePipeline.RenderIdSheetToFile(
            new RenderRequest(source, Px(50), Px(50), CropSpec.Full, 0, 0, FitMode.Fill, 0,
                new ImageAdjustments(), null),
            copies: 4, gapMm: 0.2, cutMarks: false,
            PapierL, PapierH, sortie, Dpi, cutBorder: true, footer: Bande());

        using var planche = new MagickImage(sortie);

        Assert.Equal((uint)PapierL, planche.Width);
        Assert.Equal((uint)PapierH, planche.Height);
    }

    /// <summary>Et la planche couchée, elle, garde aussi ses cotes — rien n'a bougé pour elle.</summary>
    [Fact]
    public void Une_planche_couchee_sort_inchangee()
    {
        var source = Photo();
        var sortie = Path.Combine(_dossier, "planche-fr.png");

        ImagePipeline.RenderIdSheetToFile(
            new RenderRequest(source, Px(35), Px(45), CropSpec.Full, 0, 0, FitMode.Fill, 0,
                new ImageAdjustments(), null),
            copies: 8, gapMm: 0.2, cutMarks: false,
            PapierL, PapierH, sortie, Dpi, cutBorder: true, footer: Bande());

        using var planche = new MagickImage(sortie);

        Assert.Equal((uint)PapierL, planche.Width);
        Assert.Equal((uint)PapierH, planche.Height);
    }
}
