using Studio.Core.Domain;
using Studio.Imaging.Geometry;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// « Commandes du jour » → « Modifier » doit RENDRE LE TRAVAIL DÉJÀ FAIT.
///
/// L'écran ne rouvrait que le dossier des photos : cadrages, corrections, formats par
/// photo, quantités et finitions étaient perdus, et il fallait tout refaire pour retirer une
/// seule photo d'une commande de quinze. Signalé le 17/08/2026.
/// </summary>
public class TravailDepuisCommandeTests
{
    private static OrderItem Article(string fichier, CropSpec? cadrage = null) => new()
    {
        FileName = fichier,
        Quantity = 2,
        RotationQuarterTurns = 1,
        FineRotationDegrees = 1.5,
        Crop = cadrage ?? new CropSpec(0.1, 0.2, 0.6, 0.5),
        FitOverride = FitMode.Fit,
        CutBorder = true,
        Finish = "Lustré",
        Adjustments = new ImageAdjustments { Exposure = 0.4, Contrast = 12, RedEye = true },
    };

    private static Envelope Enveloppe(params OrderLine[] lignes)
    {
        var e = new Envelope { Number = 1 };
        e.Lines.AddRange(lignes);
        return e;
    }

    private static OrderLine Ligne(string produit, params OrderItem[] articles)
    {
        var l = new OrderLine { ProductCode = produit };
        l.Items.AddRange(articles);
        return l;
    }

    // — ce qui était perdu —

