using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// L'espace de couleur d'une photo de client, et le décalage qu'on prenait faute de le lire.
///
/// <b>Le cas réel.</b> Commande 20-013 du 20/08/2026 : planche d'identité tirée d'un
/// <c>_DSC0905.JPG</c> — Nikon D3200 réglé en Adobe RGB. Le fichier ne porte AUCUN profil
/// ICC ; il ne le déclare que dans l'EXIF (<c>ColorSpace = 65535</c>). Studio le lisait donc
/// comme du sRGB, et la peau sortait froide.
///
/// Ces essais tiennent la règle sur des pixels, et non sur l'intention du code : une couleur
/// de peau connue, un fichier fabriqué comme le fait l'appareil, et la mesure de ce qui en
/// ressort.
/// </summary>
public class EspaceCouleurSourceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Espace-" + Guid.NewGuid().ToString("N"));

    public EspaceCouleurSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>La peau du front de _DSC0905, relevée dans le fichier du client.</summary>
    private static readonly MagickColor Peau = MagickColor.FromRgb(216, 170, 147);

    /// <summary>
    /// Une photo comme l'écrit un reflex réglé en Adobe RGB : pas de profil ICC, et
    /// l'EXIF qui dit « Uncalibrated ».
    /// </summary>
    private string PhotoAdobeRgbSansProfil(ushort colorSpace = 65535)
    {
        var chemin = Path.Combine(_root, $"_DSC{colorSpace}.jpg");

        using var image = new MagickImage(Peau, 400, 300);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.ColorSpace, colorSpace);
        image.SetProfile(exif);
        image.Write(chemin, MagickFormat.Jpeg);

        return chemin;
    }

    private static (byte R, byte V, byte B) Pixel(IMagickImage<byte> image)
    {
        using var px = image.GetPixels();
        var p = px.GetPixel(10, 10);
        return ((byte)p.GetChannel(0), (byte)p.GetChannel(1), (byte)p.GetChannel(2));
    }

    /// <summary>
    /// LE DÉFAUT LUI-MÊME : sans profil embarqué, l'EXIF est le seul témoin, et il dit
    /// Adobe RGB. La peau doit se réchauffer — c'est ce qui manquait sur le papier.
    /// </summary>
    [Fact]
    public void Une_photo_AdobeRGB_sans_profil_est_ramenee_en_srgb()
    {
        using var image = new MagickImage(PhotoAdobeRgbSansProfil());

        var avant = Pixel(image);
        EspaceCouleurSource.RamenerEnSrgb(image);
        var apres = Pixel(image);

        Assert.True(apres.R > avant.R + 8,
            $"la peau devait se réchauffer : {avant} → {apres}");

        // et le vert comme le bleu bougent peu : c'est un virage de teinte, pas un
        // éclaircissement général
        Assert.InRange(apres.V, avant.V - 6, avant.V + 6);
        Assert.InRange(apres.B, avant.B - 6, avant.B + 6);
    }

    /// <summary>Certains appareils écrivent 2 au lieu de 65535 pour dire Adobe RGB.</summary>
    [Fact]
    public void La_valeur_2_est_lue_comme_AdobeRGB()
    {
        using var image = new MagickImage(PhotoAdobeRgbSansProfil(colorSpace: 2));

        Assert.NotNull(EspaceCouleurSource.DeclareParLExif(image));
    }

    /// <summary>
    /// <b>Une photo sRGB ne doit RIEN subir.</b> C'est la quasi-totalité de ce qui passe au
    /// comptoir — téléphones, compacts, cartes de clients — et une conversion appliquée à
    /// tort virerait toutes les photos du magasin.
    /// </summary>
    [Fact]
    public void Une_photo_srgb_nest_pas_touchee()
    {
        using var image = new MagickImage(PhotoAdobeRgbSansProfil(colorSpace: 1));

        var avant = Pixel(image);
        EspaceCouleurSource.RamenerEnSrgb(image);

        Assert.Equal(avant, Pixel(image));
        Assert.Null(EspaceCouleurSource.DeclareParLExif(image));
    }

    /// <summary>Sans EXIF du tout : sRGB présumé, le comportement d'avant la règle.</summary>
    [Fact]
    public void Une_photo_sans_exif_est_presumee_srgb()
    {
        var chemin = Path.Combine(_root, "sans-exif.jpg");
        using (var neuve = new MagickImage(Peau, 400, 300)) neuve.Write(chemin, MagickFormat.Jpeg);

        using var image = new MagickImage(chemin);

        var avant = Pixel(image);
        EspaceCouleurSource.RamenerEnSrgb(image);

        Assert.Equal(avant, Pixel(image));
    }

    /// <summary>
    /// Le profil EMBARQUÉ l'emporte sur l'EXIF : il est la déclaration la plus forte, et il
    /// peut porter autre chose qu'Adobe RGB. Une photo marquée sRGB par son profil ne bouge
    /// pas, même si son EXIF dit « Uncalibrated » — ce que font certains logiciels de
    /// retouche en réenregistrant.
    /// </summary>
    [Fact]
    public void Le_profil_embarque_lemporte_sur_lexif()
    {
        var chemin = Path.Combine(_root, "profil-et-exif.jpg");

        using (var neuve = new MagickImage(Peau, 400, 300))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.ColorSpace, (ushort)65535);
            neuve.SetProfile(exif);
            neuve.SetProfile(ColorProfiles.SRGB);
            neuve.Write(chemin, MagickFormat.Jpeg);
        }

        using var image = new MagickImage(chemin);

        var avant = Pixel(image);
        EspaceCouleurSource.RamenerEnSrgb(image);
        var apres = Pixel(image);

        Assert.InRange(apres.R, avant.R - 2, avant.R + 2);
        Assert.InRange(apres.V, avant.V - 2, avant.V + 2);
        Assert.InRange(apres.B, avant.B - 2, avant.B + 2);
    }

    /// <summary>
    /// ET LE TIRAGE SUIT : la correction ne vaut que si elle traverse tout le pipeline.
    /// On rend la photo comme le fait un tirage, et l'on mesure sur le fichier produit.
    /// </summary>
    [Fact]
    public void Le_tirage_dune_photo_AdobeRGB_sort_rechauffe()
    {
        var source = PhotoAdobeRgbSansProfil();
        var sortie = Path.Combine(_root, "tirage.png");

        ImagePipeline.RenderToFile(
            new RenderRequest(source, 200, 150, CropSpec.Full, 0, 0, FitMode.Fill, 0,
                new ImageAdjustments()),
            sortie);

        using var tirage = new MagickImage(sortie);
        var (r, v, b) = Pixel(tirage);

        // la peau du fichier vaut 216,170,147 ; ramenée en sRGB elle se réchauffe nettement
        Assert.True(r > 224, $"le tirage devait sortir réchauffé, il vaut {r},{v},{b}");
    }
}
