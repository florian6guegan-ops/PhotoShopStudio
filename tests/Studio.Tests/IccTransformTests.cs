using System.Drawing;
using System.Drawing.Imaging;
using ImageMagick;
using Studio.Printing.LargeFormat;

// ImageMagick a son propre RenderingIntent : sans cet alias, le nom est ambigu ici comme
// dans IccTransform, qui traduit justement de l'un vers l'autre.
using Intent = Studio.Printing.LargeFormat.RenderingIntent;

namespace Studio.Tests;

/// <summary>
/// La conversion ICC des agrandissements.
///
/// Elle n'existait pas : la boîte d'impression proposait « Profil de l'imprimante », « Mode de
/// rendu » et « Compensation du point noir », et <see cref="LargeFormatPrinter"/> imprimait
/// l'image telle quelle. L'opérateur réglait sa colorimétrie sans qu'aucun pixel ne change.
/// Ces tests portent donc sur les PIXELS : que la conversion ait lieu, et que chaque réglage
/// pèse dessus.
/// </summary>
public class IccTransformTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Icc-" + Guid.NewGuid().ToString("N"));

    private readonly string _profilPapier;

    public IccTransformTests()
    {
        Directory.CreateDirectory(_root);

        // un profil RVB qui n'est pas sRGB : convertir vers lui doit se voir.
        // AdobeRGB est livré avec Magick.NET, donc présent sur toute machine de test.
        _profilPapier = Path.Combine(_root, "papier.icc");
        File.WriteAllBytes(_profilPapier, ColorProfiles.AdobeRGB1998.ToByteArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Un carré d'une seule couleur : ce que la conversion lui fait se lit directement.</summary>
    private static Bitmap Uni(int r, int g, int b)
    {
        var bitmap = new Bitmap(32, 32, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(r, g, b));
        return bitmap;
    }

    private static Color Lire(Bitmap bitmap) => bitmap.GetPixel(16, 16);

    private static Bitmap Convertir(
        Bitmap source, string profil,
        Intent intent = Intent.RelativeColorimetric, bool bpc = true) =>
        IccTransform.Apply(source, documentProfile: null, profil, intent, bpc);

    [Fact]
    public void La_conversion_change_reellement_les_pixels()
    {
        using var source = Uni(200, 60, 40);
        using var converti = Convertir(source, _profilPapier);

        Assert.NotEqual(Lire(source), Lire(converti));
    }

    [Fact]
    public void La_conversion_garde_la_taille_de_l_image()
    {
        using var source = Uni(120, 130, 140);
        using var converti = Convertir(source, _profilPapier);

        Assert.Equal(source.Width, converti.Width);
        Assert.Equal(source.Height, converti.Height);
    }

    /// <summary>
    /// Le mode de rendu pèse sur le résultat. Sans cela, la liste déroulante n'était qu'un
    /// ornement — ce qu'elle a été depuis le début.
    /// </summary>
    [Fact]
    public void Le_mode_de_rendu_change_le_resultat()
    {
        var papier = ProfilPapierReel();

        // une couleur saturée, hors du gamut du papier : c'est là que les modes divergent
        using var source = Uni(0, 255, 30);

        using var relatif = Convertir(source, papier, Intent.RelativeColorimetric);
        using var perception = Convertir(source, papier, Intent.Perceptual);

        Assert.NotEqual(Lire(relatif), Lire(perception));
    }

    /// <summary>La compensation du point noir agit là où elle doit : dans les ombres.</summary>
    [Fact]
    public void La_compensation_du_point_noir_change_les_ombres()
    {
        var papier = ProfilPapierReel();

        using var ombre = Uni(6, 6, 6);

        using var avec = Convertir(ombre, papier, bpc: true);
        using var sans = Convertir(ombre, papier, bpc: false);

        Assert.NotEqual(Lire(avec), Lire(sans));
    }

    /// <summary>
    /// Un VRAI profil de papier, celui que le pilote Epson installe.
    ///
    /// Les profils livrés avec Magick.NET (sRGB, AdobeRGB…) sont matriciels : entre deux
    /// d'entre eux, le mode de rendu et la compensation du point noir ne changent
    /// légitimement RIEN — même point blanc, noir à zéro de part et d'autre. Ils ne
    /// prouveraient donc pas que ces réglages arrivent jusqu'au moteur de couleurs. Seul un
    /// profil à table de correspondance, avec un gamut et un point noir réels, le montre — et
    /// c'est justement sur ceux-là que l'atelier tire.
    /// </summary>
    private static string ProfilPapierReel()
    {
        var dossier = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color");

        var profil = Directory.Exists(dossier)
            ? Directory.EnumerateFiles(dossier, "SC-P800*.icc").OrderBy(f => f).FirstOrDefault()
            : null;

        Assert.True(profil is not null,
            $"Aucun profil papier SC-P800 dans « {dossier} » : installez le pilote Epson. " +
            "Sans lui, rien ne prouve que le mode de rendu et la compensation du point noir " +
            "arrivent réellement au moteur de couleurs.");

        return profil!;
    }

    /// <summary>
    /// Un profil introuvable doit le dire clairement, et non sortir un tirage muet.
    /// </summary>
    [Fact]
    public void Un_profil_introuvable_est_signale()
    {
        using var source = Uni(120, 120, 120);

        Assert.Throws<FileNotFoundException>(
            () => Convertir(source, Path.Combine(_root, "absent.icc")));
    }

    /// <summary>
    /// Un profil CMJN ne peut pas partir sur ce chemin d'impression : on le dit à l'opérateur
    /// au lieu de lui rendre des couleurs fausses.
    /// </summary>
    [Fact]
    public void Un_profil_cmjn_est_refuse_avec_une_explication()
    {
        var cmjn = Path.Combine(_root, "cmjn.icc");
        File.WriteAllBytes(cmjn, ColorProfiles.USWebCoatedSWOP.ToByteArray());

        using var source = Uni(120, 120, 120);

        var erreur = Assert.Throws<InvalidOperationException>(() => Convertir(source, cmjn));
        Assert.Contains("CMJN", erreur.Message);
    }

    /// <summary>
    /// L'image ne doit pas partir en biais : GDI+ aligne ses lignes sur quatre octets,
    /// ImageMagick non. Une largeur qui n'est pas un multiple de quatre est le cas piège.
    /// </summary>
    [Fact]
    public void Une_largeur_non_alignee_ne_decale_pas_l_image()
    {
        using var source = new Bitmap(37, 11, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
            graphics.Clear(Color.FromArgb(200, 60, 40));

        using var converti = Convertir(source, _profilPapier);

        // toute l'image était unie : elle doit le rester d'un coin à l'autre
        var coin = converti.GetPixel(0, 0);
        Assert.Equal(coin, converti.GetPixel(36, 10));
        Assert.Equal(coin, converti.GetPixel(36, 0));
        Assert.Equal(coin, converti.GetPixel(0, 10));
    }

    /// <summary>Les images 32 bits (avec canal alpha) passent aussi.</summary>
    [Fact]
    public void Une_image_32_bits_est_acceptee()
    {
        using var source = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
            graphics.Clear(Color.FromArgb(255, 200, 60, 40));

        using var converti = Convertir(source, _profilPapier);

        Assert.Equal(32, converti.Width);
        Assert.NotEqual(Color.FromArgb(255, 200, 60, 40), Lire(converti));
    }
}
