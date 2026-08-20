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

    /// <param name="fullBleed">
    /// Faux par défaut : ces mesures portent sur la planche à marges — écart réglable et
    /// repères de coupe — qui reste ce que produit un produit dont <c>FullBleed</c> est
    /// désactivé. Le fond perdu a ses propres épreuves, plus bas.
    /// </param>
    private string Rendre(int copies = 8, bool cutBorder = true, SheetFooter? footer = null,
        double gapMm = SheetSpec.DefaultGapMm, int? cellHeightPx = null, bool fullBleed = false)
    {
        var sortie = Path.Combine(_root, $"planche-{Guid.NewGuid():N}.png");
        var hauteur = cellHeightPx ?? CellH;

        ImagePipeline.RenderIdSheetToFile(
            new RenderRequest(_source, CellW, hauteur, CropSpec.Full, 0, 0, FitMode.Fill, 0,
                new ImageAdjustments()),
            copies, gapMm, cutMarks: true,
            SheetW, SheetH, sortie, Dpi,
            cutBorder, footer, fullBleed);

        return sortie;
    }

    /// <summary>La bande d'avant : la date, et rien d'autre.</summary>
    private static SheetFooter DateSeule(DateTime moment) => new(moment);

    private static SheetLayoutResult Disposition(int copies = 8, double gapMm = SheetSpec.DefaultGapMm,
        int? cellHeightPx = null) =>
        IdSheetLayout.Layout(SheetW, SheetH, CellW, cellHeightPx ?? CellH,
            MmPx.ToPixels(gapMm, Dpi), copies, MmPx.ToPixels(3, Dpi));

    /// <summary>
    /// Bande réservée en bas quand la planche est horodatée.
    ///
    /// ⚠ <b>DEMANDÉE au même calcul que le rendu, et non recopiée.</b> Elle valait 7 mm en
    /// dur ici pendant que <c>ImagePipeline</c> en demandait 6,5 à
    /// <c>SheetFooterLayout.ReserveMinimalePx</c> — six pixels d'écart, donc un bloc de
    /// photos posé trois pixels plus bas dans la mesure que sur l'image.
    ///
    /// L'écart ne se voyait pas : <c>IdSheetLayout</c> poussait alors le bloc à la longueur
    /// des repères de coupe dès que le centre tombait plus haut, ce qui ramenait les deux
    /// réserves au MÊME résultat. Ce décalage a été retiré le 20/08/2026 — il collait le
    /// bloc au bord bas sur une planche juste, et le massicot de la machine y prenait un
    /// bout de la photo — et l'écart est aussitôt ressorti.
    ///
    /// Le commentaire disait déjà « doit rester en accord avec ImagePipeline » : on ne le
    /// lui demande plus, on le lui prend.
    /// </summary>
    private static int BandeHorodatage =>
        SheetFooterLayout.ReserveMinimalePx(DateSeule(DateTime.Now), Dpi);

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

    /// <summary>
    /// Le contour de découpe est noir et se trouve JUSTE À L'EXTÉRIEUR de la photo.
    ///
    /// Il était tracé à cheval sur le bord et mangeait une demi-épaisseur de photo : sur
    /// une case de 35 mm à 300 ppp il ne restait que 411 px au lieu de 413, soit 34,8 mm.
    /// Une photo d'identité sous-cotée se fait refuser au guichet (03/08/2026). Le premier
    /// pixel de la case doit donc être de la PHOTO, et le trait se trouver avant.
    /// </summary>
    [Fact]
    public void Un_contour_noir_entoure_chaque_photo_sans_la_mordre()
    {
        using var planche = new MagickImage(Rendre(cutBorder: true));
        using var pixels = planche.GetPixels();
        var cellule = Disposition().Cells[0];
        var y = cellule.Y + cellule.Height / 2;

        var justeAvant = Niveau(pixels, cellule.X - 1, y);
        var premierPixelDeLaPhoto = Niveau(pixels, cellule.X, y);
        var dansLaPhoto = Niveau(pixels, cellule.X + cellule.Width / 2, y);

        Assert.True(justeAvant < 60,
            $"le contour doit être noir juste avant la photo (obtenu {justeAvant})");
        Assert.True(premierPixelDeLaPhoto > 100,
            $"le premier pixel de la case doit être de la photo, pas du trait (obtenu {premierPixelDeLaPhoto})");
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
        using var planche = new MagickImage(Rendre(footer: DateSeule(moment)));
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
        using var planche = new MagickImage(Rendre(footer: DateSeule(new DateTime(2026, 7, 31, 19, 42, 0))));
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
        using var planche = new MagickImage(Rendre(footer: null));
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
        var sortie = Rendre(footer: DateSeule(new DateTime(2026, 7, 31, 19, 42, 0)), cellHeightPx: hautes);

        using var planche = new MagickImage(sortie);
        var disposition = DispositionRendue(cellHeightPx: hautes);
        var basPhotos = disposition.Cells.Max(c => c.Bottom);

        Assert.True(SheetH - basPhotos < MmPx.ToPixels(3.5, Dpi),
            "ce cas ne vaut que si la marge est effectivement trop courte");

        using var pixels = planche.GetPixels();
        Assert.True(CompterPixelsSombres(pixels, basPhotos, SheetH) < 50,
            "rien ne doit être écrit faute de place");
    }

    // ----- planche à fond perdu (04/08/2026) -----

    /// <summary>
    /// La disposition d'une planche à fond perdu : les cases ne sont plus séparées que par
    /// le trait de découpe, et rien n'est réservé dans les marges.
    /// </summary>
    /// <summary>
    /// La disposition telle que le RENDU la calcule.
    ///
    /// L'écart est celui du fond perdu, et non l'épaisseur du trait : les deux ont
    /// longtemps valu la même chose, mais l'écart est passé à un millimètre le 11/08/2026
    /// pour donner au massicot la marge qui lui manquait. Les confondre ici ferait chercher
    /// les cases là où elles ne sont plus.
    /// </summary>
    private static SheetLayoutResult DispositionFondPerdu(int copies = 8, int reservePx = 0) =>
        IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH,
            MmPx.ToPixels(SheetSpec.EcartFondPerduMm, Dpi), copies,
            tickLength: 0, bottomReserve: reservePx);

    /// <summary>
    /// À fond perdu, les photos ne sont séparées que de l'espace du massicot.
    ///
    /// <b>Elles étaient JOINTIVES</b> — l'écart valait l'épaisseur du trait, deux dixièmes
    /// de millimètre — et c'était trop juste : un coup de lame qui dévie entame la voisine.
    /// L'écart est passé à un millimètre le 11/08/2026, à la demande de l'exploitant, sans
    /// qu'aucune planche y perde de photo.
    ///
    /// Ce qui compte n'a pas changé : l'écart reste petit devant une case, et sans commune
    /// mesure avec les deux millimètres d'avant le fond perdu, qui dispersaient les photos
    /// au milieu du blanc.
    /// </summary>
    [Fact]
    public void A_fond_perdu_les_photos_ne_sont_separees_que_du_trait_de_coupe()
    {
        var disposition = DispositionFondPerdu();

        var ecart = disposition.Cells[1].X - disposition.Cells[0].Right;

        Assert.Equal(MmPx.ToPixels(SheetSpec.EcartFondPerduMm, Dpi), ecart);

        // le trait de découpe tient dans cet écart, et de loin
        Assert.True(ecart >= ImagePipeline.TraitDeDecoupePx(Dpi),
            "le trait de découpe doit tenir dans l'écart");

        // …qui reste une fraction de la case : on coupe la planche d'un trait, on ne
        // disperse pas les photos
        Assert.True(ecart < CellW / 10, "l'écart doit rester discret devant la case");
    }

    /// <summary>
    /// Les cases gardent leurs 35 × 45 mm PLEINS malgré le resserrement : une photo
    /// d'identité sous-cotée se fait refuser au guichet, et c'est justement pour cela que
    /// le trait vit dans l'écart plutôt que sur la photo.
    /// </summary>
    [Fact]
    public void A_fond_perdu_les_cases_gardent_leurs_cotes()
    {
        var disposition = DispositionFondPerdu();

        Assert.All(disposition.Cells, c =>
        {
            Assert.Equal(MmPx.ToPixels(35, Dpi), c.Width);
            Assert.Equal(MmPx.ToPixels(45, Dpi), c.Height);
        });
    }

    /// <summary>
    /// Le contour noir reste : c'est le choix de la boutique (04/08/2026), et c'est sur lui
    /// qu'on coupe. Deux cases voisines n'en partagent qu'un seul, sur lequel passent les
    /// ciseaux, et la photo n'en garde rien.
    /// </summary>
    [Fact]
    public void A_fond_perdu_le_contour_noir_reste_hors_de_la_photo()
    {
        using var planche = new MagickImage(Rendre(fullBleed: true, cutBorder: true));
        using var pixels = planche.GetPixels();
        var cellule = DispositionFondPerdu().Cells[0];
        var y = cellule.Y + cellule.Height / 2;

        Assert.True(Niveau(pixels, cellule.X - 1, y) < 60, "le trait doit border la photo");
        Assert.True(Niveau(pixels, cellule.X, y) > 100, "le premier pixel de la case est de la photo");
    }

    /// <summary>
    /// Le fond perdu ne trace PLUS de repères dans les marges : ils n'ont plus de marge où
    /// vivre, et la bande basse prend leur place.
    /// </summary>
    [Fact]
    public void A_fond_perdu_les_reperes_de_marge_disparaissent()
    {
        Assert.Empty(DispositionFondPerdu().CutTicks);
    }

    /// <summary>
    /// Resserrer les cases ne doit RIEN coûter en nombre de photos : c'est le contraire, et
    /// c'est bien pourquoi la capacité doit se compter avec l'écart réel.
    /// </summary>
    [Fact]
    public void Le_fond_perdu_ne_fait_jamais_perdre_de_photos()
    {
        var aMarges = IdSheetLayout.MaxCopies(SheetW, SheetH, CellW, CellH,
            MmPx.ToPixels(SheetSpec.DefaultGapMm, Dpi));
        var aFondPerdu = IdSheetLayout.MaxCopies(SheetW, SheetH, CellW, CellH,
            MmPx.ToPixels(SheetSpec.CutLineMm, Dpi));

        Assert.True(aFondPerdu >= aMarges,
            $"le fond perdu doit porter au moins autant de photos ({aFondPerdu} contre {aMarges})");
    }

    /// <summary>
    /// La bande complète — mention et code QR — s'imprime bel et bien, et elle porte plus
    /// d'encre que la date seule. C'est la planche demandée le 04/08/2026.
    /// </summary>
    [Fact]
    public void La_bande_porte_la_mention_et_le_code_qr()
    {
        var moment = new DateTime(2026, 8, 4, 16, 30, 0);
        var complete = SheetFooter.Pour(moment, new MarqueSettings(QrTexte: "https://exemple.test"));

        // Chaque planche est mesurée sous SES photos, et non sous celles de l'autre : les
        // deux bandes ne réservent pas la même hauteur, donc les grilles ne tombent pas au
        // même endroit. Compter dans une zone commune ferait entrer des pixels de photo —
        // gris moyen, donc « sombres » au seuil retenu — et les deux comptes seraient
        // dominés par la photo au lieu de la bande.
        var encreAvec = EncreSousLesPhotos(complete);
        var encreSans = EncreSousLesPhotos(DateSeule(moment));

        // Le seuil a suivi le dessin de la bande, deux fois le 11/08/2026 :
        //
        // 1. ×2 tant que le contenu grandissait avec la bande. Il a fallu le plafonner —
        //    « PHOTOS CONFORMES » sortait en capitales d'un centimètre sur les planches à
        //    peu de rangées — et la mention comme le QR déposent depuis moins d'encre ;
        // 2. les deux planches réservent MAINTENANT la même hauteur (le minimum où une date
        //    s'écrit, et non la hauteur de ce qu'elles portent), donc leurs photos tombent
        //    au même endroit et leurs bandes ont la même taille. La comparaison est d'autant
        //    plus serrée qu'elle porte sur la seule différence qui reste : ce qui est écrit.
        //
        // Ce que ce test protège n'a pas bougé — mention et code QR s'impriment bel et bien,
        // et chargent la bande d'au moins moitié plus que la date seule.
        Assert.True(encreAvec > encreSans * 1.4,
            $"la bande complète doit porter bien plus que la date ({encreAvec} contre {encreSans})");
    }

    /// <summary>
    /// Encre déposée sous la dernière rangée d'une planche à fond perdu portant
    /// <paramref name="footer"/> — c'est-à-dire dans SA bande, la réserve étant celle que
    /// cette bande-là demande.
    /// </summary>
    private int EncreSousLesPhotos(SheetFooter footer)
    {
        using var planche = new MagickImage(Rendre(footer: footer, fullBleed: true));
        using var pixels = planche.GetPixels();

        var basPhotos = DispositionFondPerdu(
            reservePx: SheetFooterLayout.ReservePx(footer, Dpi)).Cells.Max(c => c.Bottom);

        return CompterPixelsSombres(pixels, basPhotos, SheetH);
    }

    /// <summary>
    /// Un logo introuvable ne doit PAS empêcher la planche de sortir : le fichier vit hors
    /// du dépôt et peut avoir été déplacé, alors que le client, lui, attend ses photos.
    /// </summary>
    [Fact]
    public void Un_logo_absent_n_empeche_pas_la_planche()
    {
        var footer = SheetFooter.Pour(
            new DateTime(2026, 8, 4, 16, 30, 0),
            new MarqueSettings(LogoPath: Path.Combine(_root, "logo-qui-n-existe-pas.png")));

        using var planche = new MagickImage(Rendre(footer: footer, fullBleed: true));

        Assert.Equal((uint)SheetW, planche.Width);
        Assert.Equal((uint)SheetH, planche.Height);
    }

    /// <summary>
    /// La bande ne mord JAMAIS sur une case : elle commence sous la dernière rangée. Une
    /// photo d'identité rognée est une photo refusée au guichet.
    /// </summary>
    [Fact]
    public void La_bande_ne_mord_pas_sur_les_photos()
    {
        var footer = SheetFooter.Pour(
            new DateTime(2026, 8, 4, 16, 30, 0),
            new MarqueSettings(QrTexte: "https://exemple.test"));

        var basPhotos = DispositionFondPerdu(
            reservePx: SheetFooterLayout.ReservePx(footer, Dpi)).Cells.Max(c => c.Bottom);
        var pose = SheetFooterLayout.Place(footer, SheetW, SheetH, basPhotos, Dpi);

        Assert.NotNull(pose);
        Assert.True(pose.Band.Y >= basPhotos);

        // La bande ne descend plus jusqu'au bord du fichier : elle s'arrête à la marge du
        // massicot. Ce qui passait dessous partait au rognage — la date sortait amputée sur
        // les planches serrées, et à six dixièmes de millimètre près sur la française
        // (11/08/2026). C'est le pendant de la protection des côtés, qui existait déjà.
        Assert.Equal(SheetH - MmPx.ToPixels(SheetFooterLayout.MargeBasseMm, Dpi), pose.Band.Bottom);
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
