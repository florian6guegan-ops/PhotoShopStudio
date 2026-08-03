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
    /// Dimensions du canevas qu'un redressement produit : l'image tournée, coins vides
    /// compris.
    ///
    /// C'est sur CE canevas que les fractions d'un recadrage se comptent, puisque le
    /// rendu tourne l'image avant de la recadrer. Juger de l'orientation d'un cadrage sur
    /// les dimensions de l'image droite fait partir de travers un cadre presque carré.
    /// </summary>
    public static (double Width, double Height) TiltedCanvas(double width, double height, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        return (width * cos + height * sin, width * sin + height * cos);
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
    /// en préservant sa taille quand c'est possible, et TOUJOURS ses proportions.
    ///
    /// Les proportions comptent autant que les bornes : un cadre trop grand pour l'image
    /// dont on ne rabotait que la hauteur changeait de forme au passage. Sur une photo
    /// d'identité où la tête est petite dans le cadre, <see cref="IdPhotoFr.ComputeCrop"/>
    /// demande un cadre plus haut que l'image ; l'ancienne version bornait hauteur et
    /// largeur séparément et rendait un cadre au rapport quelconque — celui affiché à
    /// l'écran ne correspondait alors plus au format choisi (signalé le 03/08/2026).
    /// On réduit donc les deux côtés du même facteur.
    /// </summary>
    public static CropSpec ClampToBounds(CropSpec crop)
    {
        var w = Math.Max(crop.Width, 0.01);
        var h = Math.Max(crop.Height, 0.01);

        // trop grand pour l'image : on rétrécit les DEUX côtés d'autant, la forme est gardée
        var reduction = Math.Min(1, Math.Min(1 / w, 1 / h));
        w *= reduction;
        h *= reduction;

        var x = Math.Clamp(crop.X, 0, 1 - w);
        var y = Math.Clamp(crop.Y, 0, 1 - h);
        return new CropSpec(x, y, w, h);
    }
}
