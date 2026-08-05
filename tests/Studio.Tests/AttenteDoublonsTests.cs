using Studio.Core.Domain;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// Une commande où la MÊME photo est tirée dans deux formats, mise de côté puis reprise.
///
/// C'est ce que permet le bouton « Dupliquer » (demandé le 05/08/2026) : une photo en
/// 10×15 et la même en 15×20. Les deux lignes portent alors le même nom de fichier — il
/// n'y en a qu'un sur le disque — et c'est précisément là qu'était le piège.
///
/// <b>La règle de l'attente n'a pas changé</b> : les photos sont désignées par leur NOM DE
/// FICHIER, jamais par leur rang. Mais un nom ne suffit plus à désigner UNE ligne, et la
/// reprise doit rendre autant de lignes qu'elle en a enregistrées.
///
/// L'écran, lui, n'est pas couvert : <c>Studio.App</c> n'est pas référencé par les essais.
/// Ce qui l'est ici, c'est le socle sur lequel il s'appuie — le magasin doit conserver les
/// deux entrées et les rendre dans l'ordre.
/// </summary>
public class AttenteDoublonsTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "AttenteDoublons-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
    }

    private AttenteStore Attente() => new(_racine);

    /// <summary>Deux lignes pour un seul fichier : le 10×15 et le 15×20 de la même photo.</summary>
    private static TravailEnAttente TravailAvecDoublon() => new()
    {
        PhotosDirectory = @"E:\DCIM\100CANON",
        ProduitParDefaut = "10x15",
        Titre = "100CANON",
        Photos =
        [
            new PhotoEnAttente
            {
                FileName = "photo1.jpg",
                Selected = true,
                Quantity = 2,
                ProductCode = "10x15",
                CropX = 0, CropY = 0, CropWidth = 1, CropHeight = 1,
            },
            new PhotoEnAttente
            {
                FileName = "photo1.jpg",
                Selected = true,
                Quantity = 1,
                ProductCode = "15x20",
                CropX = 0.1, CropY = 0.1, CropWidth = 0.8, CropHeight = 0.8,
            },
            new PhotoEnAttente { FileName = "photo2.jpg", Selected = false, Quantity = 1 },
        ],
    };

    [Fact]
    public void Les_deux_lignes_d_une_meme_photo_sont_conservees()
    {
        var attente = Attente();
        var travail = TravailAvecDoublon();
        attente.Enregistrer(travail);

        var relu = attente.Lire(travail.Id);

        Assert.NotNull(relu);
        Assert.Equal(2, relu!.Photos.Count(p => p.FileName == "photo1.jpg"));
    }

    /// <summary>
    /// L'ORDRE compte : c'est lui qui permet à la reprise de rendre le 10×15 à la première
    /// vignette et le 15×20 à la seconde. Une reprise qui les intervertirait ne perdrait
    /// rien, mais tirerait les deux formats à l'envers.
    /// </summary>
    [Fact]
    public void Les_formats_restent_dans_leur_ordre()
    {
        var attente = Attente();
        var travail = TravailAvecDoublon();
        attente.Enregistrer(travail);

        var lignes = attente.Lire(travail.Id)!.Photos
            .Where(p => p.FileName == "photo1.jpg")
            .ToList();

        Assert.Equal("10x15", lignes[0].ProductCode);
        Assert.Equal("15x20", lignes[1].ProductCode);
    }

    /// <summary>
    /// Chaque ligne garde SON cadrage et SA quantité : c'est tout l'objet du doublon —
    /// deux réglages pour une seule image.
    /// </summary>
    [Fact]
    public void Chaque_ligne_garde_son_propre_cadrage()
    {
        var attente = Attente();
        var travail = TravailAvecDoublon();
        attente.Enregistrer(travail);

        var lignes = attente.Lire(travail.Id)!.Photos
            .Where(p => p.FileName == "photo1.jpg")
            .ToList();

        Assert.Equal(2, lignes[0].Quantity);
        Assert.True(lignes[0].Crop.IsFull);

        Assert.Equal(1, lignes[1].Quantity);
        Assert.Equal(0.1, lignes[1].Crop.X, 6);
        Assert.Equal(0.8, lignes[1].Crop.Width, 6);
    }

    /// <summary>
    /// La reprise consomme les entrées d'un même nom À LA FILE — c'est la règle que suit
    /// <c>PhotoGridView.AppliquerLAttente</c>, reproduite ici pour la figer : une simple
    /// recherche par nom donnerait la PREMIÈRE entrée aux deux vignettes, et le second
    /// format serait perdu.
    /// </summary>
    [Fact]
    public void La_reprise_consomme_les_entrees_a_la_file()
    {
        var attente = Attente();
        var travail = TravailAvecDoublon();
        attente.Enregistrer(travail);

        var files = attente.Lire(travail.Id)!.Photos
            .GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new Queue<PhotoEnAttente>(g),
                StringComparer.OrdinalIgnoreCase);

        // deux vignettes réclament « photo1.jpg » : elles doivent recevoir deux entrées
        // DIFFÉRENTES
        var premiere = files["photo1.jpg"].Dequeue();
        var seconde = files["photo1.jpg"].Dequeue();

        Assert.NotEqual(premiere.ProductCode, seconde.ProductCode);
        Assert.Empty(files["photo1.jpg"]);
    }
}
