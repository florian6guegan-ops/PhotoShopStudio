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

    /// <summary>
    /// Niveau de compression des PNG produits par l'atelier : 1, c'est-à-dire le plus
    /// rapide qui compresse encore.
    ///
    /// <b>Pourquoi ce n'est pas un détail.</b> Mesuré le 02/08/2026 sur un rendu 50×70 à
    /// 300 ppp (5906 × 8268 px, 48,8 Mpx), depuis un scan de 7518 × 5013 :
    ///
    /// | Étape | Durée |
    /// |---|---|
    /// | lecture du JPEG source | 710 ms |
    /// | redimensionnement | 4 837 ms |
    /// | **écriture PNG, réglage par défaut** | **32 415 ms** — 41,8 Mo |
    /// | écriture PNG, niveau 1 | 4 126 ms — 50,4 Mo |
    /// | écriture PNG, niveau 0 | 2 296 ms — 139,8 Mo |
    ///
    /// Le rendu complet passe de 42 s à ~10 s. Les 8 Mo de plus par fichier ne coûtent rien :
    /// ces rendus sont des fichiers de travail, effacés à l'archivage des commandes. C'était
    /// là, et non dans l'impression, que « Imprimer » paraissait interminable.
    ///
    /// Le niveau 0 va deux fois plus vite encore, mais triple la taille : 140 Mo par tirage
    /// à relire ensuite depuis le disque, ce qu'on paierait à l'ouverture de la boîte
    /// d'agrandissement.
    /// </summary>
    private const string CompressionPng = "1";

    /// <summary>
    /// Qualité JPEG des rendus d'agrandissement. 95 : la limite au-delà de laquelle le
    /// fichier grossit sans que l'œil y gagne, et bien au-dessus du 92 de la planche
    /// d'index. La source est elle-même un JPEG d'appareil ou de scanner — un ré-encodage
    /// à 95 après agrandissement ne retire rien de visible sur un tirage.
    /// </summary>
    private const int QualiteJpeg = 95;

    /// <summary>
    /// Écrit une image de l'atelier, au format que dit son extension.
    ///
    /// <b>Passer par ici et non par <c>image.Write</c></b> : les réglages par défaut de
    /// Magick.NET coûtent des dizaines de secondes sur les grandes images.
    ///
    /// <b>Pourquoi le format compte à ce point.</b> Mesuré le 02/08/2026 sur un rendu 40×50
    /// à 300 ppp (4724 × 5906 = 27,9 Mpx), depuis une photo de 3024 × 2005 :
    ///
    /// | Écriture | Durée | Taille |
    /// |---|---|---|
    /// | PNG, réglages par défaut | 15 228 ms | 14,7 Mo |
    /// | PNG, compression 1 | 12 531 ms | 16,3 Mo |
    /// | PNG, compression 0, sans filtre | 11 905 ms | 26,6 Mo |
    /// | **JPEG qualité 95** | **694 ms** | **8,9 Mo** |
    ///
    /// Le niveau de compression ne change presque rien : l'encodeur PNG de Magick.NET est
    /// lent en lui-même sur ces définitions, indépendamment de zlib. Seul le changement de
    /// format règle la question — et il divise le rendu par trois.
    ///
    /// <b>Le PNG reste pour les PLANCHES</b> (identité, personnalisées) : elles portent des
    /// contours de découpe de deux dixièmes de millimètre et de la date en petits
    /// caractères, autour desquels le JPEG laisse des franges. Elles sont aussi bien plus
    /// petites, donc le coût ne se voit pas.
    /// </summary>
    public static void Write(IMagickImage<byte> image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            image.Format = MagickFormat.Jpeg;
            image.Quality = QualiteJpeg;
            image.Write(path);
            return;
        }

        image.Settings.SetDefine(MagickFormat.Png, "compression-level", CompressionPng);

        // une image née d'une couleur porte le pseudo-format « XC », que rien ne sait
        // écrire : on impose le format plutôt que de le laisser deviner par l'extension
        image.Format = MagickFormat.Png;
        image.Write(path);
    }
}
