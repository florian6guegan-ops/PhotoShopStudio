using System.Globalization;
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
    public static void RenderIdSheetToFile(
        RenderRequest cellRequest, int copies, double gapMm, bool cutMarks,
        int sheetWidthPx, int sheetHeightPx, string outputPath, int dpi = 300,
        bool cutBorder = true, DateTime? stamp = null)
    {
        var gapPx = MmPx.ToPixels(gapMm, dpi);
        var tickPx = cutMarks ? MmPx.ToPixels(3, dpi) : 0;

        var layout = IdSheetLayout.Layout(
            sheetWidthPx, sheetHeightPx,
            cellRequest.TargetWidthPx, cellRequest.TargetHeightPx,
            gapPx, copies, tickPx);

        // la date demande de la place : on refait la disposition en réservant le bas,
        // sans quoi elle devrait tenir dans la marge résiduelle et sortirait illisible
        if (stamp is not null)
            layout = IdSheetLayout.Layout(
                sheetWidthPx, sheetHeightPx,
                cellRequest.TargetWidthPx, cellRequest.TargetHeightPx,
                gapPx, copies, tickPx,
                bottomReserve: StampBandPx(dpi));

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

        if (stamp is { } moment)
            DrawStamp(sheet, layout, moment, dpi);

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
    /// Contour noir autour de chaque photo, tracé à cheval sur son bord.
    ///
    /// Le trait est fin — deux dixièmes de millimètre — pour que le coup de ciseaux le
    /// fasse disparaître : un contour large laisserait un liseré noir sur la photo coupée.
    /// </summary>
    private static void DrawCutBorders(MagickImage sheet, SheetLayoutResult layout, int dpi)
    {
        var epaisseur = Math.Max(1, MmPx.ToPixels(0.2, dpi));

        var drawables = new Drawables()
            .StrokeColor(MagickColors.Black)
            .StrokeWidth(epaisseur)
            .FillColor(MagickColors.Transparent);

        foreach (var cell in layout.Cells)
            drawables.Rectangle(cell.X, cell.Y, cell.Right - 1, cell.Bottom - 1);

        sheet.Draw(drawables);
    }

    /// <summary>Épaisseur du trait de découpe, en millimètres. Voir <see cref="DrawCutBorders"/>.</summary>
    private const double TraitDeDecoupeMm = 0.2;

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
    /// Date et heure du tirage dans la marge basse de la planche — l'administration exige
    /// une photo récente, et c'est ce qui le prouve.
    ///
    /// Rien n'est écrit si la marge est trop courte : mordre sur les photos les rendrait
    /// non conformes, ce qui serait pire que l'absence de mention.
    /// </summary>
    /// <summary>
    /// Corps de la mention, en millimètres. DiLand écrit la sienne en 5 mm — relevé dans
    /// son code : <c>new Font("Arial", 5 * unMillimetreEnPixels, GraphicsUnit.Pixel)</c>.
    /// On s'aligne dessus : en dessous, la date est illisible sur le tirage.
    /// </summary>
    private const double StampHeightMm = 5;

    /// <summary>Hauteur à réserver en bas de la planche : la mention et son air autour.</summary>
    private static int StampBandPx(int dpi) => MmPx.ToPixels(StampHeightMm + 2, dpi);

    private static void DrawStamp(MagickImage sheet, SheetLayoutResult layout, DateTime moment, int dpi)
    {
        var basPhotos = layout.Cells.Max(c => c.Bottom);
        var marge = (int)sheet.Height - basPhotos;

        var hauteurTexte = MmPx.ToPixels(StampHeightMm, dpi);
        if (marge < hauteurTexte + MmPx.ToPixels(1, dpi)) return;

        // la taille est donnée en pixels : ImageMagick dessine à 72 points par pouce quelle
        // que soit la densité de l'image, donc un point vaut ici un pixel. La convertir
        // comme un vrai corps typographique la divisait par quatre — mention illisible.
        var drawables = new Drawables()
            .Font(StampFont())
            .FontPointSize(hauteurTexte)
            .FillColor(MagickColors.Black)
            .StrokeColor(MagickColors.Transparent)
            .TextAlignment(TextAlignment.Center)
            .Text(sheet.Width / 2.0, basPhotos + (marge + hauteurTexte) / 2.0,
                moment.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));

        sheet.Draw(drawables);
    }

    /// <summary>Police de l'horodatage — voir <see cref="Fonts.SansEmpattement"/>.</summary>
    private static string StampFont() => Fonts.SansEmpattement();

    /// <param name="dpi">Résolution du produit : c'est elle qui donne l'épaisseur du trait de
    /// découpe, la densité du fichier n'étant posée qu'à l'écriture.</param>
    private static MagickImage Render(RenderRequest request, int dpi)
    {
        MagickInit.Configure();

        if (request.Fit == FitMode.Polaroid) return RenderPolaroid(request, dpi);

        var image = new MagickImage(request.SourcePath);
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

            // le contour suit le bord du POLAROID, pas celui de la photo : c'est le cadre
            // entier qu'on découpe, bande basse comprise
            if (request.CutBorder) DrawCutBorder(tirage, pose.Frame, dpi);

            if (request.IccProfilePath is not null)
            {
                tirage.RenderingIntent = RenderingIntent.Perceptual;
                tirage.BlackPointCompensation = true;
                tirage.TransformColorSpace(ColorProfiles.SRGB,
                    new ColorProfile(File.ReadAllBytes(request.IccProfilePath)));
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

        // Emplacement de la photo dans le tirage, quand du blanc l'entoure : c'est là que
        // passeront les ciseaux. Nul en mode « remplir », où la photo occupe tout.
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
        }
        else
        {
            // image entière : tient dans le format moins les marges, fond blanc autour
            var availW = targetW - 2 * (uint)request.BorderPx;
            var availH = targetH - 2 * (uint)request.BorderPx;
            image.Resize(new MagickGeometry(availW, availH)); // conserve les proportions

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
            image.TransformColorSpace(ColorProfiles.SRGB, new ColorProfile(File.ReadAllBytes(request.IccProfilePath)));
        }
    }

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

