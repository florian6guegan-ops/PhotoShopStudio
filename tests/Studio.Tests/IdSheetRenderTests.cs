using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Rendu d'une planche identité telle qu'elle sort sur la DNP : photos espacées, contour
/// de découpe, date et heure exigées par l'administration.
///
/// On vérifie sur les pixels de la planche produite, pas sur l'intention du code.
/// </summary>
public class IdSheetRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdSheet-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;

    private const int Dpi = 300;

    /// <summary>Planche 10×15 en paysage, cellules 35×45 mm : le tirage identité courant.</summary>
    private static int SheetW => MmPx.ToPixels(152, Dpi);
    private static int SheetH => MmPx.ToPixels(102, Dpi);
    private static int CellW => MmPx.ToPixels(35, Dpi);
    private static int CellH => MmPx.ToPixels(45, Dpi);

    public IdSheetRenderTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "portrait.png");

        // un gris moyen : ni blanc ni noir, pour distinguer la photo du fond et du contour
        using var photo = new MagickImage(MagickColor.FromRgb(128, 128, 128), 700, 900);
        photo.Write(_source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Rendre(int copies = 8, bool cutBorder = true, DateTime? stamp = null,
        double gapMm = SheetSpec.DefaultGapMm, int? cellHeightPx = null)
    {
        var sortie = Path.Combine(_root, $"planche-{Guid.NewGuid():N}.png");
        var hauteur = cellHeightPx ?? CellH;

        ImagePipeline.RenderIdSheetToFile(
            new RenderRequest(_source, CellW, hauteur, CropSpec.Full, 0, 0, FitMode.Fill, 0,
                new ImageAdjustments()),
            copies, gapMm, cutMarks: true,
            SheetW, SheetH, sortie, Dpi,
            cutBorder, stamp);

        return sortie;
    }

    private static SheetLayoutResult Disposition(int copies = 8, double gapMm = SheetSpec.DefaultGapMm,
        int? cellHeightPx = null) =>
        IdSheetLayout.Layout(SheetW, SheetH, CellW, cellHeightPx ?? CellH,
            MmPx.ToPixels(gapMm, Dpi), copies, MmPx.ToPixels(3, Dpi));

    /// <summary>
    /// Bande réservée en bas quand la planche est horodatée : la mention de 5 mm et l'air
    /// autour. Doit rester en accord avec ImagePipeline, faute de quoi les mesures
    /// porteraient sur la mauvaise zone.
    /// </summary>
    private static int BandeHorodatage => MmPx.ToPixels(7, Dpi);

    /// <summary>Disposition réellement produite lorsque la planche porte la date.</summary>
    private static SheetLayoutResult DispositionRendue(int copies = 8, int? cellHeightPx = null) =>
        IdSheetLayout.Layout(SheetW, SheetH, CellW, cellHeightPx ?? CellH,
            MmPx.ToPixels(SheetSpec.DefaultGapMm, Dpi), copies, MmPx.ToPixels(3, Dpi),
            BandeHorodatage);

    private static byte Niveau(IPixelCollection<byte> pixels, int x, int y) =>
        (byte)pixels.GetPixel(x, y).GetChannel(0);

    [Fact]
    public void La_planche_sort_aux_dimensions_du_tirage()
    {
        using var planche = new MagickImage(Rendre());

        Assert.Equal((uint)SheetW, planche.Width);
        Assert.Equal((uint)SheetH, planche.Height);
    }

    /// <summary>Huit photos, c'est ce que la boutique vend ; il faut qu'elles tiennent.</summary>
    [Fact]
    public void Huit_photos_tiennent_sur_le_tirage()
    {
        var disposition = Disposition();

        Assert.Equal(8, disposition.Cells.Count);
        Assert.All(disposition.Cells, c => Assert.True(c.Right <= SheetW && c.Bottom <= SheetH));
    }

    /// <summary>
    /// Les photos doivent être écartées : sans espace entre elles, un coup de ciseaux de
    /// travers entame la voisine.
    /// </summary>
    [Fact]
    public void Les_photos_sont_ecartees_les_unes_des_autres()
    {
        var disposition = Disposition();
        var attendu = MmPx.ToPixels(SheetSpec.DefaultGapMm, Dpi);

        var premiere = disposition.Cells[0];
        var seconde = disposition.Cells[1];

        Assert.Equal(attendu, seconde.X - premiere.Right);
        Assert.True(attendu > 0, "l'écart doit être réel");
    }

    /// <summary>Le contour de découpe doit être noir et se trouver sur le bord de la photo.</summary>
    [Fact]
    public void Un_contour_noir_entoure_chaque_photo()
    {
        using var planche = new MagickImage(Rendre(cutBorder: true));
        using var pixels = planche.GetPixels();
        var cellule = Disposition().Cells[0];

        var surLeBord = Niveau(pixels, cellule.X, cellule.Y + cellule.Height / 2);
        var dansLaPhoto = Niveau(pixels, cellule.X + cellule.Width / 2, cellule.Y + cellule.Height / 2);

        Assert.True(surLeBord < 60, $"le contour doit être noir (obtenu {surLeBord})");
        Assert.True(dansLaPhoto > 100, $"la photo ne doit pas être noircie (obtenu {dansLaPhoto})");
    }

    [Fact]
    public void Sans_contour_le_bord_de_la_photo_reste_clair()
    {
        using var planche = new MagickImage(Rendre(cutBorder: false));
        using var pixels = planche.GetPixels();
        var cellule = Disposition().Cells[0];

        var surLeBord = Niveau(pixels, cellule.X, cellule.Y + cellule.Height / 2);

        Assert.True(surLeBord > 100, $"aucun contour attendu (obtenu {surLeBord})");
    }

    /// <summary>
    /// Le contour doit rester fin : un trait large laisserait un liseré noir sur la photo
    /// une fois coupée.
    /// </summary>
    [Fact]
    public void Le_contour_reste_fin()
    {
        using var planche = new MagickImage(Rendre(cutBorder: true));
        using var pixels = planche.GetPixels();
        var cellule = Disposition().Cells[0];
        var y = cellule.Y + cellule.Height / 2;

        var epaisseur = 0;
        for (var x = cellule.X - 4; x < cellule.X + 12; x++)
            if (Niveau(pixels, x, y) < 60) epaisseur++;

        Assert.InRange(epaisseur, 1, MmPx.ToPixels(0.5, Dpi));
    }

    /// <summary>
    /// La date et l'heure sont exigées par l'administration : elles doivent apparaître
    /// dans la marge, sous les photos.
    /// </summary>
    [Fact]
    public void La_date_et_l_heure_sont_imprimees_dans_la_marge()
    {
        var moment = new DateTime(2026, 7, 31, 19, 42, 0);
        using var planche = new MagickImage(Rendre(stamp: moment));
        using var pixels = planche.GetPixels();

        var basPhotos = DispositionRendue().Cells.Max(c => c.Bottom);
        var encre = CompterPixelsSombres(pixels, basPhotos, SheetH);

        Assert.True(encre > 50, $"la mention doit être imprimée (pixels sombres : {encre})");
    }

    /// <summary>
    /// La mention doit être lisible sur le tirage, pas seulement présente. DiLand écrit la
    /// sienne au corps de 5 mm ; en dessous elle ne se lit plus, ce qui a été constaté sur
    /// une planche de contrôle le 31/07/2026.
    ///
    /// On mesure la hauteur des chiffres : pour un corps de 5 mm, Arial donne environ
    /// 3,6 mm de haut de chiffre, plus les jambages du « / ».
    /// </summary>
    [Fact]
    public void La_mention_est_ecrite_assez_grand_pour_etre_lue()
    {
        using var planche = new MagickImage(Rendre(stamp: new DateTime(2026, 7, 31, 19, 42, 0)));
        using var pixels = planche.GetPixels();

        var haut = int.MaxValue;
        var bas = -1;
        var gauche = int.MaxValue;
        var droite = -1;

        var basPhotos = DispositionRendue().Cells.Max(c => c.Bottom);
        for (var y = basPhotos + 2; y < SheetH; y++)
            for (var x = 0; x < SheetW; x++)
                if (Niveau(pixels, x, y) < 140)
                {
                    haut = Math.Min(haut, y);
                    bas = Math.Max(bas, y);
                    gauche = Math.Min(gauche, x);
                    droite = Math.Max(droite, x);
                }

        Assert.True(bas > 0, "la mention doit être présente");

        var hauteurMm = (bas - haut + 1) * 25.4 / Dpi;
        var largeurMm = (droite - gauche + 1) * 25.4 / Dpi;

        Assert.InRange(hauteurMm, 3.0, 6.0);
        Assert.InRange(largeurMm, 25.0, 60.0);
    }

    [Fact]
    public void Sans_horodatage_la_marge_reste_vierge()
    {
        using var planche = new MagickImage(Rendre(stamp: null));
        using var pixels = planche.GetPixels();

        var basPhotos = Disposition().Cells.Max(c => c.Bottom);
        var encre = CompterPixelsSombres(pixels, basPhotos, SheetH);

        Assert.True(encre < 50, $"la marge doit rester vierge (pixels sombres : {encre})");
    }

    /// <summary>
    /// Quand la marge est trop courte, mieux vaut ne rien écrire : mordre sur les photos
    /// les rendrait non conformes, ce qui serait pire que l'absence de mention.
    /// </summary>
    [Fact]
    public void Une_marge_trop_courte_n_est_pas_horodatee()
    {
        // des cellules plus hautes : la grille occupe presque toute la planche et ne
        // laisse plus de quoi écrire dessous
        var hautes = MmPx.ToPixels(49, Dpi);
        var sortie = Rendre(stamp: new DateTime(2026, 7, 31, 19, 42, 0), cellHeightPx: hautes);

        using var planche = new MagickImage(sortie);
        var disposition = DispositionRendue(cellHeightPx: hautes);
        var basPhotos = disposition.Cells.Max(c => c.Bottom);

        Assert.True(SheetH - basPhotos < MmPx.ToPixels(3.5, Dpi),
            "ce cas ne vaut que si la marge est effectivement trop courte");

        using var pixels = planche.GetPixels();
        Assert.True(CompterPixelsSombres(pixels, basPhotos, SheetH) < 50,
            "rien ne doit être écrit faute de place");
    }

    /// <summary>
    /// Compte l'encre déposée dans une bande horizontale.
    ///
    /// Le seuil sépare deux choses qui coexistent dans la marge : le texte, très
    /// anticrénelé donc surtout en gris moyen, et les traits de coupe tracés en #9E9E9E,
    /// soit 158. Mesuré sur une planche réelle, le texte pèse une centaine de pixels sous
    /// 140 tandis que les traits n'apparaissent qu'au-dessus de 150.
    /// </summary>
    private static int CompterPixelsSombres(IPixelCollection<byte> pixels, int yDebut, int yFin)
    {
        var sombres = 0;
        for (var y = yDebut + 2; y < yFin; y++)
            for (var x = 0; x < SheetW; x++)
                if (Niveau(pixels, x, y) < 140) sombres++;

        return sombres;
    }
}