    /// <summary>Le cadrage, au pixel près : c'est le geste le plus long, et le plus perdu.</summary>
    [Fact]
    public void Le_cadrage_est_rendu()
    {
        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(Ligne("10x15", Article("001.jpg", new CropSpec(0.12, 0.13, 0.77, 0.73))))],
            @"C:\photos", "essai");

        var photo = Assert.Single(travail.Photos);
        Assert.Equal(0.12, photo.CropX, 4);
        Assert.Equal(0.13, photo.CropY, 4);
        Assert.Equal(0.77, photo.CropWidth, 4);
        Assert.Equal(0.73, photo.CropHeight, 4);
    }

    [Fact]
    public void Les_corrections_la_rotation_et_la_finition_sont_rendues()
    {
        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(Ligne("10x15", Article("001.jpg")))], @"C:\photos", "essai");

        var photo = Assert.Single(travail.Photos);
        Assert.Equal(0.4, photo.Adjustments.Exposure, 4);
        Assert.Equal(12, photo.Adjustments.Contrast, 4);
        Assert.True(photo.Adjustments.RedEye);
        Assert.Equal(1, photo.RotationQuarterTurns);
        Assert.Equal(1.5, photo.FineRotationDegrees, 4);
        Assert.Equal(FitMode.Fit, photo.Fit);
        Assert.True(photo.CutBorder);
        Assert.Equal("Lustré", photo.Finish);
        Assert.Equal(2, photo.Quantity);
        Assert.Equal("10x15", photo.ProductCode);
    }

    /// <summary>
    /// Les photos reviennent COCHÉES : elles faisaient partie de la commande. Rouvrir tout
    /// décoché obligerait à re-cocher quinze photos pour en retirer une.
    /// </summary>
    [Fact]
    public void Les_photos_reviennent_cochees()
    {
        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(Ligne("10x15", Article("001.jpg"), Article("002.jpg")))],
            @"C:\photos", "essai");

        Assert.Equal(2, travail.Photos.Count);
        Assert.All(travail.Photos, p => Assert.True(p.Selected));
    }

    /// <summary>
    /// LE MULTI-FORMAT : une même photo tirée en 10×15 ET en 15×20 fait DEUX entrées. C'est
    /// ce que la reprise sait déjà recréer (voir RecreerLesDoublonsEnAttente) ; encore
    /// faut-il les lui donner.
    /// </summary>
    [Fact]
    public void Une_photo_tiree_en_deux_formats_donne_deux_entrees()
    {
        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(
                Ligne("10x15", Article("001.jpg")),
                Ligne("15x20", Article("001.jpg")))],
            @"C:\photos", "essai");

        Assert.Equal(2, travail.Photos.Count);
        Assert.All(travail.Photos, p => Assert.Equal("001.jpg", p.FileName));
        Assert.Contains(travail.Photos, p => p.ProductCode == "10x15");
        Assert.Contains(travail.Photos, p => p.ProductCode == "15x20");
    }

    // — la planche personnalisée —

    /// <summary>
    /// Une planche personnalisée rouverte au format du catalogue remettrait tous les cadres
    /// au centre, au mauvais rapport : le format demandé serait perdu une seconde fois.
    /// </summary>
    [Fact]
    public void Une_planche_personnalisee_rend_sa_taille_et_son_papier()
    {
        var ligne = Ligne("8x10", Article("001.jpg"));
        ligne.CustomCellWidthMm = 70;
        ligne.CustomCellHeightMm = 100;

        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(ligne)], @"C:\photos", "essai");

        Assert.True(travail.EnTaillePersonnalisee);
        Assert.Equal(70, travail.CustomWidthMm, 3);
        Assert.Equal(100, travail.CustomHeightMm, 3);
        Assert.Equal("8x10", travail.PaperCode);
    }

    /// <summary>
    /// ⚠ Sur une planche, le code de la ligne est le PAPIER, pas un format de photo. Le
    /// poser sur la photo lui donnerait le rapport du papier, et le cadrage repris ne
    /// voudrait plus rien dire.
    /// </summary>
    [Fact]
    public void Sur_une_planche_le_papier_ne_devient_pas_le_format_de_la_photo()
    {
        var ligne = Ligne("8x10", Article("001.jpg"));
        ligne.CustomCellWidthMm = 70;
        ligne.CustomCellHeightMm = 100;

        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(ligne)], @"C:\photos", "essai");

        Assert.Null(Assert.Single(travail.Photos).ProductCode);
    }

    // — ce qu'il ne faut pas casser —

    /// <summary>
    /// <b>L'identifiant est NEUF.</b> Rouvrir une commande du jour ne doit effacer ni
    /// modifier aucune commande mise de côté — l'impression efface l'attente qui porte cet
    /// identifiant-là.
    /// </summary>
    [Fact]
    public void L_identifiant_est_neuf_a_chaque_reprise()
    {
        var commande = new[] { Enveloppe(Ligne("10x15", Article("001.jpg"))) };

        var a = TravailDepuisCommande.Traduire(commande, @"C:\photos", "essai");
        var b = TravailDepuisCommande.Traduire(commande, @"C:\photos", "essai");

        Assert.NotEqual(Guid.Empty, a.Id);
        Assert.NotEqual(a.Id, b.Id);
    }

    /// <summary>
    /// Le dossier d'une commande ne contient QUE ses photos, à plat : descendre en dessous
    /// ramènerait les rendus et les fichiers de suivi.
    /// </summary>
    [Fact]
    public void On_ne_descend_pas_sous_le_dossier_de_la_commande()
    {
        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(Ligne("10x15", Article("001.jpg")))], @"C:\photos", "essai");

        Assert.False(travail.AvecSousDossiers);
        Assert.Equal(@"C:\photos", travail.PhotosDirectory);
    }

    /// <summary>
    /// Les réglages sont une COPIE : la reprise ne doit pas modifier la commande enregistrée
    /// sous elle.
    /// </summary>
    [Fact]
    public void Les_corrections_sont_une_copie()
    {
        var article = Article("001.jpg");
        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(Ligne("10x15", article))], @"C:\photos", "essai");

        Assert.Single(travail.Photos).Adjustments.Exposure = 1.9;

        Assert.Equal(0.4, article.Adjustments.Exposure, 4);
    }

    /// <summary>Le produit de la barre : le plus représenté, pour préremplir la liste.</summary>
    [Fact]
    public void Le_produit_par_defaut_est_le_plus_represente()
    {
        var travail = TravailDepuisCommande.Traduire(
            [Enveloppe(
                Ligne("10x15", Article("001.jpg"), Article("002.jpg"), Article("003.jpg")),
                Ligne("15x20", Article("004.jpg")))],
            @"C:\photos", "essai");

        Assert.Equal("10x15", travail.ProduitParDefaut);
    }
}
