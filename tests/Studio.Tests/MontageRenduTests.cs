using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Rendu d'une feuille de montage : deux 24×30 sur un 40×60.
///
/// La feuille est composée COUCHÉE (60 × 40) : c'est ainsi que deux tirages debout tiennent
/// côte à côte. Les dimensions viennent donc du plan, jamais du produit — s'en écarter donne
/// une feuille où rien ne tient.
///
/// <b>Ce que ces essais protègent.</b> L'empreinte est portrait, mais une photo PAYSAGE est
/// rendue en 30×24 : elle doit être tournée pour se poser, et non recadrée. Sans cela, il
/// faudrait refuser les sélections mêlées — ou rogner une photo sur deux dans le mauvais
/// sens, sur du papier grand format, sans que rien ne le signale avant le client.
/// </summary>
public class MontageRenduTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Montage-" + Guid.NewGuid().ToString("N"));

    private const int Dpi = 300;

    /// <summary>La feuille DANS LE SENS DU PLAN — 60 × 40, et non 40 × 60.</summary>
    private static int SheetW => MmPx.ToPixels(Plan.LargeurMm, Dpi);
    private static int SheetH => MmPx.ToPixels(Plan.HauteurMm, Dpi);

    /// <summary>Le tirage, dans le sens du catalogue : 24 × 30 debout.</summary>
    private static int TirageW => MmPx.ToPixels(240, Dpi);
    private static int TirageH => MmPx.ToPixels(300, Dpi);

    private readonly string _rouge;
    private readonly string _bleu;

    public MontageRenduTests()
    {
        Directory.CreateDirectory(_root);
        _rouge = Ecrire("rouge.png", MagickColors.Red);
        _bleu = Ecrire("bleu.png", MagickColors.Blue);
    }

    private string Ecrire(string nom, MagickColor couleur)
    {
        var chemin = Path.Combine(_root, nom);
        using var image = new MagickImage(couleur, 900, 900);
        image.Write(chemin);
        return chemin;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private static readonly PaperOption Quarante60 = new("40x60", "40×60", 400, 600);

    private static PlanMontage Plan =>
        MontageFeuille.Pour(Quarante60, 240, 300)
        ?? throw new InvalidOperationException("deux 24×30 doivent tenir sur un 40×60");

    private static (int Largeur, int Hauteur) Empreinte =>
        MontageFeuille.EmpreintePixels(Plan, 240, 300, Dpi);

    private static RenderRequest Tirage(string source, bool debout) =>
        new(source, debout ? TirageW : TirageH, debout ? TirageH : TirageW,
            CropSpec.Full, 0, 0, FitMode.Fill, 0, new ImageAdjustments());

    private string Rendre(params ImagePipeline.SheetCell[] cellules)
    {
        var sortie = Path.Combine(_root, $"montage-{Guid.NewGuid():N}.png");
        ImagePipeline.RenderCustomSheetToFile(
            cellules, SheetSpec.DefaultGapMm, cutMarks: true,
            SheetW, SheetH, sortie, Dpi, cutBorder: true, footprint: Empreinte);
        return sortie;
    }

    private static SheetLayoutResult Disposition(int cases) =>
        IdSheetLayout.Layout(SheetW, SheetH, Empreinte.Largeur, Empreinte.Hauteur,
            MmPx.ToPixels(SheetSpec.DefaultGapMm, Dpi), cases, MmPx.ToPixels(3, Dpi));

    private static (byte R, byte G, byte B) CouleurAuCentre(IPixelCollection<byte> pixels, PixelRect zone)
    {
        var pixel = pixels.GetPixel(zone.X + zone.Width / 2, zone.Y + zone.Height / 2);
        return ((byte)pixel.GetChannel(0), (byte)pixel.GetChannel(1), (byte)pixel.GetChannel(2));
    }

    /// <summary>
    /// L'empreinte est debout comme le tirage : c'est la FEUILLE qui se couche pour les
    /// accueillir. Un portrait garde donc son cadrage sans la moindre rotation.
    /// </summary>
    [Fact]
    public void La_feuille_se_couche_et_lempreinte_reste_debout()
    {
        Assert.True(Plan.FeuilleTournee);
        Assert.False(Plan.CelluleTournee);
        Assert.Equal(TirageW, Empreinte.Largeur);
        Assert.Equal(TirageH, Empreinte.Hauteur);
    }

    /// <summary>
    /// ⚠ L'essai central : un portrait et un paysage se montent sur la MÊME feuille, chacun
    /// à son sens. C'est ce que la rotation à la pose rend possible — sans elle, il faudrait
    /// refuser les sélections mêlées, ou recadrer l'une des deux.
    /// </summary>
    [Fact]
    public void Un_portrait_et_un_paysage_tiennent_sur_la_meme_feuille()
    {
        var sortie = Rendre(
            new ImagePipeline.SheetCell(Tirage(_rouge, debout: true), 1),
            new ImagePipeline.SheetCell(Tirage(_bleu, debout: false), 1));

        var layout = Disposition(2);
        using var planche = new MagickImage(sortie);
        using var pixels = planche.GetPixels();

        var premiere = CouleurAuCentre(pixels, layout.Cells[0]);
        var seconde = CouleurAuCentre(pixels, layout.Cells[1]);

        Assert.True(premiere.R > 200 && premiere.B < 60);
        Assert.True(seconde.B > 200 && seconde.R < 60);
    }

    /// <summary>
    /// Le tirage PAYSAGE, tourné, couvre TOUTE son empreinte debout. Posé sans rotation, il
    /// déborderait en largeur et laisserait deux bandes de papier blanc en haut et en bas —
    /// le client verrait un tirage plus court que celui qu'il a payé.
    /// </summary>
    [Fact]
    public void Le_tirage_tourne_remplit_toute_son_empreinte()
    {
        var sortie = Rendre(new ImagePipeline.SheetCell(Tirage(_bleu, debout: false), 1));

        var zone = Disposition(1).Cells[0];
        using var planche = new MagickImage(sortie);
        using var pixels = planche.GetPixels();

        // ⚠ On exige du BLEU, pas « du bleu ou du blanc » : le papier nu a lui aussi son
        // canal bleu à 255, et une case laissée vide passerait un simple « > 200 ».
        // C'est précisément le bas de la case qui reste nu quand la rotation manque.
        foreach (var y in new[] { zone.Y + 3, zone.Y + zone.Height - 4 })
        {
            var pixel = pixels.GetPixel(zone.X + zone.Width / 2, y);
            Assert.True(pixel.GetChannel(2) > 200 && pixel.GetChannel(0) < 60,
                $"la case n'est pas remplie de bleu en y={y} : le tirage n'a pas été tourné");
        }
    }

    /// <summary>
    /// La feuille sort en 60 × 40, le sens retenu par le plan — et non en 40 × 60, le sens
    /// du catalogue. C'est ce que l'écran grand format devra tirer à 100 %.
    /// </summary>
    [Fact]
    public void La_feuille_sort_dans_le_sens_du_plan()
    {
        var sortie = Rendre(new ImagePipeline.SheetCell(Tirage(_rouge, debout: true), 1));

        using var planche = new MagickImage(sortie);
        Assert.Equal((uint)SheetW, planche.Width);
        Assert.Equal((uint)SheetH, planche.Height);
        Assert.True(planche.Width > planche.Height, "la feuille est composée couchée");
    }

    /// <summary>
    /// La densité voyage avec le fichier. Sans elle, l'écran grand format retombe sur
    /// 300 ppp par défaut — ce qui est juste ici par chance, mais un rendu qui ne porterait
    /// pas sa densité partirait trois fois trop grand sur une machine qui lit du 96.
    /// </summary>
    [Fact]
    public void La_feuille_porte_sa_densite()
    {
        var sortie = Rendre(new ImagePipeline.SheetCell(Tirage(_rouge, debout: true), 1));

        using var planche = new MagickImage(sortie);

        // le PNG enregistre la densité en pixels par CENTIMÈTRE : on la reconvertit avant
        // de la lire, sinon on compare 300 à 118
        var densite = planche.Density.ChangeUnits(DensityUnit.PixelsPerInch);
        Assert.Equal(Dpi, (int)Math.Round(densite.X));
    }

    /// <summary>
    /// ⚠ Non-régression : une case qui n'est ni à l'empreinte ni sa transposée reste
    /// refusée. C'est la garde qui empêche une case de recouvrir sa voisine.
    /// </summary>
    [Fact]
    public void Une_case_dune_autre_taille_reste_refusee()
    {
        var bancale = new RenderRequest(_bleu, TirageW + 40, TirageH, CropSpec.Full, 0, 0,
            FitMode.Fill, 0, new ImageAdjustments());

        Assert.Throws<ArgumentException>(() => Rendre(
            new ImagePipeline.SheetCell(Tirage(_rouge, debout: true), 1),
            new ImagePipeline.SheetCell(bancale, 1)));
    }
}
