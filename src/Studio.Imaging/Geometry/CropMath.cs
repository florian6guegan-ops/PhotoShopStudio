using Studio.Core.Domain;

namespace Studio.Imaging.Geometry;

/// <summary>
/// Maths de recadrage, en fonctions pures. Les aspects sont exprimés largeur/hauteur.
/// La rotation utilisateur est appliquée en amont : ici on raisonne toujours
/// sur les dimensions de l'image déjà orientée.
/// </summary>
public static class CropMath
{
    /// <summary>
    /// Plus grand recadrage centré de l'image respectant l'aspect cible (mode « plein »).
    /// </summary>
    public static CropSpec CenterCrop(int imageWidth, int imageHeight, double targetAspect)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Dimensions d'image invalides");
        if (targetAspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetAspect));

        var imageAspect = (double)imageWidth / imageHeight;

        if (imageAspect > targetAspect)
        {
            // image trop large : on rogne les côtés
            var width = targetAspect / imageAspect;
            return new CropSpec((1 - width) / 2, 0, width, 1);
        }
        else
        {
            // image trop haute : on rogne haut et bas
            var height = imageAspect / targetAspect;
            return new CropSpec(0, (1 - height) / 2, 1, height);
        }
    }

    /// <summary>Convertit un recadrage normalisé en rectangle pixels sur l'image source.</summary>
    public static PixelRect ToPixelRect(CropSpec crop, int imageWidth, int imageHeight)
    {
        var x = (int)Math.Round(crop.X * imageWidth);
        var y = (int)Math.Round(crop.Y * imageHeight);
        var w = (int)Math.Round(crop.Width * imageWidth);
        var h = (int)Math.Round(crop.Height * imageHeight);

        // les arrondis ne doivent jamais sortir de l'image
        x = Math.Clamp(x, 0, imageWidth - 1);
        y = Math.Clamp(y, 0, imageHeight - 1);
        w = Math.Clamp(w, 1, imageWidth - x);
        h = Math.Clamp(h, 1, imageHeight - y);
        return new PixelRect(x, y, w, h);
    }

    /// <summary>
    /// Mode « entier » : rectangle de destination de l'image entière, centrée dans le
    /// canevas avec au moins <paramref name="borderPx"/> de marge sur chaque bord.
    /// </summary>
    public static PixelRect FitWithin(int canvasWidth, int canvasHeight, double imageAspect, int borderPx = 0)
    {
        if (imageAspect <= 0) throw new ArgumentOutOfRangeException(nameof(imageAspect));

        var availableW = canvasWidth - 2 * borderPx;
        var availableH = canvasHeight - 2 * borderPx;
        if (availableW <= 0 || availableH <= 0)
            throw new ArgumentOutOfRangeException(nameof(borderPx), "Marge trop grande pour le canevas");

        int w, h;
        if ((double)availableW / availableH > imageAspect)
        {
            h = availableH;
            w = (int)Math.Round(availableH * imageAspect);
        }
        else
        {
            w = availableW;
            h = (int)Math.Round(availableW / imageAspect);
        }

        return new PixelRect((canvasWidth - w) / 2, (canvasHeight - h) / 2, w, h);
    }

    /// <summary>Part de zoom minimale : un recadrage ne descend jamais sous 1/5 du recadrage maximal (zoom ×5).</summary>
    public const double MinZoomShare = 0.2;

    /// <summary>Déplace le recadrage (deltas normalisés) sans changer sa taille, borné à l'image.</summary>
    public static CropSpec Pan(CropSpec crop, double dxNorm, double dyNorm) =>
        ClampToBounds(crop with { X = crop.X + dxNorm, Y = crop.Y + dyNorm });

    /// <summary>
    /// Agrandit (facteur &gt; 1) ou resserre (facteur &lt; 1) le recadrage autour de son centre,
    /// en préservant l'aspect pixel et sans jamais dépasser le recadrage maximal de l'image.
    /// </summary>
    public static CropSpec Zoom(CropSpec crop, double factor, int imageWidth, int imageHeight, double targetAspect)
    {
        if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));

        var max = CenterCrop(imageWidth, imageHeight, targetAspect);
        var scale = Math.Clamp(crop.Width * factor, max.Width * MinZoomShare, max.Width) / crop.Width;
        // même contrainte sur la hauteur (l'arrivée en butée doit stopper les deux axes ensemble)
        scale = Math.Clamp(crop.Height * scale, max.Height * MinZoomShare, max.Height) / crop.Height;

        var w = crop.Width * scale;
        var h = crop.Height * scale;
        var cx = crop.X + crop.Width / 2;
        var cy = crop.Y + crop.Height / 2;
        return ClampToBounds(new CropSpec(cx - w / 2, cy - h / 2, w, h));
    }

    /// <summary>
    /// Oriente le canevas du produit comme la photo : renvoie (largeur, hauteur) éventuellement
    /// échangées pour qu'une photo paysage parte en 15×10 plutôt que rognée en 10×15.
    /// L'aspect effectif tient compte du recadrage (exprimé sur l'image orientée).
    /// </summary>
    public static (int Width, int Height) OrientCanvas(
        int canvasWidth, int canvasHeight, int imageWidth, int imageHeight, CropSpec crop)
    {
        if (canvasWidth == canvasHeight) return (canvasWidth, canvasHeight);

        var effectiveAspect = crop.Width * imageWidth / (crop.Height * imageHeight);
        var imageLandscape = effectiveAspect > 1;
        var canvasLandscape = canvasWidth > canvasHeight;
        return imageLandscape == canvasLandscape
            ? (canvasWidth, canvasHeight)
            : (canvasHeight, canvasWidth);
    }

    /// <summary>
    /// Ramène un recadrage (déplacé/zoomé par l'utilisateur) dans les bornes 0..1
    /// en préservant sa taille quand c'est possible.
    /// </summary>
    public static CropSpec ClampToBounds(CropSpec crop)
    {
        var w = Math.Clamp(crop.Width, 0.01, 1);
        var h = Math.Clamp(crop.Height, 0.01, 1);
        var x = Math.Clamp(crop.X, 0, 1 - w);
        var y = Math.Clamp(crop.Y, 0, 1 - h);
        return new CropSpec(x, y, w, h);
    }
}
