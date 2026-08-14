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
}
