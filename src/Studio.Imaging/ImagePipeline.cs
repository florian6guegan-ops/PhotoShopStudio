using ImageMagick;
using ImageMagick.Drawing;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.Imaging;

public sealed record RenderRequest(
    string SourcePath,
    int TargetWidthPx,
    int TargetHeightPx,
    CropSpec Crop,
    int RotationQuarterTurns,
    double FineRotationDegrees,
    FitMode Fit,
    int BorderPx,
    ImageAdjustments Adjustments,
    string? IccProfilePath = null,
    bool CutBorder = false);

/// <summary>
/// Pipeline de rendu : produit le bitmap final aux dimensions exactes du produit.
/// Tout se passe ici, une seule fois — le pilote reçoit ensuite l'image 1:1
/// sans aucune remise à l'échelle.
/// </summary>
public static class ImagePipeline
{
    /// <summary>Rend la photo aux dimensions finales et l'écrit (PNG) dans renders/.</summary>
    public static void RenderToFile(RenderRequest request, string outputPath, int dpi = 300)
    {
        using var image = Render(request, dpi);
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        MagickInit.Write(image, outputPath);
    }

    /// <summary>
    /// Planche identité : rend la cellule (35×45 …) une seule fois puis la duplique
    /// selon la disposition IdSheetLayout, traits de coupe dans les marges.
    /// Le RenderRequest décrit la cellule (TargetWidth/HeightPx = dimensions de la cellule).
    /// </summary>
    /// <param name="fullBleed">
    /// Planche « à fond perdu » : les photos sont JOINTIVES — l'écart se réduit à
    /// l'épaisseur du trait de découpe, qui y tient tout entier — et les repères de coupe
    /// des marges disparaissent, la bande basse prenant leur place.
    ///
    /// C'est la planche que la boutique sort désormais (demandée le 04/08/2026, sur modèle
    /// d'un tirage de borne). L'écart de 2 mm et les repères en marge dispersaient les
    /// photos au milieu du blanc ; jointives, elles se coupent d'un trait de massicot d'un
    /// bord à l'autre. Le contour noir de chaque case, lui, reste : c'est sur lui qu'on coupe.
    /// </param>
    /// <param name="footer">Ce que porte la bande basse. Null = pas de bande.</param>
    public static void RenderIdSheetToFile(
        RenderRequest cellRequest, int copies, double gapMm, bool cutMarks,
        int sheetWidthPx, int sheetHeightPx, string outputPath, int dpi = 300,
        bool cutBorder = true, SheetFooter? footer = null, bool fullBleed = true)
    {
        // À fond perdu, l'écart n'est plus réglable : il vaut EXACTEMENT l'épaisseur du
        // trait de découpe, qui doit y tenir sans mordre sur les photos (voir
        // DrawCutBorders). Les repères de marge tombent avec lui — ils n'ont plus de marge
        // où vivre.
        var gapPx = fullBleed ? TraitDeDecoupePx(dpi) : MmPx.ToPixels(gapMm, dpi);
        var tickPx = !fullBleed && cutMarks ? MmPx.ToPixels(3, dpi) : 0;

        var layout = IdSheetLayout.Layout(
            sheetWidthPx, sheetHeightPx,
            cellRequest.TargetWidthPx, cellRequest.TargetHeightPx,
            gapPx, copies, tickPx);

        // la bande demande de la place : on refait la disposition en réservant le bas,
        // sans quoi elle devrait tenir dans la marge résiduelle et sortirait illisible
        if (footer is not null)
            layout = IdSheetLayout.Layout(
                sheetWidthPx, sheetHeightPx,
                cellRequest.TargetWidthPx, cellRequest.TargetHeightPx,
                gapPx, copies, tickPx,
                bottomReserve: SheetFooterLayout.ReservePx(footer, dpi));

        using var cell = Render(cellRequest, dpi);
        using var sheet = new MagickImage(MagickColors.White, (uint)sheetWidthPx, (uint)sheetHeightPx);

        // la densité AVANT de dessiner : une taille de police est exprimée en points, et
        // ImageMagick les convertit en pixels d'après la résolution de l'image. Posée
        // après, elle laisserait le texte calculé à 72 dpi, donc quatre fois trop petit.
        sheet.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);

        foreach (var rect in layout.Cells)
            sheet.Composite(cell, rect.X, rect.Y, CompositeOperator.Over);

        if (layout.CutTicks.Count > 0)
        {
            var drawables = new Drawables().StrokeColor(new MagickColor("#9E9E9E")).StrokeWidth(1);
            foreach (var tick in layout.CutTicks)
                drawables.Line(tick.X1, tick.Y1, tick.X2, tick.Y2);
            sheet.Draw(drawables);
        }

