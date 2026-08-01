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
    string? IccProfilePath = null);

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
        using var image = Render(request);
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        image.Write(outputPath);
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

        using var cell = Render(cellRequest);
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

        // une image créée à partir d'une couleur porte le pseudo-format « XC », que rien
        // ne sait écrire ; sans extension dans le chemin, l'écriture échouerait
        sheet.Format = MagickFormat.Png;
        sheet.Write(outputPath);
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

    /// <summary>
    /// Police de l'horodatage. Sans choix explicite, ImageMagick retombe sur une fonte
    /// interne minuscule, illisible sur un tirage. On prend la première police sans
    /// empattement installée — plus lisible en petit corps qu'une police à empattements.
    /// </summary>
    private static string StampFont()
    {
        if (_stampFont is not null) return _stampFont;

        var installees = MagickNET.FontFamilies.ToList();
        _stampFont = new[] { "Arial", "Segoe UI", "Tahoma", "Verdana", "Calibri" }
                         .FirstOrDefault(installees.Contains)
                     ?? installees.FirstOrDefault()
                     ?? "Arial";

        return _stampFont;
    }

    private static string? _stampFont;

    private static MagickImage Render(RenderRequest request)
    {
        MagickInit.Configure();

        var image = new MagickImage(request.SourcePath);
        try
        {
            RenderInto(image, request);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
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

    private static void RenderInto(MagickImage image, RenderRequest request)
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

        if (request.Fit == FitMode.Fill)
        {
            // remplit le format : redimensionne pour couvrir puis recoupe au centre l'excédent
            image.Resize(new MagickGeometry(targetW, targetH) { FillArea = true });
            image.Crop(targetW, targetH, Gravity.Center);
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
            image.BackgroundColor = MagickColors.White;
            image.Extent(targetW, targetH, Gravity.Center, MagickColors.White);
        }

        ApplyAdjustments(image, request.Adjustments);

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

    private static void ApplyAdjustments(MagickImage image, ImageAdjustments adjustments) =>
        ImageAdjuster.Apply(image, adjustments);
}
