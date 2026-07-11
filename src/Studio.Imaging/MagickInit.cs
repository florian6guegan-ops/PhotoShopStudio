using ImageMagick;

namespace Studio.Imaging;

public static class MagickInit
{
    private static bool _done;

    /// <summary>
    /// Plafonne les ressources de Magick.NET : un fichier client corrompu ou
    /// démesuré ne doit jamais pouvoir mettre l'application à genoux (leçon DiLand).
    /// </summary>
    public static void Configure()
    {
        if (_done) return;
        _done = true;

        ResourceLimits.Memory = 2UL * 1024 * 1024 * 1024;      // 2 Go puis bascule sur disque
        ResourceLimits.Width = 60000;                           // ~15 m à 300 dpi : au-delà c'est un fichier piégé
        ResourceLimits.Height = 60000;
    }
}
