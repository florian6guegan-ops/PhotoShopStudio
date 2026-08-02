using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Rendu d'une planche « personnalisée ».
///
/// Elle diffère de la planche identité sur un seul point, mais qui est tout l'enjeu : les
/// cases ne portent pas la même photo. On vérifie donc, sur les pixels, que chaque photo
/// est bien allée dans SES cases — un décalage d'une case suffirait à tirer la mauvaise
/// image, et rien ne le signalerait avant que le client ne regarde.
/// </summary>
public class CustomSheetRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PlanchePerso-" + Guid.NewGuid().ToString("N"));

    private const int Dpi = 300;

    /// <summary>Un 13×18 debout, des cases de 5,5 × 8 cm : le cas de la boutique.</summary>
    private static int SheetW => MmPx.ToPixels(127, Dpi);
    private static int SheetH => MmPx.ToPixels(180, Dpi);
    private static int CellW => MmPx.ToPixels(55, Dpi);
    private static int CellH => MmPx.ToPixels(80, Dpi);

    private readonly string _rouge;
    private readonly string _bleu;

    public CustomSheetRenderTests()
    {
        Directory.CreateDirectory(_root);
        _rouge = Ecrire("rouge.png", MagickColors.Red);
        _bleu = Ecrire("bleu.png", MagickColors.Blue);
    }

    private string Ecrire(string nom, MagickColor couleur)
    {
        var chemin = Path.Combine(_root, nom);
        using var image = new MagickImage(couleur, 600, 900);
        image.Write(chemin);
        return chemin;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private static RenderRequest Case(string source) =>
        new(source, CellW, CellH, CropSpec.Full, 0, 0, FitMode.Fill, 0, new ImageAdjustments());

    private string Rendre(params ImagePipeline.SheetCell[] cellules)
    {
        var sortie = Path.Combine(_root, $"planche-{Guid.NewGuid():N}.png");
        ImagePipeline.RenderCustomSheetToFile(
            cellules, SheetSpec.DefaultGapMm, cutMarks: true,
            SheetW, SheetH, sortie, Dpi, cutBorder: true);
        return sortie;
    }

    private static SheetLayoutResult Disposition(int cases) =>
        IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH,
            MmPx.ToPixels(SheetSpec.DefaultGapMm, Dpi), cases,
            MmPx.ToPixels(3, Dpi));

    /// <summary>Couleur au cœur d'une case, à l'abri du contour de découpe.</summary>
    private static (byte R, byte G, byte B) CouleurAuCentre(IPixelCollection<byte> pixels, PixelRect zone)
    {
        var pixel = pixels.GetPixel(zone.X + zone.Width / 2, zone.Y + zone.Height / 2);

        return ((byte)pixel.GetChannel(0), (byte)pixel.GetChannel(1), (byte)pixel.GetChannel(2));
    }

    [Fact]
    public void Chaque_photo_va_dans_ses_propres_cases()
    {
        var sortie = Rendre(
            new ImagePipeline.SheetCell(Case(_rouge), 2),
            new ImagePipeline.SheetCell(Case(_bleu), 1));

        var layout = Disposition(3);
        using var planche = new MagickImage(sortie);
        using var pixels = planche.GetPixels();

        Assert.True(CouleurAuCentre(pixels, layout.Cells[0]).R > 200);
        Assert.True(CouleurAuCentre(pixels, layout.Cells[1]).R > 200);

        var troisieme = CouleurAuCentre(pixels, layout.Cells[2]);
        Assert.True(troisieme.B > 200);
        Assert.True(troisieme.R < 60);
    }

    [Fact]
    public void La_planche_sort_a_la_taille_du_papier()
    {
        var sortie = Rendre(new ImagePipeline.SheetCell(Case(_rouge), 1));

        using var planche = new MagickImage(sortie);
        Assert.Equal((uint)SheetW, planche.Width);
        Assert.Equal((uint)SheetH, planche.Height);
    }

    /// <summary>Le fond reste blanc : c'est le papier qu'on voit entre les photos.</summary>
    [Fact]
    public void Les_places_libres_restent_blanches()
    {
        var sortie = Rendre(new ImagePipeline.SheetCell(Case(_rouge), 1));

        using var planche = new MagickImage(sortie);
        using var pixels = planche.GetPixels();
        var coin = pixels.GetPixel(2, SheetH - 3);

        Assert.True(coin.GetChannel(0) > 240);
        Assert.True(coin.GetChannel(1) > 240);
        Assert.True(coin.GetChannel(2) > 240);
    }

    [Fact]
    public void Une_planche_sans_photo_est_refusee()
    {
        Assert.Throws<ArgumentException>(() => Rendre());
    }

    /// <summary>
    /// Une case d'une autre taille recouvrirait sa voisine sans que rien ne le dise : la
    /// grille est uniforme, et c'est sur cette hypothèse que les places ont été comptées.
    /// </summary>
    [Fact]
    public void Des_cases_de_tailles_differentes_sont_refusees()
    {
        var autre = new RenderRequest(_bleu, CellW + 40, CellH, CropSpec.Full, 0, 0,
            FitMode.Fill, 0, new ImageAdjustments());

        Assert.Throws<ArgumentException>(() => Rendre(
            new ImagePipeline.SheetCell(Case(_rouge), 1),
            new ImagePipeline.SheetCell(autre, 1)));
    }
}
