using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le rendu de la planche de RENTRÉE, sur les pixels de la feuille produite : quatre cases
/// d'identité d'un côté, le portrait de l'autre, la date en bas.
///
/// Les deux cadrages portent des GRIS DIFFÉRENTS — la photo d'identité est tirée d'une
/// bande claire, le portrait d'une bande sombre — ce qui permet de vérifier non seulement
/// que quelque chose est posé, mais que c'est bien le bon cadrage au bon endroit. Sans
/// cela, une planche qui répéterait la case d'identité dans la grande case passerait pour
/// juste.
/// </summary>
public class PlancheRentreeRenderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Rentree-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;

    private const int Dpi = 300;

    /// <summary>La planche 10×15 de la DNP, couchée : celle que la boutique vend.</summary>
    private static int SheetW => MmPx.ToPixels(156.1, Dpi);
    private static int SheetH => MmPx.ToPixels(105, Dpi);
    private static int CellW => MmPx.ToPixels(35, Dpi);
    private static int CellH => MmPx.ToPixels(45, Dpi);

    private const byte NiveauHaut = 200;
    private const byte NiveauBas = 60;

    public PlancheRentreeRenderTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "portrait.png");

        // Deux bandes horizontales : claire en haut, sombre en bas. Le cadrage décide donc
        // du gris qui sortira, et la planche se lit au pixel.
        using var photo = new MagickImage(MagickColor.FromRgb(NiveauHaut, NiveauHaut, NiveauHaut),
            1200, 1600);
        photo.Draw(new ImageMagick.Drawing.Drawables()
            .FillColor(MagickColor.FromRgb(NiveauBas, NiveauBas, NiveauBas))
            .Rectangle(0, 800, 1199, 1599));
        photo.Write(_source);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <param name="identites">Cases à la norme posées sur la planche.</param>
    /// <param name="footer">La bande basse ; null = pas de date.</param>
    private string Rendre(int identites = 4, SheetFooter? footer = null, bool fullBleed = true)
    {
        var sortie = Path.Combine(_root, $"rentree-{Guid.NewGuid():N}.png");

        // l'identité prend la MOITIÉ HAUTE (claire), le portrait la MOITIÉ BASSE (sombre)
        var identite = new RenderRequest(_source, CellW, CellH,
            new CropSpec(0, 0, 1, 0.5), 0, 0, FitMode.Fill, 0, new ImageAdjustments());

        var portrait = identite with { Crop = new CropSpec(0, 0.5, 1, 0.5) };

        ImagePipeline.RenderPlancheRentreeToFile(
            identite, portrait, identites,
            SheetSpec.DefaultGapMm, cutMarks: true,
            SheetW, SheetH, sortie, Dpi,
            cutBorder: true, footer, fullBleed);

        return sortie;
    }

    /// <summary>
    /// La disposition que le RENDU vient de poser — et donc les mêmes arguments que lui.
    ///
    /// ⚠ <c>airAuBord</c> en fait partie depuis le 21/08/2026 : les essais d'ici sondent des
    /// PIXELS à des coordonnées prises ici, et une disposition calculée autrement que celle
    /// qui a été dessinée les ferait regarder à côté — en silence, et avec des assertions
    /// qui passeraient ou tomberaient sans rapport avec ce qu'elles décrivent.
    /// </summary>
    private static PlancheRentreeResult Disposition(int identites = 4, int reserve = 0) =>
        PlancheRentree.Layout(SheetW, SheetH, CellW, CellH,
            MmPx.ToPixels(SheetSpec.EcartFondPerduMm, Dpi), identites,
            bottomReserve: reserve,
            largeurMinimaleGrandePx: MmPx.ToPixels(PlancheRentree.LargeurMinimaleGrandeMm, Dpi),
            airAuBord: MmPx.ToPixels(PlancheRentree.AirAuBordMm, Dpi))!;

    private static byte Niveau(IPixelCollection<byte> pixels, int x, int y) =>
        (byte)pixels.GetPixel(x, y).GetChannel(0);

    [Fact]
    public void La_planche_sort_aux_dimensions_du_tirage()
    {
        using var planche = new MagickImage(Rendre());

        Assert.Equal((uint)SheetW, planche.Width);
        Assert.Equal((uint)SheetH, planche.Height);
    }

    /// <summary>
    /// Le cœur du format : la grande case porte le cadrage LARGE, pas une identité agrandie.
    /// Les deux cadrages sortent de bandes de gris différentes, et c'est ce qui se mesure.
    /// </summary>
    [Fact]
    public void Le_portrait_porte_son_propre_cadrage()
    {
        var mise = Disposition();

        using var planche = new MagickImage(Rendre());
        using var pixels = planche.GetPixels();

        var caseId = mise.Identites[0];
        var grande = mise.Grande;

        Assert.InRange(Niveau(pixels, caseId.X + caseId.Width / 2, caseId.Y + caseId.Height / 2),
            NiveauHaut - 12, NiveauHaut + 12);

        Assert.InRange(Niveau(pixels, grande.X + grande.Width / 2, grande.Y + grande.Height / 2),
            NiveauBas - 12, NiveauBas + 12);
    }

    /// <summary>
    /// Les quatre cases sont bien là, toutes les quatre : une planche qui n'en poserait que
    /// trois se vendrait au même prix.
    /// </summary>
    [Fact]
    public void Les_quatre_identites_sont_posees()
    {
        var mise = Disposition();

        using var planche = new MagickImage(Rendre());
        using var pixels = planche.GetPixels();

        Assert.Equal(4, mise.Identites.Count);
        Assert.All(mise.Identites, c =>
            Assert.InRange(Niveau(pixels, c.X + c.Width / 2, c.Y + c.Height / 2),
                NiveauHaut - 12, NiveauHaut + 12));
    }

    /// <summary>
    /// La date : l'administration l'exige, et une planche de rentrée porte les mêmes photos
    /// d'identité qu'une autre. Elle s'écrit SOUS LE BLOC D'IDENTITÉS — le portrait, lui,
    /// descend jusqu'au bord de la feuille.
    /// </summary>
    [Fact]
    public void La_date_sinscrit_sous_les_photos()
    {
        var footer = SheetFooter.Pour(new DateTime(2026, 9, 1, 10, 30, 0), null);

        using var planche = new MagickImage(Rendre(footer: footer));
        using var pixels = planche.GetPixels();

        var reserve = SheetFooterLayout.ReserveMinimalePx(footer, Dpi);
        var mise = Disposition(reserve: reserve);

        // la bande est bien sous les cases, et de leur largeur
        Assert.True(mise.BandeBasse.Height > 0);
        Assert.True(mise.BandeBasse.Right <= mise.Grande.X);

        // du noir dans la bande basse, donc du texte
        var sombre = 0;
        for (var y = mise.BandeBasse.Y; y < mise.BandeBasse.Bottom; y++)
        for (var x = mise.BandeBasse.X; x < mise.BandeBasse.Right; x += 3)
            if (Niveau(pixels, x, y) < 100) sombre++;

        Assert.True(sombre > 0, "aucune date écrite sous les photos");
    }

    /// <summary>
    /// LA MENTION ET LE NOM DE LA BOUTIQUE, qui manquaient à cette planche.
    ///
    /// Signalé le 20/08/2026 : « il manque la mention photo conforme ainsi que le nom du
    /// magasin ». Ils n'étaient pas oubliés — la bande courait sous le portrait, donc sur
    /// sa hauteur minimale, et <c>SheetFooterLayout</c> n'y gardait que la date. Sous le
    /// seul bloc d'identités elle a la place, et les écrit sur leur propre ligne.
    ///
    /// On mesure l'ENCRE, ligne par ligne : deux lignes noircies séparément prouvent que la
    /// mention n'a pas simplement remplacé la date.
    /// </summary>
    [Fact]
    public void La_mention_et_le_magasin_sinscrivent_sous_le_bloc()
    {
        var marque = new MarqueSettings(
            Mention: "PHOTOS CONFORMES\naux normes des documents officiels",
            NomMagasin: "Photoconcept MA");

        var footer = SheetFooter.Pour(new DateTime(2026, 9, 1, 10, 30, 0), marque);

        Assert.False(footer.DateSeule);

        using var planche = new MagickImage(Rendre(footer: footer));
        using var pixels = planche.GetPixels();

        var mise = Disposition(reserve: SheetFooterLayout.ReserveMinimalePx(footer, Dpi));
        var bande = mise.BandeBasse;

        var lignes = new List<int>();
        for (var y = bande.Y; y < bande.Bottom; y++)
        {
            var encre = 0;
            for (var x = bande.X; x < bande.Right; x++)
                if (Niveau(pixels, x, y) < 140) encre++;
            lignes.Add(encre);
        }

        // deux paquets de lignes encrées séparés par du blanc : la mention, puis la date
        var paquets = 0;
        var dedans = false;
        foreach (var encre in lignes)
        {
            if (encre > 0 && !dedans) { paquets++; dedans = true; }
            else if (encre == 0) dedans = false;
        }

        Assert.True(paquets >= 2,
            $"la bande ne porte que {paquets} ligne(s) de texte : la mention n'est pas écrite");
    }

    /// <summary>
    /// Une planche qui ne peut pas porter son portrait doit le DIRE, et tout de suite : un
    /// rendu muet sortirait une feuille inutilisable, découverte au massicot.
    /// </summary>
    [Fact]
    public void Une_planche_sans_place_pour_le_portrait_est_refusee()
    {
        Assert.Throws<InvalidOperationException>(() => Rendre(identites: 8));
    }

    /// <summary>
    /// Le contour de découpe entoure AUSSI le portrait : c'est sur lui qu'on coupe, et la
    /// grande photo se découpe comme les autres.
    /// </summary>
    [Fact]
    public void Le_portrait_a_son_contour_de_decoupe()
    {
        var mise = Disposition();

        using var planche = new MagickImage(Rendre());
        using var pixels = planche.GetPixels();

        // juste à l'extérieur du bord gauche de la grande case, le trait doit être là
        var x = mise.Grande.X - 1;
        var y = mise.Grande.Y + mise.Grande.Height / 2;

        Assert.True(Niveau(pixels, x, y) < 100,
            $"pas de trait de découpe à gauche du portrait (niveau {Niveau(pixels, x, y)})");
    }

    /// <summary>
    /// LE DÉFAUT DU 20/08/2026 : un portrait REDRESSÉ sortait avec un coin blanc.
    ///
    /// Relevé sur la planche de 16:06 : six pixels de blanc au bord droit en haut, quatre
    /// à mi-hauteur, plus rien aux deux tiers — un BISEAU, et non une marge. C'est la
    /// signature d'une rotation : le rendu redresse avant de recadrer, la photo grandit, et
    /// ses coins deviennent blancs.
    ///
    /// Le cadre large est le seul du logiciel que l'opérateur ne pose pas lui-même : il est
    /// déduit du cadre d'identité par <see cref="CadrageElargi"/>, qui le bornait à l'image
    /// ENTIÈRE — coins compris. Personne ne pouvait donc le rattraper à l'écran.
    ///
    /// <b>Le cadre est posé en haut à DROITE exprès</b> : au centre, il ne rencontrerait
    /// jamais les coins et l'essai passerait sur le code fautif. Les deux signes d'angle
    /// sont éprouvés — se tromper de sens ramènerait le cadre vers le mauvais coin, et
    /// l'autre resterait blanc.
    /// </summary>
    [Theory]
    [InlineData(0.9)]
    [InlineData(-0.9)]
    [InlineData(3)]
    [InlineData(-3)]
    [InlineData(8)]
    public void Le_portrait_redresse_n_a_aucun_coin_blanc(double degres)
    {
        var mise = Disposition();

        var identite = new CropSpec(0.55, 0.04, 0.34, 0.28);

        var large = CadrageElargi.Depuis(
            identite, 1200, 1600,
            mise.Grande.Width * 25.4 / Dpi, mise.Grande.Height * 25.4 / Dpi,
            redressementDegres: degres);

        var sortie = Path.Combine(_root, $"redresse-{Guid.NewGuid():N}.png");
        var caseId = new RenderRequest(_source, CellW, CellH, identite, 0, degres,
            FitMode.Fill, 0, new ImageAdjustments());

        ImagePipeline.RenderPlancheRentreeToFile(
            caseId, caseId with { Crop = large }, 4,
            SheetSpec.DefaultGapMm, cutMarks: true,
            SheetW, SheetH, sortie, Dpi,
            cutBorder: true, footer: null, fullBleed: true);

        using var planche = new MagickImage(sortie);
        using var pixels = planche.GetPixels();

        // La photo d'essai est faite de deux gris (200 et 60) : tout pixel blanc dans la
        // grande case vient du remplissage de la rotation, et de rien d'autre. On rentre de
        // deux pixels pour ne pas compter le trait de découpe, qui borde la case.
        var blancs = 0;
        var premier = "";
        for (var y = mise.Grande.Y + 2; y < mise.Grande.Bottom - 2; y += 2)
        {
            for (var x = mise.Grande.X + 2; x < mise.Grande.Right - 2; x += 2)
            {
                if (Niveau(pixels, x, y) < 250) continue;
                if (blancs == 0) premier = $" (premier en {x},{y})";
                blancs++;
            }
        }

        Assert.True(blancs == 0,
            $"{blancs} points blancs dans le portrait redressé de {degres}°{premier}");
    }
}
