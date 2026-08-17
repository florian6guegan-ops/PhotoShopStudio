using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// LE CADRAGE DÉCIDE DU SENS DE LA CASE, sur une planche personnalisée.
///
/// Le défaut corrigé, payé deux fois en papier à Créteil le 14/08/2026 (commandes 14-018
/// puis 14-027) : l'écran de recadrage donne au cadre l'orientation de LA PHOTO, pendant
/// que la planche prend celle du FORMAT SAISI. L'opérateur cadrait des portraits debout,
/// bien centrés, et Studio les coulait dans des cases couchées de 80 × 65 mm — coupés en
/// haut et en bas, sans que rien ne le signale.
///
/// Le rectangle de cadrage EST l'expression de ce qu'on veut voir sortir. C'est lui qui
/// tranche, et non l'ordre dans lequel deux nombres ont été tapés.
/// </summary>
public class SensDesCadragesTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "studio-sens-cadrages-" + Guid.NewGuid().ToString("N"));

    public SensDesCadragesTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Une photo de la taille voulue, écrite pour de vrai : on PING ses cotes.</summary>
    private string Photo(string nom, uint largeur, uint hauteur)
    {
        var chemin = Path.Combine(_dossier, nom);
        using var image = new MagickImage(MagickColors.Gray, largeur, hauteur);
        image.Write(chemin);
        return chemin;
    }

    private static OrderItem Article(string fichier, double cropLargeur, double cropHauteur,
        int quartsDeTour = 0) =>
        new()
        {
            FileName = fichier,
            Quantity = 1,
            RotationQuarterTurns = quartsDeTour,
            Crop = new CropSpec(0, 0, cropLargeur, cropHauteur),
        };

    private static OrderLine Ligne(params OrderItem[] articles)
    {
        var ligne = new OrderLine { ProductCode = "10x15" };
        ligne.Items.AddRange(articles);
        return ligne;
    }

    // — le cas de Créteil —

    /// <summary>
    /// Les deux photos de la commande 14-027 : des portraits d'identité scannés, cadrés
    /// presque en entier. Le cadrage est DEBOUT, et c'est ce que la planche doit suivre.
    /// </summary>
    [Fact]
    public void Des_portraits_cadres_debout_donnent_un_sens_debout()
    {
        Photo("001.jpg", 1864, 2442);
        Photo("002.jpg", 1827, 2293);

        var sens = PrintOrchestrator.SensDesCadrages(
            Ligne(Article("001.jpg", 0.893, 0.847), Article("002.jpg", 0.842, 0.839)),
            _dossier);

        Assert.True(sens, "deux portraits cadrés debout : la case doit être debout");
    }

    [Fact]
    public void Des_photos_cadrees_couchees_donnent_un_sens_couche()
    {
        Photo("001.jpg", 2442, 1864);

        var sens = PrintOrchestrator.SensDesCadrages(Ligne(Article("001.jpg", 0.9, 0.9)), _dossier);

        Assert.False(sens);
    }

    /// <summary>
    /// Le CADRAGE l'emporte sur la photo : une photo debout dont on ne garde qu'une bande
    /// large est un tirage couché, et la case doit suivre le rectangle, pas le fichier.
    /// </summary>
    [Fact]
    public void Une_photo_debout_cadree_en_bandeau_donne_un_sens_couche()
    {
        Photo("001.jpg", 2000, 3000);

        var sens = PrintOrchestrator.SensDesCadrages(Ligne(Article("001.jpg", 1.0, 0.4)), _dossier);

        Assert.False(sens, "0,4 × 3000 = 1200 de haut pour 2000 de large : c'est couché");
    }

    /// <summary>
    /// Les quarts de tour posés par l'opérateur comptent : ils changent la photo AVANT que
    /// le cadrage ne s'y applique. Les oublier retournerait le verdict.
    /// </summary>
    [Fact]
    public void Un_quart_de_tour_est_pris_en_compte()
    {
        Photo("001.jpg", 2000, 3000);

        var sens = PrintOrchestrator.SensDesCadrages(
            Ligne(Article("001.jpg", 0.9, 0.9, quartsDeTour: 1)), _dossier);

        Assert.False(sens, "pivotée d'un quart de tour, la photo est couchée");
    }

    // — les cas où l'on ne touche à rien —

    /// <summary>
    /// Moitié debout, moitié couché : aucun sens ne s'impose. On garde alors celui que
    /// l'opérateur a saisi, plutôt qu'un arbitrage qui surprendrait une planche sur deux.
    /// </summary>
    [Fact]
    public void Des_cadrages_partages_ne_tranchent_pas()
    {
        Photo("001.jpg", 2000, 3000);
        Photo("002.jpg", 3000, 2000);

        var sens = PrintOrchestrator.SensDesCadrages(
            Ligne(Article("001.jpg", 0.9, 0.9), Article("002.jpg", 0.9, 0.9)), _dossier);

        Assert.Null(sens);
    }

    /// <summary>Un fichier absent ne fait pas échouer le tirage : il se signalera au rendu.</summary>
    [Fact]
    public void Un_fichier_illisible_ne_leve_pas()
    {
        var sens = PrintOrchestrator.SensDesCadrages(Ligne(Article("absente.jpg", 0.9, 0.9)), _dossier);

        Assert.Null(sens);
    }

    // — L'ORIENTATION EXIF, l'angle mort de tous les essais ci-dessus —

    /// <summary>
    /// Une photo prise à la verticale, comme elle sort d'un appareil ou d'un téléphone :
    /// les pixels sont STOCKÉS couchés, et une étiquette EXIF dit « tourne-moi ».
    ///
    /// Tous les essais de cette classe écrivaient des images sans étiquette, où les cotes
    /// stockées sont aussi les cotes vues. C'est précisément ce qui a laissé passer le
    /// défaut de la commande 17-021.
    /// </summary>
    private string PhotoDeboutParEtiquette(string nom, uint largeurStockee, uint hauteurStockee)
    {
        var chemin = Path.Combine(_dossier, nom);

        using (var image = new MagickImage(MagickColors.Gray, largeurStockee, hauteurStockee))
        {
            // ⚠ IL FAUT LES DEUX, ET DANS CET ORDRE : le profil EXIF d'abord, la propriété
            // Orientation ensuite. Mesuré le 17/08/2026 — l'un OU l'autre seul se relit
            // « Undefined », les deux ensemble donnent bien LeftBottom. C'est ce qui avait
            // fait renoncer à couvrir la règle par un essai (voir ThumbnailService).
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)8);
            image.SetProfile(exif);
            image.Orientation = OrientationType.LeftBottom;   // 8 : « pivoter d'un quart de tour »
            image.Write(chemin, MagickFormat.Jpeg);
        }

        // ⚠ On VÉRIFIE que l'étiquette tient. Sans ce contrôle, un jour où Magick.NET
        // cesserait de l'écrire, les deux essais ci-dessous passeraient au vert en ne
        // prouvant plus rien — ils retomberaient sur une photo couchée ordinaire.
        using var relue = new MagickImage();
        relue.Ping(chemin);
        Assert.True(
            relue.Orientation is OrientationType.LeftTop or OrientationType.RightTop
                or OrientationType.RightBottom or OrientationType.LeftBottom,
            $"l'étiquette EXIF ne s'est pas écrite (relue : {relue.Orientation}) — " +
            "cet essai ne prouverait rien");

        return chemin;
    }

    /// <summary>
    /// LE CAS DE LA COMMANDE 17-021, le 17/08/2026.
    ///
    /// Une photo prise à la verticale : 6016 × 4000 dans le fichier, 4000 × 6016 à l'écran.
    /// L'opérateur a demandé du 7 × 10 cm et cadré debout — le rectangle vaut 0,774 × 0,735
    /// des cotes VUES, soit 3097 × 4425 px, donc debout.
    ///
    /// En lisant l'en-tête BRUT, le même rectangle donne 4658 × 2942 : couché. La planche
    /// basculait alors ses cases en 100 × 70, et le client repartait avec du 10 × 7 après
    /// avoir demandé du 7 × 10. C'est « le format personnalisé ne garde pas le format ».
    /// </summary>
    [Fact]
    public void Un_portrait_par_etiquette_EXIF_cadre_debout_donne_un_sens_debout()
    {
        PhotoDeboutParEtiquette("001.jpg", 6016, 4000);

        var sens = PrintOrchestrator.SensDesCadrages(
            Ligne(Article("001.jpg", 0.7742733, 0.7354420)), _dossier);

        Assert.True(sens,
            "0,774 × 4000 = 3097 de large pour 0,735 × 6016 = 4425 de haut : c'est DEBOUT. " +
            "Lire l'en-tête brut inverserait le verdict.");
    }

    /// <summary>
    /// Le revers, pour que l'essai ci-dessus ne passe pas simplement parce qu'on aurait
    /// inversé la règle : sur la MÊME photo étiquetée, une bande large reste couchée.
    /// </summary>
    [Fact]
    public void Un_portrait_par_etiquette_EXIF_cadre_en_bandeau_donne_un_sens_couche()
    {
        PhotoDeboutParEtiquette("001.jpg", 6016, 4000);

        // 1,0 × 4000 = 4000 de large, 0,4 × 6016 = 2406 de haut : couché
        var sens = PrintOrchestrator.SensDesCadrages(
            Ligne(Article("001.jpg", 1.0, 0.4)), _dossier);

        Assert.False(sens);
    }

    /// <summary>
    /// Étiquette EXIF ET quart de tour de l'opérateur : les deux se cumulent, et il a fallu
    /// les deux pour que le verdict soit juste. La photo est vue debout (4000 × 6016), le
    /// quart de tour la recouche (6016 × 4000), un cadrage presque plein est donc couché.
    /// </summary>
    [Fact]
    public void Etiquette_EXIF_et_quart_de_tour_se_cumulent()
    {
        PhotoDeboutParEtiquette("001.jpg", 6016, 4000);

        var sens = PrintOrchestrator.SensDesCadrages(
            Ligne(Article("001.jpg", 0.9, 0.9, quartsDeTour: 1)), _dossier);

        Assert.False(sens);
    }
}
