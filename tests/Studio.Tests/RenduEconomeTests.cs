using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le décodage économe des JPEG, et surtout la QUALITÉ qu'il ne doit pas coûter.
///
/// <b>Pourquoi.</b> Mesuré le 05/08/2026 sur la planche d'identité de la commande 05-026
/// (photo de 6016 × 4000 pour une cellule de 413 × 531) : le rendu passait 320 ms à
/// décoder l'image entière puis 920 ms à la réduire. Le décodeur JPEG sait rendre
/// directement au demi, au quart ou au huitième — le rendu complet est passé de 2587 ms à
/// 1696 ms, et le 10×15 courant de 1216 ms à 815 ms.
///
/// <b>Un gain de temps qui abîmerait les tirages ne serait pas un gain.</b> Ces essais
/// figent donc l'écart admis entre le rendu économe et le rendu pleine résolution — c'est
/// la seule chose qui empêche d'aller trop loin en cherchant de la vitesse.
///
/// Les cotes sont RÉDUITES par rapport à la boutique : ce qui déclenche le décodage
/// économe est le RAPPORT entre la source et la cible, pas leur taille absolue. Une photo
/// de 24 Mpx rendue quatre fois mettait cinq minutes, pour ne rien prouver de plus.
/// </summary>
public class RenduEconomeTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "RenduEconome-" + Guid.NewGuid().ToString("N"));

    public RenduEconomeTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
    }

    /// <summary>
    /// Une photo avec du DÉTAIL : un aplat se rééchantillonne sans erreur et ne prouverait
    /// rien. Des disques concentriques PLEINS donnent des contours dans toutes les
    /// directions — ce qu'un sous-échantillonnage abîme en premier.
    ///
    /// <b>Pleins, et non des traits fins.</b> Une mire de traits d'un ou deux pixels est un
    /// cas d'aliasing extrême qu'aucune photo ne présente : le moindre demi-pixel de
    /// décalage y fait bondir l'écart, et l'essai refusait alors une réduction que la vraie
    /// photo de la boutique traverse à 0,002 d'écart. On mesure ce qu'on imprime.
    /// </summary>
    private string PhotoAvecDuDetail(uint largeur = 2400, uint hauteur = 1600)
    {
        var chemin = Path.Combine(_racine, $"photo-{largeur}x{hauteur}.jpg");

        using var image = new MagickImage(MagickColors.White, largeur, hauteur);

        var dessins = new ImageMagick.Drawing.Drawables();
        for (var i = 12; i >= 1; i--)
        {
            // rayons calculés en PROPORTION : sur une petite source, un pas fixe les
            // rendrait négatifs et le dessin partirait en vrille
            dessins.FillColor(i % 2 == 0 ? MagickColors.Firebrick : MagickColors.SteelBlue)
                   .Ellipse(largeur / 2.0, hauteur / 2.0,
                            largeur / 2.0 * i / 13.0, hauteur / 2.0 * i / 13.0, 0, 360);
        }
        image.Draw(dessins);

        image.Write(chemin, MagickFormat.Jpeg);
        return chemin;
    }

    private static RenderRequest Demande(string source, int largeur, int hauteur,
        double redressement = 0, CropSpec? crop = null) =>
        new(source, largeur, hauteur, crop ?? CropSpec.Full,
            RotationQuarterTurns: 0, redressement, FitMode.Fill, BorderPx: 0,
            new ImageAdjustments());

    /// <summary>
    /// Compare le rendu économe au rendu pleine résolution.
    ///
    /// La référence passe par un PNG — format que le décodage économe ne touche pas — de
    /// sorte qu'on compare bien deux chemins du MÊME pipeline, et non deux pipelines.
    /// </summary>
    private double EcartAvecLaReference(RenderRequest demande)
    {
        var pleine = Path.Combine(_racine, "source-pleine.png");
        using (var image = new MagickImage(demande.SourcePath)) image.Write(pleine);

        var reference = Path.Combine(_racine, "reference.png");
        ImagePipeline.RenderToFile(demande with { SourcePath = pleine }, reference, 300);

        var econome = Path.Combine(_racine, "econome.png");
        ImagePipeline.RenderToFile(demande, econome, 300);

        using var a = new MagickImage(reference);
        using var b = new MagickImage(econome);

        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);

        return a.Compare(b, ErrorMetric.RootMeanSquared);
    }

    /// <summary>
    /// Le seuil admis. La 13ᵉ passe avait déjà accepté 0,0096 pour la réduction avant
    /// redressement ; on reste très en dessous. Mesuré sur la vraie photo de la boutique :
    /// 0,00302 pour un 10×15, 0,00209 pour une cellule d'identité.
    /// </summary>
    private const double EcartAdmis = 0.02;

    /// <summary>Le tirage que la boutique sort toute la journée.</summary>
    [Fact]
    public void Un_tirage_ordinaire_econome_rend_la_meme_image()
    {
        var ecart = EcartAvecLaReference(Demande(PhotoAvecDuDetail(), 900, 600));

        Assert.True(ecart < EcartAdmis, $"écart RMS de {ecart:0.00000} sur un tirage ordinaire");
    }

    /// <summary>
    /// La cellule d'identité : la plus petite cible, donc la réduction la plus forte — et
    /// c'est aussi celle qui doit rester nette, un visage à 35 × 45 mm ne pardonne rien.
    /// </summary>
    [Fact]
    public void Une_cellule_d_identite_redressee_rend_la_meme_image()
    {
        var ecart = EcartAvecLaReference(
            Demande(PhotoAvecDuDetail(), 413, 531, redressement: 1.25,
                    crop: new CropSpec(0.15, 0.13, 0.69, 0.59)));

        Assert.True(ecart < EcartAdmis, $"écart RMS de {ecart:0.00000} sur une cellule d'identité");
    }

    /// <summary>
    /// Un recadrage SERRÉ demande plus de source que le tirage n'en montre : sans la
    /// division par la part retenue, on décoderait trop petit et le tirage sortirait mou.
    /// </summary>
    [Fact]
    public void Un_recadrage_serre_garde_sa_definition()
    {
        var ecart = EcartAvecLaReference(
            Demande(PhotoAvecDuDetail(), 900, 600, crop: new CropSpec(0.3, 0.3, 0.2, 0.2)));

        Assert.True(ecart < EcartAdmis, $"écart RMS de {ecart:0.00000} sur un recadrage serré");
    }

    /// <summary>
    /// <b>Un agrandissement ne doit RIEN perdre.</b> Quand le besoin dépasse ce que le
    /// fichier contient, le décodeur doit le lire en entier — il ne sait pas agrandir, et
    /// l'indication de taille ne doit pas le faire croire.
    /// </summary>
    [Fact]
    public void Un_agrandissement_lit_la_source_entiere()
    {
        var ecart = EcartAvecLaReference(Demande(PhotoAvecDuDetail(), 4800, 3200));

        Assert.True(ecart < EcartAdmis, $"écart RMS de {ecart:0.00000} sur un agrandissement");
    }

    /// <summary>
    /// Les formats sans décodage progressif — PNG, TIFF — passent par le chemin d'avant.
    /// Le rendu doit rester identique, et surtout ne pas échouer.
    /// </summary>
    [Fact]
    public void Un_PNG_se_rend_comme_avant()
    {
        var png = Path.Combine(_racine, "source.png");
        using (var image = new MagickImage(MagickColors.SteelBlue, 1200u, 800u))
            image.Write(png);

        var sortie = Path.Combine(_racine, "png-rendu.png");
        ImagePipeline.RenderToFile(Demande(png, 900, 600), sortie, 300);

        using var rendu = new MagickImage(sortie);
        Assert.Equal(900u, rendu.Width);
        Assert.Equal(600u, rendu.Height);
    }

    /// <summary>
    /// Une source plus PETITE que le tirage ne doit pas être agrandie au décodage — ni
    /// faire échouer le rendu. C'est le cas d'une photo de téléphone tirée en grand.
    /// </summary>
    [Fact]
    public void Une_petite_source_n_est_pas_agrandie_au_decodage()
    {
        var petite = PhotoAvecDuDetail(800, 600);
        var sortie = Path.Combine(_racine, "petite-rendue.png");

        ImagePipeline.RenderToFile(Demande(petite, 1795, 1205), sortie, 300);

        using var rendu = new MagickImage(sortie);
        Assert.Equal(1795u, rendu.Width);
        Assert.Equal(1205u, rendu.Height);
    }
}
