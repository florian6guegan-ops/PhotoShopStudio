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

    // — les planches d'identité —

    private static IdentiteEnAttente Norme() => new()
    {
        Country = "France", Document = "Passeport / CNI",
        WidthMm = 35, HeightMm = 45, HeadMinMm = 32, HeadMaxMm = 36,
    };

    /// <summary>
    /// Le seul motif de rouvrir une planche est que le guichet a REFUSÉ le cadrage : il faut
    /// donc retrouver celui qu'on avait, pas un cadrage automatique tout neuf.
    /// </summary>
    [Fact]
    public void Une_planche_rend_son_cadrage_ses_fonds_et_ses_quantites()
    {
        var article = Article("001.jpg", new CropSpec(0.2, 0.05, 0.5, 0.7));
        article.SheetCopiesOverride = 6;
        article.Adjustments.Grayscale = true;
        article.Adjustments.GrayBackground = true;

        var travail = TravailDepuisCommande.TraduireIdentite(
            [article], @"C:\photos", "planche", Norme());

        var photo = Assert.Single(travail.Identite!.Photos);
        Assert.Equal(0.2, photo.CropX, 4);
        Assert.Equal(0.7, photo.CropHeight, 4);
        Assert.Equal(6, photo.Copies);
        Assert.Equal(2, photo.Quantity);
        Assert.Equal(1.5, photo.Redressement, 4);
        Assert.True(photo.NoirEtBlanc);
        Assert.True(photo.FondGris);
        Assert.False(photo.FondBlanc);
    }

    /// <summary>
    /// Une commande écrite AVANT que la commande ne garde les repères n'en a aucun : la
    /// photo revient « pas prête », pour que la détection les retrouve — et l'écran, lui,
    /// ne recadre pas par-dessus un cadrage repris.
    /// </summary>
    [Fact]
    public void Une_planche_sans_reperes_revient_pas_prete_pour_que_la_detection_les_retrouve()
    {
        var travail = TravailDepuisCommande.TraduireIdentite(
            [Article("001.jpg")], @"C:\photos", "planche", Norme());

        Assert.False(Assert.Single(travail.Identite!.Photos).Prete);
    }

    /// <summary>
    /// <b>Les repères reviennent de la commande, et la photo est alors PRÊTE.</b>
    ///
    /// C'est ce qui fait de « Commandes du jour › Photos d'identité » un historique dont on
    /// rouvre une planche telle qu'elle est SORTIE : sans eux, la détection de visage
    /// reposait les siens, et la mesure de la tête pouvait tomber ailleurs que sur le papier
    /// qu'on vient chercher — alors qu'on la rouvre justement parce que le guichet a refusé
    /// le cadrage.
    /// </summary>
    [Fact]
    public void Une_planche_rend_les_reperes_du_visage_et_revient_prete()
    {
        var article = Article("001.jpg");
        article.Reperes = new ReperesIdentite
        {
            CrownX = 0.51, CrownY = 0.12,
            ChinX = 0.50, ChinY = 0.62,
            HeadX = 0.34, HeadY = 0.10, HeadWidth = 0.33, HeadHeight = 0.54,
            AxeVisage = 0.47,
        };

        var travail = TravailDepuisCommande.TraduireIdentite(
            [article], @"C:\photos", "planche", Norme());

        var photo = Assert.Single(travail.Identite!.Photos);

        Assert.True(photo.Prete);
        Assert.Equal(0.51, photo.CrownX!.Value, 4);
        Assert.Equal(0.12, photo.CrownY!.Value, 4);
        Assert.Equal(0.50, photo.ChinX!.Value, 4);
        Assert.Equal(0.62, photo.ChinY!.Value, 4);
        Assert.Equal(0.34, photo.HeadX!.Value, 4);
        Assert.Equal(0.54, photo.HeadHeight!.Value, 4);
        Assert.Equal(0.47, photo.AxeVisage, 4);
    }

    /// <summary>
    /// Un objet de repères VIDE ne vaut pas des repères : il ne dit rien de plus que son
    /// absence, et laisser la photo « prête » figerait un placement que personne n'a posé.
    /// </summary>
    [Fact]
    public void Des_reperes_vides_ne_rendent_pas_la_photo_prete()
    {
        var article = Article("001.jpg");
        article.Reperes = new ReperesIdentite();

        var travail = TravailDepuisCommande.TraduireIdentite(
            [article], @"C:\photos", "planche", Norme());

        Assert.False(Assert.Single(travail.Identite!.Photos).Prete);
    }

    /// <summary>
    /// L'axe du visage revient à SON milieu quand la commande n'en porte pas : c'est la
    /// valeur neutre, et non zéro — qui collerait le visage au bord gauche.
    /// </summary>
    [Fact]
    public void Sans_reperes_l_axe_du_visage_revient_au_milieu()
    {
        var travail = TravailDepuisCommande.TraduireIdentite(
            [Article("001.jpg")], @"C:\photos", "planche", Norme());

        Assert.Equal(0.5, Assert.Single(travail.Identite!.Photos).AxeVisage, 4);
    }

    /// <summary>La norme visée revient telle quelle : c'est elle qui fixe la case et le prix.</summary>
    [Fact]
    public void La_norme_visee_est_conservee()
    {
        var travail = TravailDepuisCommande.TraduireIdentite(
            [Article("001.jpg")], @"C:\photos", "planche", Norme());

        Assert.Equal("France", travail.Identite!.Country);
        Assert.Equal(35, travail.Identite.WidthMm, 3);
        Assert.Equal(45, travail.Identite.HeightMm, 3);
        Assert.Equal(36, travail.Identite.HeadMaxMm, 3);
        Assert.False(travail.AvecSousDossiers);
    }

    /// <summary>Sans nombre de photos enregistré, on repart de la planche PLEINE (zéro).</summary>
    [Fact]
    public void Sans_nombre_enregistre_la_planche_repart_pleine()
    {
        var article = Article("001.jpg");
        article.SheetCopiesOverride = null;

        var travail = TravailDepuisCommande.TraduireIdentite(
            [article], @"C:\photos", "planche", Norme());

        Assert.Equal(0, Assert.Single(travail.Identite!.Photos).Copies);
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

    // — l'avertissement « photos non conformes » —

    /// <summary>
    /// <b>C'est au retour du guichet qu'il sert.</b> On rouvre une planche d'identité
    /// surtout quand la mairie l'a refusée ; une série d'école qui repartirait alors en
    /// « PHOTOS CONFORMES » aurait perdu l'avertissement exactement au moment qui compte.
    /// </summary>
    [Fact]
    public void Une_planche_hors_norme_le_reste_a_la_reouverture()
    {
        var article = Article("001.jpg");
        article.PhotosNonConformes = true;

        var travail = TravailDepuisCommande.TraduireIdentite(
            [article], @"C:\photos", "planche", Norme());

        Assert.True(Assert.Single(travail.Identite!.Photos).NonConforme);
    }

    /// <summary>
    /// Le défaut ne bouge pas : une planche ordinaire, et toutes les commandes écrites avant
    /// que ce champ n'existe, reviennent conformes.
    /// </summary>
    [Fact]
    public void Une_planche_ordinaire_revient_conforme()
    {
        var travail = TravailDepuisCommande.TraduireIdentite(
            [Article("001.jpg")], @"C:\photos", "planche", Norme());

        Assert.False(Assert.Single(travail.Identite!.Photos).NonConforme);
    }
}
