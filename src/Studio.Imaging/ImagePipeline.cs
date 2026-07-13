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
        int sheetWidthPx, int sheetHeightPx, string outputPath, int dpi = 300)
    {
        var gapPx = MmPx.ToPixels(gapMm, dpi);
        var layout = IdSheetLayout.Layout(
            sheetWidthPx, sheetHeightPx,
            cellRequest.TargetWidthPx, cellRequest.TargetHeightPx,
            gapPx, copies,
            tickLength: cutMarks ? MmPx.ToPixels(3, dpi) : 0);

        using var cell = Render(cellRequest);
        using var sheet = new MagickImage(MagickColors.White, (uint)sheetWidthPx, (uint)sheetHeightPx);

        foreach (var rect in layout.Cells)
            sheet.Composite(cell, rect.X, rect.Y, CompositeOperator.Over);

        if (layout.CutTicks.Count > 0)
        {
            var drawables = new Drawables().StrokeColor(new MagickColor("#9E9E9E")).StrokeWidth(1);
            foreach (var tick in layout.CutTicks)
                drawables.Line(tick.X1, tick.Y1, tick.X2, tick.Y2);
            sheet.Draw(drawables);
        }

        sheet.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        sheet.Write(outputPath);
    }

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

    private static void ApplyAdjustments(MagickImage image, ImageAdjustments adjustments)
    {
        if (adjustments.IsNeutral) return;

        if (adjustments.Grayscale)
            image.Grayscale(PixelIntensityMethod.Rec709Luminance);

        if (adjustments.Brightness != 0 || adjustments.Contrast != 0)
            image.BrightnessContrast(
                new Percentage(adjustments.Brightness),
                new Percentage(adjustments.Contrast));
    }
}
