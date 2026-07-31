using System;
using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.App.Infrastructure;

/// <summary>
/// Applique les corrections à une vignette, pour que la grille montre les photos telles
/// qu'elles sortiront.
///
/// Sans cela, l'opérateur corrigerait à l'aveugle : l'aperçu de l'écran de correction ne
/// porte que sur une photo, alors que le réglage part sur toutes celles qui sont cochées.
///
/// La vignette fait deux cents pixels de côté : l'aller-retour par le PNG coûte quelques
/// millisecondes, négligeable devant le rendu du tirage.
/// </summary>
public static class ThumbnailAdjuster
{
    public static BitmapSource Apply(BitmapSource source, ImageAdjustments adjustments)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adjustments);

        if (adjustments.IsNeutral) return source;

        try
        {
            using var flux = new MemoryStream();
            var encodeur = new PngBitmapEncoder();
            encodeur.Frames.Add(BitmapFrame.Create(source));
            encodeur.Save(flux);

            using var image = new MagickImage(flux.ToArray());
            ImageAdjuster.Apply(image, adjustments);

            var corrigee = new BitmapImage();
            corrigee.BeginInit();
            corrigee.StreamSource = new MemoryStream(image.ToByteArray(MagickFormat.Png));
            corrigee.CacheOption = BitmapCacheOption.OnLoad;
            corrigee.EndInit();
            corrigee.Freeze();

            return corrigee;
        }
        catch (Exception e)
        {
            // une vignette non corrigée vaut mieux qu'une grille vide : le tirage, lui,
            // repasse de toute façon par le pipeline complet
            FileLog.Write("Vignette : correction impossible", e);
            return source;
        }
    }
}
