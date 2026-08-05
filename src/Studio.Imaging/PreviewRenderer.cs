using ImageMagick;
using Studio.Core.Domain;

namespace Studio.Imaging;

/// <summary>Un aperçu corrigé, en pixels BGRA prêts à afficher.</summary>
public sealed record PreviewPixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// Aperçu des corrections, pour que l'opérateur voie ce qu'il règle pendant qu'il le règle.
///
/// La photo source est décodée, réduite et <b>désossée en octets</b> une seule fois, à la
/// construction. Chaque aperçu repart de ces octets : un tableau à recopier, les
/// corrections dessus, et l'écran l'affiche tel quel. Les réglages ne s'accumulent donc
/// pas d'un aperçu à l'autre, et surtout aucune image ImageMagick n'est refabriquée en
/// route.
///
/// <b>Ce que ça change.</b> La version précédente clonait l'image Magick, la corrigeait,
/// puis en ressortait les pixels : trois allers-retours entre le tableau d'octets et
/// l'objet natif à chaque mouvement de curseur. Avec le calcul devenu rapide
/// (<see cref="PixelCorrections"/>), c'était devenu l'essentiel du temps — 64 ms pour un
/// développement complet, dont 12 de calcul. Ici il n'en reste qu'une copie de tableau.
///
/// La taille de travail — 900 px sur le grand côté — est celle de DiLand, qui plafonne la
/// sienne à 1000 (<c>PhotoItem.GetReducedImage</c>). Lui l'écrit sur le disque et la relit ;
/// nous la gardons en mémoire.
///
/// L'aperçu ne sert qu'à voir : le tirage, lui, repasse par le pipeline complet à pleine
/// résolution — mais par les mêmes corrections, au pixel près.
/// </summary>
public sealed class PreviewRenderer : IDisposable
{
    private readonly byte[] _octets;
    private bool _libere;

    /// <summary>
    /// L'image réduite, gardée telle quelle : les trois corrections automatiques ont besoin
    /// de mesurer l'image entière — histogramme, extrema, moyenne par canal — et c'est le
    /// travail d'ImageMagick. Elles ne bougent pas pendant qu'un curseur glisse, donc leur
    /// résultat se garde d'un aperçu à l'autre.
    /// </summary>
    private readonly MagickImage _reduite;

    private ImageAdjustments? _automatismesEnCache;
    private byte[]? _apresAutomatismes;

    public int Width { get; }
    public int Height { get; }

    /// <param name="sourcePath">Photo d'origine.</param>
    /// <param name="maxSide">Côté le plus long de l'aperçu, en pixels.</param>
    public PreviewRenderer(string sourcePath, int maxSide = 900)
    {
        // La source est décodée À LA TAILLE DE L'APERÇU, pas à celle du fichier.
        //
        // Un reflex de 24 Mpx était décodé en entier pour n'en garder que 900 px de côté,
        // soit 0,8 Mpx : trente fois trop de pixels, et une pointe de mémoire de cent
        // mégaoctets par photo ouverte. Le décodeur JPEG sait rendre l'image au huitième
        // sans rien perdre de ce qu'on en fera — voir MagickInit.IndicationDeTaille. C'est
        // ce qui rend l'ouverture de « Modifier » immédiate sur une carte de reflex.
        _reduite = MagickInit.Lire(sourcePath, maxSide);
        _reduite.AutoOrient();

        if (_reduite.GetColorProfile() is { } profil)
            _reduite.TransformColorSpace(profil, ColorProfiles.SRGB);

        _reduite.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));

        // BGRA d'emblée : c'est ce que WPF attend, et ce qui évite une conversion par image
        _reduite.Alpha(AlphaOption.Opaque);

        Width = (int)_reduite.Width;
        Height = (int)_reduite.Height;

        _octets = _reduite.GetPixels().ToByteArray(PixelMapping.BGRA)
                  ?? throw new InvalidOperationException("Aperçu illisible : pixels absents.");
    }

    /// <summary>
    /// Aperçu corrigé, en pixels BGRA bruts.
    /// </summary>
    /// <param name="adjustments">Réglages à appliquer.</param>
    /// <param name="avecRelief">
    /// Faux pour sauter la clarté et la netteté. Ce n'était au départ qu'une béquille de
    /// vitesse ; le relief coûte désormais quelques millisecondes et l'aperçu l'applique
    /// toujours. Le paramètre reste pour les appelants qui n'en veulent pas.
    /// </param>
    public PreviewPixels RenderPixels(ImageAdjustments adjustments, bool avecRelief = true)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        ObjectDisposedException.ThrowIf(_libere, this);

        // un tableau neuf par aperçu : celui qu'on rend part vivre à l'écran, qui le garde
        // aussi longtemps qu'il l'affiche — un tampon réutilisé se ferait réécrire sous lui
        var travail = (byte[])Depart(adjustments).Clone();

        var disposition = PixelCorrections.Disposition.Bgra;
        PixelCorrections.AppliquerPoints(travail, Width, Height, disposition, adjustments);

        if (avecRelief)
            PixelCorrections.AppliquerRelief(travail, Width, Height, disposition, adjustments);

        return new PreviewPixels(travail, Width, Height);
    }

    /// <summary>
    /// Les octets sur lesquels partir : l'image réduite, ou son état après les automatismes.
    ///
    /// Le noir et blanc en fait partie : comme les automatismes, il précède tout le reste et
    /// ne change pas d'un mouvement de curseur à l'autre. Le calculer une fois épargne une
    /// conversion d'espace colorimétrique par image affichée.
    /// </summary>
    private byte[] Depart(ImageAdjustments a)
    {
        if (!a.Grayscale && !a.AutoLevels && !a.AutoContrast && !a.AutoColor) return _octets;

        if (_apresAutomatismes is { } cache && _automatismesEnCache is { } faits &&
            faits.Grayscale == a.Grayscale && faits.AutoLevels == a.AutoLevels &&
            faits.AutoContrast == a.AutoContrast && faits.AutoColor == a.AutoColor)
            return cache;

        using var copie = (MagickImage)_reduite.Clone();

        if (a.Grayscale) copie.Grayscale(PixelIntensityMethod.Rec709Luma);
        if (a.AutoColor && !a.Grayscale) copie.WhiteBalance();
        if (a.AutoLevels) copie.AutoLevel();
        if (a.AutoContrast) copie.Normalize();

        // le noir et blanc laisse l'image sur un seul canal : sans ce retour en sRGB, la
        // relecture BGRA n'aurait plus les canaux qu'elle attend
        if (copie.ColorSpace != ColorSpace.sRGB) copie.ColorSpace = ColorSpace.sRGB;
        copie.Alpha(AlphaOption.Opaque);

        _apresAutomatismes = copie.GetPixels().ToByteArray(PixelMapping.BGRA) ?? _octets;
        _automatismesEnCache = a.Clone();

        return _apresAutomatismes;
    }

    public void Dispose()
    {
        if (_libere) return;
        _libere = true;
        _reduite.Dispose();
    }
}