        if (cutBorder)
            DrawCutBorders(sheet, layout, dpi);

        if (footer is not null)
            SheetFooterPainter.Draw(sheet, footer, layout.Cells.Max(c => c.Bottom), dpi);

        MagickInit.Write(sheet, outputPath);
    }

    /// <summary>Une photo et le nombre de cases qu'elle occupe sur la planche.</summary>
    public sealed record SheetCell(RenderRequest Request, int Copies);

    /// <summary>
    /// Planche « personnalisée » : des photos DIFFÉRENTES, toutes à la même taille, casées
    /// côte à côte sur un papier du catalogue.
    ///
    /// C'est la planche identité avec une seule différence, mais qui change tout : les cases
    /// ne portent pas la même image. La disposition reste celle d'<see cref="IdSheetLayout"/>
    /// — grille uniforme centrée, repères de coupe dans les marges — parce que c'est elle qui
    /// a servi à COMPTER les places dans <c>CustomSheetLayout</c>, et que les deux doivent
    /// tomber sur le même nombre.
    ///
    /// Chaque photo n'est rendue QU'UNE FOIS, quel qu'en soit le nombre d'exemplaires : cinq
    /// tirages d'une même image, c'est un rendu et cinq recopies.
    /// </summary>
    /// <param name="cells">Les photos de cette planche, avec leur nombre de cases.</param>
    public static void RenderCustomSheetToFile(
        IReadOnlyList<SheetCell> cells, double gapMm, bool cutMarks,
        int sheetWidthPx, int sheetHeightPx, string outputPath, int dpi = 300,
        bool cutBorder = true)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
            throw new ArgumentException("Une planche sans photo n'a rien à imprimer.", nameof(cells));

        var cellW = cells[0].Request.TargetWidthPx;
        var cellH = cells[0].Request.TargetHeightPx;

        // toutes les cases d'une grille uniforme font la même taille : une case qui
        // dépasserait recouvrirait sa voisine sans que rien ne le signale
        if (cells.Any(c => c.Request.TargetWidthPx != cellW || c.Request.TargetHeightPx != cellH))
            throw new ArgumentException(
                "Toutes les photos d'une planche personnalisée doivent être à la même taille.",
                nameof(cells));

        var total = cells.Sum(c => c.Copies);
        if (total < 1)
            throw new ArgumentException("Aucune case à poser sur la planche.", nameof(cells));

        var gapPx = MmPx.ToPixels(gapMm, dpi);
        var tickPx = cutMarks ? MmPx.ToPixels(3, dpi) : 0;

        var layout = IdSheetLayout.Layout(
            sheetWidthPx, sheetHeightPx, cellW, cellH, gapPx, total, tickPx);

        using var sheet = new MagickImage(MagickColors.White, (uint)sheetWidthPx, (uint)sheetHeightPx);
        sheet.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);

        var rendues = new List<MagickImage>(cells.Count);
        try
        {
            var place = 0;
            foreach (var cellule in cells)
            {
                var image = Render(cellule.Request, dpi);
                rendues.Add(image);

                for (var i = 0; i < cellule.Copies; i++, place++)
                {
                    var rect = layout.Cells[place];
                    sheet.Composite(image, rect.X, rect.Y, CompositeOperator.Over);
                }
            }
        }
        finally
        {
            foreach (var image in rendues) image.Dispose();
        }

        if (layout.CutTicks.Count > 0)
        {
            var drawables = new Drawables().StrokeColor(new MagickColor("#9E9E9E")).StrokeWidth(1);
            foreach (var tick in layout.CutTicks)
                drawables.Line(tick.X1, tick.Y1, tick.X2, tick.Y2);
            sheet.Draw(drawables);
        }

        if (cutBorder)
            DrawCutBorders(sheet, layout, dpi);

        MagickInit.Write(sheet, outputPath);
    }

    /// <summary>
    /// Contour noir AUTOUR de chaque photo, posé juste à l'extérieur de sa case.
    ///
    /// Le trait était tracé à cheval sur le bord, et il mangeait donc la moitié de son
    /// épaisseur sur la photo : sur une case de 35 mm à 300 ppp, il restait 411 px de
    /// photo au lieu de 413, soit 34,8 mm. Assez pour qu'une photo d'identité sorte « un
    /// chouille trop petite » (signalé le 03/08/2026) — et une photo sous-cotée se fait
    /// refuser au guichet.
    ///
    /// Il est maintenant posé entièrement dans l'écart qui sépare deux cases, lequel vaut
    /// exactement une épaisseur de trait : la photo garde ses 35 × 45 mm pleins, et deux
    /// photos voisines restent séparées par un seul trait, sur lequel passent les ciseaux.
    /// </summary>
    private static void DrawCutBorders(MagickImage sheet, SheetLayoutResult layout, int dpi)
    {
        var epaisseur = TraitDeDecoupePx(dpi);

        // Le trait est centré sur le chemin. Le décaler d'une demi-épaisseur ne suffit
        // pas : le chemin tombe alors sur le bord même du premier pixel de photo, que le
        // trait recouvre encore à moitié. Le demi-pixel supplémentaire place le chemin
        // ENTRE deux pixels, si bien que le trait s'arrête pile avant la photo.
        var decalage = epaisseur / 2.0 + 0.5;

        // Trait NON lissé. Le lissage étalait le noir sur le pixel voisin : le premier
        // pixel de la photo tombait à 189 quand le fond alentour était à 202, treize
        // niveaux plus sombre. Sur le papier, cette hairline tranche avec le fond uni tout
        // le long du cadre — c'est le « liseré qui dénote du fond » signalé le 03/08/2026.
        // Un trait de découpe n'a rien à gagner à être lissé : il est droit et il est fin.
        var drawables = new Drawables()
            .DisableStrokeAntialias()
            .StrokeColor(MagickColors.Black)
            .StrokeWidth(epaisseur)
            .FillColor(MagickColors.Transparent);

        foreach (var cell in layout.Cells)
            drawables.Rectangle(
                cell.X - decalage, cell.Y - decalage,
                cell.Right - 1 + decalage, cell.Bottom - 1 + decalage);

        sheet.Draw(drawables);
    }

    /// <summary>
    /// Épaisseur du trait de découpe, en pixels. Sert aussi d'écart entre les cases :
    /// le trait tient tout entier dans cet écart, sans mordre sur les photos.
    /// </summary>
    public static int TraitDeDecoupePx(int dpi) => Math.Max(1, MmPx.ToPixels(TraitDeDecoupeMm, dpi));

    /// <summary>
    /// Épaisseur du trait de découpe, en millimètres. Voir <see cref="DrawCutBorders"/>.
    ///
    /// Elle vit dans le domaine parce que la CAPACITÉ d'une planche à fond perdu en dépend :
    /// c'est cette épaisseur qui sépare deux cases, et donc elle qui décide combien de
    /// photos tiennent sur le papier. Voir <see cref="SheetSpec.LayoutGapMm"/>.
    /// </summary>
    private const double TraitDeDecoupeMm = SheetSpec.CutLineMm;

    /// <summary>
    /// Contour noir sur le bord de la photo, quand du blanc l'entoure.
    ///
    /// Un tirage en « photo entière » sort avec des marges blanches pour s'adapter au papier :
    /// rien ne dit alors où passer les ciseaux. Même règle que <see cref="DrawCutBorders"/> —
    /// trait fin, à cheval sur le bord, pour que la coupe l'emporte.
    ///
    /// Les dimensions sont en pixels du tirage : l'épaisseur suit donc la résolution du produit,
    /// sans quoi le trait serait quatre fois trop gros à 1200 ppp.
    /// </summary>
    private static void DrawCutBorder(MagickImage image, PixelRect photo, int dpi)
    {
        var epaisseur = Math.Max(1, MmPx.ToPixels(TraitDeDecoupeMm, dpi));

        var drawables = new Drawables()
            .StrokeColor(MagickColors.Black)
            .StrokeWidth(epaisseur)
            .FillColor(MagickColors.Transparent)
            .Rectangle(photo.X, photo.Y, photo.Right - 1, photo.Bottom - 1);

        image.Draw(drawables);
    }

    /// <summary>
    /// Repères de coupe dans le blanc qui entoure un cadre : quatre paires de traits, dans
    /// le prolongement de ses bords.
    ///
    /// Ils sont posés HORS du cadre, à un millimètre : ils partent donc avec la chute,
    /// là où le contour, lui, reste sur le tirage. C'est ce qui permet de viser la coupe
    /// avant de poser les ciseaux, et non pendant.
    ///
    /// Seuls ceux qui tiennent sont tracés : sur un papier dont le cadre occupe toute la
    /// largeur — le cas d'un Polaroid sur du 10×15 — il ne reste de place qu'en haut et
    /// en bas, et les repères latéraux sont simplement omis.
    /// </summary>
    private static void DrawCornerTicks(MagickImage image, PixelRect cadre, int dpi)
    {
        var longueur = MmPx.ToPixels(3, dpi);
        var ecart = MmPx.ToPixels(1, dpi);

        var traits = new Drawables()
            .DisableStrokeAntialias()
            .StrokeColor(new MagickColor("#909090"))
            .StrokeWidth(Math.Max(1, MmPx.ToPixels(TraitDeDecoupeMm, dpi)))
            .FillColor(MagickColors.Transparent);

        var quelqueChose = false;

        void Trait(double x1, double y1, double x2, double y2)
        {
            if (Math.Min(x1, x2) < 0 || Math.Min(y1, y2) < 0) return;
            if (Math.Max(x1, x2) >= image.Width || Math.Max(y1, y2) >= image.Height) return;

            traits.Line(x1, y1, x2, y2);
            quelqueChose = true;
        }

        double[] verticales = [cadre.X, cadre.Right - 1];
        foreach (var x in verticales)
        {
            Trait(x, cadre.Y - ecart - longueur, x, cadre.Y - ecart);
            Trait(x, cadre.Bottom - 1 + ecart, x, cadre.Bottom - 1 + ecart + longueur);
        }

        double[] horizontales = [cadre.Y, cadre.Bottom - 1];
        foreach (var y in horizontales)
        {
            Trait(cadre.X - ecart - longueur, y, cadre.X - ecart, y);
            Trait(cadre.Right - 1 + ecart, y, cadre.Right - 1 + ecart + longueur, y);
        }

        if (quelqueChose) image.Draw(traits);
    }

    /// <summary>
    /// Fait décoder le JPEG à la taille dont le tirage a besoin, et pas à celle du fichier.
    ///
    /// <b>C'est la plus grosse économie du rendu, et elle ne coûte rien en qualité.</b> Le
    /// décodeur JPEG sait sauter des coefficients et rendre l'image au demi, au quart ou au
    /// huitième — c'est du sous-échantillonnage exact, pas une réduction après coup.
    ///
    /// Mesuré le 05/08/2026 sur la planche d'identité de la commande 05-026 (photo de
    /// 6016 × 4000 pour une cellule de 413 × 531) :
    ///
    /// | Étape | Sans | Avec |
    /// | --- | --- | --- |
    /// | décodage | 320 ms | 155 ms |
    /// | réduction avant redressement | 920 ms | ~230 ms |
    ///
    /// La réduction qui suit n'a plus alors que six mégapixels à rééchantillonner au lieu
    /// de vingt-quatre.
    ///
    /// <b>On demande un CARRÉ, du plus grand des deux côtés.</b> L'indication porte sur le
    /// fichier, dont l'orientation n'est connue qu'après lecture de l'EXIF : demander
    /// 1194 × 1796 sur un fichier couché ferait décoder trop petit, et le tirage y perdrait
    /// vraiment. Un carré est juste dans les deux sens ; il coûte au pire un cran de
    /// décodage.
    ///
    /// Le facteur deux est celui de <see cref="ReduireAvantRedressement"/>, et pour la même
    /// raison : le rééchantillonnage final doit avoir de la matière.
    ///
    /// N'agit que sur les JPEG — les autres formats n'ont pas de décodage progressif — et
    /// jamais à la hausse : le décodeur ne sait pas agrandir, un besoin supérieur au fichier
    /// le laisse simplement le lire en entier.
    /// </summary>
    private static MagickReadSettings? LectureEconome(RenderRequest request)
    {
        if (request.TargetWidthPx <= 0 || request.TargetHeightPx <= 0) return null;

        // la part que le recadrage retiendra : plus il est serré, plus il faut de source
        var partLargeur = request.Crop.IsFull ? 1.0 : Math.Clamp(request.Crop.Width, 0.01, 1.0);
        var partHauteur = request.Crop.IsFull ? 1.0 : Math.Clamp(request.Crop.Height, 0.01, 1.0);

        // <b>Le sur-échantillonnage ne se justifie QUE devant un redressement.</b> Sans lui,
        // la source va directement à sa taille finale par un seul rééchantillonnage, et
        // garder deux fois les pixels nécessaires ne sert alors à rien — c'est pourtant le
        // cas le plus fréquent de la boutique, le 10×15.
        //
        // La marge qui reste couvre le rognage au rapport : une source dont les
        // proportions diffèrent du tirage perd quelques pour cent sur un axe.
        var marge = Math.Abs(request.FineRotationDegrees) > 0.01 ? SurEchantillonnage : 1.3;

        var besoin = Math.Max(
            request.TargetWidthPx / partLargeur,
            request.TargetHeightPx / partHauteur) * marge;

        // au-delà de ce qu'un JPEG contient, l'indication ne sert plus à rien
        if (besoin >= 100_000) return null;

        return MagickInit.IndicationDeTaille(
            request.SourcePath, (int)Math.Max(1, Math.Ceiling(besoin)));
    }

    /// <param name="dpi">Résolution du produit : c'est elle qui donne l'épaisseur du trait de
    /// découpe, la densité du fichier n'étant posée qu'à l'écriture.</param>
    private static MagickImage Render(RenderRequest request, int dpi)
    {
        MagickInit.Configure();

        if (request.Fit == FitMode.Polaroid) return RenderPolaroid(request, dpi);

        var image = LectureEconome(request) is { } econome
            ? new MagickImage(request.SourcePath, econome)
            : new MagickImage(request.SourcePath);
        try
        {
            RenderInto(image, request, dpi);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Un tirage « Polaroid » : la photo remplit la fenêtre presque carrée du film 600, et
    /// le cadre blanc — bande large en bas — occupe le reste.
    ///
    /// Composé comme une planche, et non retouché sur place : c'est la même mécanique que
    /// <see cref="RenderIdSheetToFile"/>, une image blanche aux dimensions du tirage sur
    /// laquelle on pose la photo à sa place. Les décalages y sont explicites, là où un
    /// <c>Extent</c> décentré se joue à un signe près.
    ///
    /// Deux détails qui comptent :
    ///
    /// 1. <b>la photo est rendue en « remplir le format » à la taille de la FENÊTRE</b>,
    ///    donc recadrée presque carrée — c'est la forme du Polaroid ;
    /// 2. <b>le profil ICC est appliqué au tirage entier</b>, cadre blanc compris, et non à
    ///    la seule photo : tout ce qui part sur le papier doit suivre le même chemin couleur.
    /// </summary>
    private static MagickImage RenderPolaroid(RenderRequest request, int dpi)
    {
        var pose = PolaroidFrame.Place(request.TargetWidthPx, request.TargetHeightPx);

        // la fenêtre est un tirage ordinaire en « remplir le format ». Ni contour ni ICC
        // ici : ils appartiennent au tirage complet, qui les recevra plus bas.
        var demande = request with
        {
            TargetWidthPx = pose.Window.Width,
            TargetHeightPx = pose.Window.Height,
            Fit = FitMode.Fill,
            BorderPx = 0,
            CutBorder = false,
            IccProfilePath = null,
        };

        using var photo = Render(demande, dpi);
        AppliquerTeintePolaroid(photo);

        var tirage = new MagickImage(MagickColors.White,
            (uint)request.TargetWidthPx, (uint)request.TargetHeightPx);
        try
        {
            tirage.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
            tirage.Composite(photo, pose.Window.X, pose.Window.Y, CompositeOperator.Over);

            // Le contour suit le bord du POLAROID, pas celui de la photo : c'est le cadre
            // entier qu'on découpe, bande basse comprise.
            //
            // <b>Il est tracé D'OFFICE sur ce format</b>, et non plus sur demande. Le cadre
            // garde ses proportions et ne remplit donc pas la feuille — un 10×15 n'a pas le
            // rapport d'un Polaroid — et rien sur le papier ne disait où le sortir : le
            // tirage ne ressemblait pas à un Polaroid mais à une photo posée de travers au
            // milieu du blanc (constaté sur papier le 06/08/2026). Un Polaroid se découpe,
            // c'est sa forme qui fait le produit.
            DrawCutBorder(tirage, pose.Frame, dpi);
            DrawCornerTicks(tirage, pose.Frame, dpi);

            if (request.IccProfilePath is not null)
            {
                tirage.RenderingIntent = RenderingIntent.Perceptual;
                tirage.BlackPointCompensation = true;
                tirage.TransformColorSpace(ColorProfiles.SRGB, Profil(request.IccProfilePath));
            }

            return tirage;
        }
        catch
        {
            tirage.Dispose();
            throw;
        }
    }

    /// <summary>
    /// La teinte d'un Polaroid : contraste écrasé, noirs laiteux, blancs retenus, couleurs
    /// un peu éteintes et légèrement chaudes.
    ///
    /// Trois opérations, dans cet ordre, et rien de plus — un vignettage ou un grain
    /// coûteraient plusieurs secondes par tirage pour un effet que le cadre donne déjà.
    ///
    /// 1. <c>InverseLevel</c> resserre la dynamique : le noir monte à 7 %, le blanc
    ///    redescend à 95 %. C'est le voile du film instantané, et c'est ce qui se voit le
    ///    plus. (<c>Level</c> ferait l'inverse — il étirerait le contraste.)
    /// 2. <c>Modulate</c> baisse la saturation de 12 % : l'émulsion ne tient pas les
    ///    couleurs franches.
    /// 3. un soupçon de rouge en plus et de bleu en moins, pour la dominante chaude.
    /// </summary>
    private static void AppliquerTeintePolaroid(MagickImage photo)
    {
        photo.InverseLevel(new Percentage(7), new Percentage(95));
        photo.Modulate(new Percentage(100), new Percentage(88), new Percentage(100));
        photo.Evaluate(Channels.Red, EvaluateOperator.Multiply, 1.04);
        photo.Evaluate(Channels.Blue, EvaluateOperator.Multiply, 0.97);
    }

    /// <summary>
    /// Ramène la source dans l'espace de travail sRGB. Une photo d'appareil peut porter
    /// un profil AdobeRGB ou Display P3 : sans cette conversion, ses pixels sont lus comme
    /// du sRGB et les couleurs sortent fausses (rouges éteints, vert délavé). Sans profil
    /// embarqué, on suppose sRGB — la convention des JPEG grand public.
    /// </summary>
    private static void NormalizeToSrgb(MagickImage image)
    {
        if (image.GetColorProfile() is { } embedded)
            image.TransformColorSpace(embedded, ColorProfiles.SRGB);
    }

    private static void RenderInto(MagickImage image, RenderRequest request, int dpi)
    {
        NormalizeToSrgb(image);

        image.AutoOrient(); // applique l'orientation EXIF une bonne fois pour toutes

        var turns = ((request.RotationQuarterTurns % 4) + 4) % 4;
        if (turns != 0)
            image.Rotate(90 * turns);

        // redressement fin APRÈS les quarts de tour, et avant le recadrage : le cadre que
        // l'opérateur a posé à l'écran l'a été sur l'image déjà redressée. Le fond blanc
        // remplit les coins libérés par la rotation ; c'est au cadrage de les exclure,
        // et l'écran les montre pour qu'on puisse le faire en connaissance de cause.
        if (Math.Abs(request.FineRotationDegrees) > 0.01)
        {
            ReduireAvantRedressement(image, request);

            image.BackgroundColor = MagickColors.White;
            image.Rotate(request.FineRotationDegrees);
            image.ResetPage();
        }

        if (!request.Crop.IsFull)
        {
            var rect = CropMath.ToPixelRect(request.Crop, (int)image.Width, (int)image.Height);
            image.Crop(new MagickGeometry(rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height));
            image.ResetPage();
        }

        var targetW = (uint)request.TargetWidthPx;
        var targetH = (uint)request.TargetHeightPx;

        // Emplacement de la photo dans le tirage : c'est là que passeront les ciseaux.
        PixelRect? aDecouper = null;

        if (request.Fit == FitMode.Fill)
        {
            // Remplit le format : on DÉCOUPE D'ABORD la zone utile dans la source, on la
            // met à l'échelle ensuite.
            //
            // L'ordre inverse — redimensionner pour couvrir, puis recouper — était le plus
            // court à écrire et le plus cher à exécuter : pour un 40×50 tiré d'une photo de
            // 3024 × 2005, il fabriquait une image de 8906 × 5906 (52,6 Mpx) dont il jetait
            // ensuite 47 %. Mesuré le 02/08/2026 : 3 926 ms contre 2 015 ms en ne calculant
            // que les 27,9 Mpx utiles. Le résultat est le même — une découpe centrée suivie
            // d'une mise à l'échelle donne les mêmes pixels, au demi-pixel de bord près.
            RognerAuRapport(image, targetW, targetH);
            image.Resize(new MagickGeometry(targetW, targetH) { IgnoreAspectRatio = true });
            image.ResetPage();
            // garantit les dimensions exactes même après arrondis
            image.Extent(targetW, targetH, Gravity.Center, MagickColors.White);

            // Le bord de la photo EST le bord du tirage : c'est encore là que passent les
            // ciseaux quand plusieurs tirages sortent sur la même feuille. La case
            // « Contour de découpe » ne posait rien du tout dans ce mode — c'est le
            // troisième symptôme du même défaut, avec la case grisée et le trait qui ne
            // s'affichait qu'à l'écran.
            aDecouper = new PixelRect(0, 0, (int)targetW, (int)targetH);
        }
        else
        {
            // la fenêtre : le format moins le liseré, des deux côtés. Bornée à un pixel —
            // une marge absurde ne doit pas rendre la fenêtre nulle, ni la soustraction
            // repasser par zéro sur des entiers non signés.
            var marge = (uint)Math.Max(0, request.BorderPx);
            var availW = targetW > 2 * marge ? targetW - 2 * marge : 1u;
            var availH = targetH > 2 * marge ? targetH - 2 * marge : 1u;

            if (marge > 0)
            {
                // <b>BORD BLANC : la photo REMPLIT la fenêtre.</b> Le blanc qui l'entoure
                // fait alors la même largeur des quatre côtés — c'est un liseré, et c'est
                // ce que le client achète.
                //
                // Elle était seulement MISE À L'ÉCHELLE dans la fenêtre, en gardant ses
                // proportions : une photo 3:2 dans un 10×15 laissait donc deux bandes
                // blanches larges en haut et en bas, et deux filets sur les côtés. Le
                // tirage ne ressemblait pas à un bord blanc mais à une photo mal calée
                // (signalé le 06/08/2026). C'est aussi ce que fait DiLand sur les mêmes
                // produits : marges égales de 5 mm, et « recadrer par défaut » actif.
                RognerAuRapport(image, availW, availH);
                image.Resize(new MagickGeometry(availW, availH) { IgnoreAspectRatio = true });
            }
            else
            {
                // PHOTO ENTIÈRE : rien n'est coupé, et le blanc comble ce que le rapport
                // laisse. Les marges y sont inégales, et c'est tout l'objet du mode.
                image.Resize(new MagickGeometry(availW, availH)); // conserve les proportions
            }

            // relevé APRÈS le redimensionnement et AVANT le fond blanc : c'est la taille
            // réelle de la photo, celle que Extent va centrer
            var poseeW = (int)image.Width;
            var poseeH = (int)image.Height;

            image.BackgroundColor = MagickColors.White;
            image.Extent(targetW, targetH, Gravity.Center, MagickColors.White);

            // Gravity.Center centre à l'entier inférieur : on refait le même calcul
            aDecouper = new PixelRect(
                ((int)targetW - poseeW) / 2, ((int)targetH - poseeH) / 2, poseeW, poseeH);
        }

        ApplyAdjustments(image, request.Adjustments);

        // Le trait APRÈS les corrections — une correction de luminosité ne doit pas délaver le
        // repère de coupe — et AVANT la conversion ICC, pour que tout ce qui part sur le papier
        // suive le même chemin couleur.
        if (request.CutBorder && aDecouper is { } cadre) DrawCutBorder(image, cadre, dpi);

        if (request.IccProfilePath is not null)
        {
            // gestion couleur chez nous : sRGB → profil imprimante (la correction du
            // pilote doit alors être désactivée dans le DEVMODE du produit, sinon elle
            // s'applique une seconde fois par-dessus la nôtre)
            image.RenderingIntent = RenderingIntent.Perceptual;  // photos : dégradés et peaux préservés
            image.BlackPointCompensation = true;                 // évite les noirs bouchés en dye-sub
            image.TransformColorSpace(ColorProfiles.SRGB, Profil(request.IccProfilePath));
        }
    }

    /// <summary>
    /// Le profil ICC d'un fichier, lu UNE fois pour toute la séance.
    ///
    /// Un profil d'imprimante pèse quelques centaines de kilo-octets, et il était relu à
    /// chaque tirage : une enveloppe de quarante photos ouvrait donc quarante fois le même
    /// fichier, depuis quatre fils à la fois, au beau milieu du rendu. Ils ne changent
    /// jamais en cours d'exploitation — le catalogue les dépose une fois pour toutes.
    ///
    /// <see cref="ColorProfile"/> ne porte que les octets du profil et ne s'altère pas à
    /// l'usage : le même objet se partage entre fils sans risque.
    /// </summary>
    private static ColorProfile Profil(string chemin) =>
        Profils.GetOrAdd(chemin, c => new ColorProfile(File.ReadAllBytes(c)));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ColorProfile>
        Profils = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dimensions de l'image une fois orientée (EXIF + rotation utilisateur), sans
    /// décoder les pixels (ping des seuls en-têtes).
    /// </summary>
    public static (int Width, int Height) GetOrientedSize(string sourcePath, int rotationQuarterTurns)
    {
        MagickInit.Configure();

        using var image = new MagickImage();
        image.Ping(sourcePath);
        var w = (int)image.Width;
        var h = (int)image.Height;
        if (image.Orientation is OrientationType.LeftTop or OrientationType.RightTop
            or OrientationType.RightBottom or OrientationType.LeftBottom)
            (w, h) = (h, w);
        if (rotationQuarterTurns % 2 != 0)
            (w, h) = (h, w);
        return (w, h);
    }

    /// <summary>
    /// Nombre de pixels gardés par pixel de tirage avant le redressement.
    ///
    /// Deux fois la définition finale sur chaque axe, soit quatre fois les pixels : de quoi
    /// que le rééchantillonnage final ait de la matière, et bien au-delà de ce qu'un
    /// tirage à 300 ppp peut restituer. En dessous de 2, le redressement travaillerait
    /// sur une image à peine plus grande que le tirage et ses interpolations
    /// successives se verraient sur les contours.
    /// </summary>
    private const double SurEchantillonnage = 2.0;

    /// <summary>
    /// Réduit la source à ce que le redressement a besoin de faire tourner, et pas plus.
    ///
    /// <b>C'est LE coût du rendu quand l'opérateur redresse.</b> Mesuré le 05/08/2026 sur
    /// une planche d'identité : 11 937 ms sur 13 044, soit 92 % du temps, passés à faire
    /// tourner 24 Mpx de reflex pour n'en garder ensuite que 0,2 — la cellule 35 × 45 fait
    /// 413 × 531 px. Ce Magick.NET est bâti sans OpenMP : monter les threads ne change
    /// rien (11 824 ms sur 8 cœurs contre 12 325 sur un seul), seul le nombre de pixels
    /// compte.
    ///
    /// <b>Pourquoi réduire AVANT est géométriquement exact.</b> Le cadrage est stocké en
    /// coordonnées RELATIVES (voir <see cref="CropSpec"/>) : il tombe au même endroit
    /// quelle que soit la définition. Et une rotation suivie d'une homothétie donne le même
    /// résultat que l'homothétie suivie de la rotation — l'ordre est indifférent pour une
    /// mise à l'échelle uniforme. On ne déplace donc rien, on calcule seulement sur moins
    /// de pixels.
    ///
    /// <b>Ce qu'on ne fait jamais</b> : agrandir. Une source déjà plus petite que le besoin
    /// est laissée telle quelle — inventer des pixels pour les faire tourner coûterait le
    /// prix fort pour rien.
    /// </summary>
    private static void ReduireAvantRedressement(MagickImage image, RenderRequest request)
    {
        if (image.Width == 0 || image.Height == 0) return;
        if (request.TargetWidthPx <= 0 || request.TargetHeightPx <= 0) return;

        // Part de l'image que le cadrage retiendra. Un cadrage plein en garde tout ; un
        // cadrage serré demande une source d'autant plus grande pour rendre la même
        // définition finale.
        var partLargeur = request.Crop.IsFull ? 1.0 : Math.Clamp(request.Crop.Width, 0.01, 1.0);
        var partHauteur = request.Crop.IsFull ? 1.0 : Math.Clamp(request.Crop.Height, 0.01, 1.0);

        var voulueLargeur = request.TargetWidthPx / partLargeur * SurEchantillonnage;
        var voulueHauteur = request.TargetHeightPx / partHauteur * SurEchantillonnage;

        // le plus contraignant des deux axes commande : réduire davantage perdrait de la
        // définition sur l'autre
        var facteur = Math.Max(voulueLargeur / image.Width, voulueHauteur / image.Height);
        if (facteur >= 1) return;   // déjà assez petite : rien à gagner

        image.Resize(new MagickGeometry(
            (uint)Math.Max(1, Math.Round(image.Width * facteur)),
            (uint)Math.Max(1, Math.Round(image.Height * facteur)))
        { IgnoreAspectRatio = true });

        image.ResetPage();
    }

    /// <summary>
    /// Découpe au centre la plus grande zone de <paramref name="image"/> qui ait le rapport
    /// largeur/hauteur visé. L'image n'est pas mise à l'échelle : elle l'est ensuite, et sur
    /// la seule zone conservée.
    /// </summary>
    private static void RognerAuRapport(MagickImage image, uint targetW, uint targetH)
    {
        if (targetW == 0 || targetH == 0 || image.Width == 0 || image.Height == 0) return;

        var vise = (double)targetW / targetH;
        var actuel = (double)image.Width / image.Height;

        // déjà au bon rapport, à un cheveu près : rogner ne ferait que perdre une ligne
        if (Math.Abs(actuel - vise) < 1e-9) return;

        uint largeur, hauteur;
        if (actuel > vise)
        {
            // trop large : on garde toute la hauteur
            hauteur = image.Height;
            largeur = Math.Max(1, (uint)Math.Round(image.Height * vise));
        }
        else
        {
            largeur = image.Width;
            hauteur = Math.Max(1, (uint)Math.Round(image.Width / vise));
        }

        // l'arrondi peut dépasser d'un pixel sur une image presque au bon rapport
        largeur = Math.Min(largeur, image.Width);
        hauteur = Math.Min(hauteur, image.Height);

        image.Crop(new MagickGeometry(
            (int)((image.Width - largeur) / 2), (int)((image.Height - hauteur) / 2),
            largeur, hauteur));
        image.ResetPage();
    }

    private static void ApplyAdjustments(MagickImage image, ImageAdjustments adjustments) =>
        ImageAdjuster.Apply(image, adjustments);
}

