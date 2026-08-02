using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le contour de découpe des tirages en « photo entière ».
///
/// Ce mode sort la photo entourée de blanc pour qu'elle s'adapte au papier — et rien ne disait
/// alors où passer les ciseaux. Le trait est vérifié sur les PIXELS du tirage : qu'il soit là,
/// qu'il soit au bon endroit, et qu'il reste assez fin pour que la coupe l'emporte.
/// </summary>
public class CutBorderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CutBorder-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;

    private const int Dpi = 300;

    /// <summary>Un 10×15 couché, et une photo carrée : le blanc tombera à gauche et à droite.</summary>
    private static int SheetW => MmPx.ToPixels(152, Dpi);
    private static int SheetH => MmPx.ToPixels(102, Dpi);

    public CutBorderTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "carree.png");

        // un gris moyen : ni blanc (le fond) ni noir (le trait), donc les trois se distinguent
        using var photo = new MagickImage(MagickColor.FromRgb(128, 128, 128), 800, 800);
        photo.Write(_source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private MagickImage Rendre(FitMode fit, bool contour)
    {
        var sortie = Path.Combine(_root, $"tirage-{Guid.NewGuid():N}.png");

        ImagePipeline.RenderToFile(
            new RenderRequest(_source, SheetW, SheetH, CropSpec.Full, 0, 0, fit, 0,
                new ImageAdjustments(), null, contour),
            sortie, Dpi);

        return new MagickImage(sortie);
    }

    /// <summary>
    /// Clarté d'un pixel, 0 à 255.
    ///
    /// Le premier canal seulement : la planche de test est neutre, et ImageMagick écrit une
    /// image neutre en NIVEAUX DE GRIS — le canal bleu n'existe alors pas et se lit 0, ce qui
    /// ferait passer du blanc pour du jaune.
    /// </summary>
    private static int Clarte(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).GetChannel(0);
    }

    /// <summary>La colonne la plus sombre trouvée sur une ligne, et son abscisse.</summary>
    private static (int X, int Valeur) PlusSombreSurLaLigne(IMagickImage<byte> image, int y)
    {
        using var pixels = image.GetPixels();
        var meilleurX = 0;
        var meilleur = 255;

        for (var x = 0; x < (int)image.Width; x++)
        {
            var v = pixels.GetPixel(x, y).GetChannel(0);
            if (v >= meilleur) continue;
            meilleur = v;
            meilleurX = x;
        }

        return (meilleurX, meilleur);
    }

    [Fact]
    public void Sans_l_option_aucun_trait_noir_n_apparait()
    {
        using var tirage = Rendre(FitMode.Fit, contour: false);

        var (_, plusSombre) = PlusSombreSurLaLigne(tirage, SheetH / 2);

        // le gris de la photo est à 128 : rien ne doit descendre nettement en dessous
        Assert.True(plusSombre > 100, $"un pixel à {plusSombre} : quelque chose a été tracé");
    }

    [Fact]
    public void Avec_l_option_un_trait_noir_borde_la_photo()
    {
        using var tirage = Rendre(FitMode.Fit, contour: true);

        var (x, plusSombre) = PlusSombreSurLaLigne(tirage, SheetH / 2);

        Assert.True(plusSombre < 60, $"aucun trait : le plus sombre est à {plusSombre}");

        // la photo est carrée sur un tirage couché : son bord gauche tombe au tiers environ,
        // certainement pas au bord de la feuille
        var borteGauche = (SheetW - SheetH) / 2;
        Assert.InRange(x, borteGauche - 4, borteGauche + 4);
    }

    /// <summary>
    /// Le blanc reste blanc à l'extérieur du trait : celui-ci borde la photo, il n'encadre pas
    /// la feuille. Un cadre posé au bord du papier ne servirait à rien — on coupe la photo.
    /// </summary>
    [Fact]
    public void Le_trait_borde_la_photo_et_non_la_feuille()
    {
        using var tirage = Rendre(FitMode.Fit, contour: true);

        var bordDeFeuille = Clarte(tirage, 2, SheetH / 2);

        Assert.True(bordDeFeuille > 240,
            $"le bord de la feuille devrait rester blanc, trouvé {bordDeFeuille}");
    }

    /// <summary>
    /// Le trait doit rester fin — deux dixièmes de millimètre — pour que le coup de ciseaux le
    /// fasse disparaître. Un trait large laisserait un liseré noir sur la photo coupée.
    /// </summary>
    [Fact]
    public void Le_trait_reste_assez_fin_pour_que_la_coupe_l_emporte()
    {
        using var tirage = Rendre(FitMode.Fit, contour: true);
        using var pixels = tirage.GetPixels();

        var y = SheetH / 2;
        var sombres = 0;
        for (var x = 0; x < SheetW / 2; x++)
            if (pixels.GetPixel(x, y).GetChannel(0) < 100)
                sombres++;

        // 0,2 mm à 300 ppp = 2 px ; on tolère l'anticrénelage de part et d'autre
        Assert.InRange(sombres, 1, 6);
    }

    /// <summary>
    /// En « remplir le format », la photo occupe tout le tirage : il n'y a rien à recouper, et
    /// un cadre noir au ras du papier ne ferait que gâcher le tirage.
    /// </summary>
    [Fact]
    public void En_remplir_le_format_l_option_ne_trace_rien()
    {
        using var tirage = Rendre(FitMode.Fill, contour: true);

        var (_, plusSombre) = PlusSombreSurLaLigne(tirage, SheetH / 2);

        Assert.True(plusSombre > 100, $"un trait a été tracé sans marge à recouper ({plusSombre})");
    }
}
