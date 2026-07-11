using ImageMagick;
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
        MagickInit.Configure();

        using var image = new MagickImage(request.SourcePath);
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
            // pilote doit alors être désactivée dans le DEVMODE du produit)
            image.TransformColorSpace(ColorProfiles.SRGB, new ColorProfile(File.ReadAllBytes(request.IccProfilePath)));
        }

        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        image.Write(outputPath);
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
