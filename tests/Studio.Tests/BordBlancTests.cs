using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le « bord blanc » : un LISERÉ régulier, et non un calage.
///
/// Les deux partagent le même <see cref="FitMode.Fit"/>, et c'est ce qui les faisait
/// confondre. La « photo entière » ne coupe rien et laisse le blanc combler ce que le
/// rapport ne remplit pas — donc des marges inégales. Le bord blanc, lui, recadre la photo
/// pour qu'elle remplisse la fenêtre, et le blanc fait la même largeur des quatre côtés.
/// C'est <see cref="Product.BorderMm"/> qui les distingue, et rien d'autre.
///
/// La cote vient de DiLand, relevée dans sa base le 06/08/2026 : ses dix produits
/// « Bord blanc » portent <c>MarginTop = MarginLeft = MarginRight = MarginBottom =
/// 18,8976…</c> unités de 96 ppp, soit exactement 5 mm sur les quatre côtés.
/// </summary>
public class BordBlancTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "BordBlanc-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;

    private const int Dpi = 300;
    private const double BorderMm = 5;

    /// <summary>Un 10×15 debout, et une photo 3:2 couchée : les deux rapports diffèrent.</summary>
    private static int SheetW => MmPx.ToPixels(102, Dpi);
    private static int SheetH => MmPx.ToPixels(152, Dpi);
    private static int BorderPx => MmPx.ToPixels(BorderMm, Dpi);

    public BordBlancTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "photo.png");

        // gris moyen : ni blanc (le liseré) ni noir, les deux se distinguent donc
        using var photo = new MagickImage(MagickColor.FromRgb(128, 128, 128), 3000, 2000);
        photo.Write(_source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private MagickImage Rendre(int borderPx)
    {
        var sortie = Path.Combine(_root, $"tirage-{Guid.NewGuid():N}.png");

        ImagePipeline.RenderToFile(
            new RenderRequest(_source, SheetW, SheetH, CropSpec.Full, 0, 0, FitMode.Fit,
                borderPx, new ImageAdjustments()),
            sortie, Dpi);

        return new MagickImage(sortie);
    }

    private static int Clarte(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).GetChannel(0);
    }

    /// <summary>Épaisseur du blanc sur une ligne, en partant d'un bord.</summary>
    private static int BlancDepuisLeBord(IMagickImage<byte> image, int x, int y, int dx, int dy)
    {
        using var pixels = image.GetPixels();
        var epaisseur = 0;

        while (x >= 0 && y >= 0 && x < (int)image.Width && y < (int)image.Height
               && pixels.GetPixel(x, y).GetChannel(0) > 240)
        {
            epaisseur++;
            x += dx;
            y += dy;
        }

        return epaisseur;
    }

    /// <summary>
    /// <b>L'essai qui porte la demande</b> : les quatre marges font la même épaisseur, et
    /// c'est celle du liseré. Avant, la photo n'était que mise à l'échelle dans la fenêtre :
    /// une 3:2 dans un 10×15 debout laissait deux bandes de plus de 30 mm en haut et en
    /// bas contre 5 mm sur les côtés.
    /// </summary>
    [Fact]
    public void Le_lisere_fait_la_meme_epaisseur_des_quatre_cotes()
    {
        using var tirage = Rendre(BorderPx);

        var gauche = BlancDepuisLeBord(tirage, 0, SheetH / 2, 1, 0);
        var droite = BlancDepuisLeBord(tirage, SheetW - 1, SheetH / 2, -1, 0);
        var haut = BlancDepuisLeBord(tirage, SheetW / 2, 0, 0, 1);
        var bas = BlancDepuisLeBord(tirage, SheetW / 2, SheetH - 1, 0, -1);

        // un pixel de tolérance : le centrage d'un reste impair tombe d'un côté
        Assert.InRange(gauche, BorderPx - 1, BorderPx + 1);
        Assert.InRange(droite, BorderPx - 1, BorderPx + 1);
        Assert.InRange(haut, BorderPx - 1, BorderPx + 1);
        Assert.InRange(bas, BorderPx - 1, BorderPx + 1);
    }

    /// <summary>Et la photo occupe bien toute la fenêtre : son centre n'est pas blanc.</summary>
    [Fact]
    public void La_photo_remplit_la_fenetre()
    {
        using var tirage = Rendre(BorderPx);

        Assert.True(Clarte(tirage, SheetW / 2, SheetH / 2) < 200, "le centre devrait porter la photo");

        // juste à l'intérieur du liseré, sur les quatre bords : encore de la photo
        Assert.True(Clarte(tirage, BorderPx + 3, SheetH / 2) < 200, "bord gauche de la fenêtre vide");
        Assert.True(Clarte(tirage, SheetW - BorderPx - 4, SheetH / 2) < 200, "bord droit de la fenêtre vide");
        Assert.True(Clarte(tirage, SheetW / 2, BorderPx + 3) < 200, "haut de la fenêtre vide");
        Assert.True(Clarte(tirage, SheetW / 2, SheetH - BorderPx - 4) < 200, "bas de la fenêtre vide");
    }

    /// <summary>
    /// <b>La « photo entière » n'est PAS touchée.</b> Sans liseré, rien n'est coupé et le
    /// blanc comble ce que le rapport laisse : les marges y sont inégales, et c'est tout
    /// l'objet du mode. Les deux comportements vivent sous le même FitMode.
    /// </summary>
    [Fact]
    public void Sans_lisere_la_photo_entiere_garde_ses_marges_inegales()
    {
        using var tirage = Rendre(0);

        var gauche = BlancDepuisLeBord(tirage, 0, SheetH / 2, 1, 0);
        var haut = BlancDepuisLeBord(tirage, SheetW / 2, 0, 0, 1);

        // une 3:2 couchée dans un 10×15 debout : toute la largeur, du blanc en haut et en bas
        Assert.Equal(0, gauche);
        Assert.True(haut > MmPx.ToPixels(20, Dpi), $"la photo entière devrait laisser du blanc en haut ({haut} px)");
    }

    /// <summary>La fenêtre à cadrer est le format MOINS le liseré, des deux côtés.</summary>
    [Fact]
    public void La_fenetre_du_produit_deduit_le_lisere()
    {
        var bordBlanc = new Product { WidthMm = 102, HeightMm = 152, BorderMm = 5 };
        var ordinaire = new Product { WidthMm = 102, HeightMm = 152, BorderMm = 0 };

        Assert.True(bordBlanc.ABordBlanc);
        Assert.Equal((92d, 142d), bordBlanc.FenetreMm);

        Assert.False(ordinaire.ABordBlanc);
        Assert.Equal((102d, 152d), ordinaire.FenetreMm);
    }
}
