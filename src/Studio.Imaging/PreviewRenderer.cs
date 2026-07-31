using ImageMagick;
using Studio.Core.Domain;

namespace Studio.Imaging;

/// <summary>
/// Aperçu des corrections, pour que l'opérateur voie ce qu'il règle pendant qu'il le règle.
///
/// La photo source est décodée et réduite une seule fois, à la construction : appliquer
/// des réglages sur un fichier de 24 mégapixels à chaque mouvement de curseur rendrait
/// l'interface inutilisable. Chaque aperçu repart de cette copie réduite, jamais du
/// fichier — les réglages ne s'accumulent donc pas d'un aperçu à l'autre.
///
/// L'aperçu ne sert qu'à voir : le tirage, lui, repasse par le pipeline complet à pleine
/// résolution.
/// </summary>
public sealed class PreviewRenderer : IDisposable
{
    private readonly MagickImage _reduite;
    private bool _libere;

    /// <param name="sourcePath">Photo d'origine.</param>
    /// <param name="maxSide">Côté le plus long de l'aperçu, en pixels.</param>
    public PreviewRenderer(string sourcePath, int maxSide = 900)
    {
        MagickInit.Configure();

        _reduite = new MagickImage(sourcePath);
        _reduite.AutoOrient();

        if (_reduite.GetColorProfile() is { } profil)
            _reduite.TransformColorSpace(profil, ColorProfiles.SRGB);

        _reduite.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        _reduite.Format = MagickFormat.Png;
    }

    /// <summary>Aperçu corrigé, encodé en PNG.</summary>
    public byte[] Render(ImageAdjustments adjustments)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        ObjectDisposedException.ThrowIf(_libere, this);

        using var copie = _reduite.Clone();
        ImageAdjuster.Apply(copie, adjustments);
        return copie.ToByteArray(MagickFormat.Png);
    }

    public void Dispose()
    {
        if (_libere) return;
        _libere = true;
        _reduite.Dispose();
    }
}
