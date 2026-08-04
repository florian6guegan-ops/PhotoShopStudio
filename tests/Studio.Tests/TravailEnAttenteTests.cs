using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// Les commandes mises en attente : on met de côté pour servir quelqu'un d'autre, et on
/// reprend là où on en était.
///
/// <b>Toute commande en préparation</b>, quelle qu'en soit l'origine — une clé USB, un
/// téléphone, une borne. La première version ne valait que pour les bornes ; or c'est
/// justement en préparant une commande au comptoir qu'on a besoin de faire autre chose, et
/// l'origine des photos n'y est pour rien.
///
/// Deux règles que ces essais tiennent :
///
/// - <b>les photos sont désignées par leur NOM DE FICHIER</b>, jamais par leur rang. Un
///   fichier illisible est écarté au chargement de la grille : les rangs se décaleraient
///   d'une ouverture à l'autre, et on reprendrait le cadrage du voisin ;
/// - <b>une commande de borne mise de côté meurt avec elle.</b> Ce qui attendrait au nom
///   d'une commande déjà tirée la ferait rouvrir dans un état sans rapport — et retirer
///   deux fois.
/// </summary>
public class TravailEnAttenteTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Attente-" + Guid.NewGuid().ToString("N"));

    public TravailEnAttenteTests() => CreerDiLand();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Depot => Path.Combine(_root, "depot");
    private string Journal => Path.Combine(_root, "diland", "reprises.json");
    private string DossierPhotos => Path.Combine(Depot, "Orders", "20260803-1648-borne.COM", "F");

    private AttenteStore Attente() => new(Path.Combine(_root, "attente"));

    private DiLandImporter Importateur(AttenteStore attente) => new(
        new DiLandRepository(Depot, Path.Combine(_root, "travail")),
        new OrderService(
            new OrderFolderStore(Path.Combine(_root, "commandes")),
            new DailyCounter(Path.Combine(_root, "compteur.json"))),
        [new Product { Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102, Enabled = true }],
        Journal,
        attente);

    private static TravailEnAttente Travail(long? borne = null) => new()
    {
        PhotosDirectory = @"E:\DCIM\100CANON",
        ProduitParDefaut = "10x15",
        KioskOid = borne,
        Titre = "100CANON",
        Resume = "2 photo(s) · 1 cochée(s) · 10x15 · 0,60 €",
        Photos =
        [
            new PhotoEnAttente
            {
                FileName = "photo1.jpg",
                Selected = true,
                Quantity = 4,
                ProductCode = "10x15",
                Finish = "Brillant",
                CropX = 0.05, CropY = 0.10, CropWidth = 0.80, CropHeight = 0.70,
                RotationQuarterTurns = 1,
                FineRotationDegrees = -2.25,
                Fit = FitMode.Fit,
                CutBorder = true,
                Adjustments = new ImageAdjustments { Exposure = 0.45, AutoContrast = true },
            },
            new PhotoEnAttente { FileName = "photo2.jpg", Selected = false, Quantity = 1 },
        ],
    };

    [Fact]
    public void Une_commande_mise_de_cote_se_relit_telle_qu_elle_a_ete_laissee()
    {
        var attente = Attente();
        var travail = Travail();
        attente.Enregistrer(travail);

        var relu = attente.Lire(travail.Id);
        Assert.NotNull(relu);

        Assert.Equal(@"E:\DCIM\100CANON", relu!.PhotosDirectory);
        Assert.Equal("10x15", relu.ProduitParDefaut);
        Assert.Null(relu.KioskOid);

        var photo = relu.Photos.Single(p => p.FileName == "photo1.jpg");
        Assert.True(photo.Selected);
        Assert.Equal(4, photo.Quantity);
        Assert.Equal("10x15", photo.ProductCode);
        Assert.Equal("Brillant", photo.Finish);
        Assert.Equal(1, photo.RotationQuarterTurns);
        Assert.Equal(-2.25, photo.FineRotationDegrees, 3);
        Assert.Equal(FitMode.Fit, photo.Fit);
        Assert.True(photo.CutBorder);
        Assert.Equal(0.45, photo.Adjustments.Exposure, 3);
        Assert.True(photo.Adjustments.AutoContrast);

        Assert.Equal(0.05, photo.Crop.X, 6);
        Assert.Equal(0.10, photo.Crop.Y, 6);
        Assert.Equal(0.80, photo.Crop.Width, 6);
        Assert.Equal(0.70, photo.Crop.Height, 6);
    }

    /// <summary>
    /// Une planche d'identité se met de côté avec TOUT ce que l'opérateur y a posé : la
    /// norme visée, les repères de crâne et de menton, le cadrage, le redressement, les
    /// deux cases, et la photo qu'il regardait.
    ///
    /// C'est le seul travail dont la reprise ne se rattrape pas à la main : refaire les
    /// repères, c'est refaire la photo d'identité.
    /// </summary>
    [Fact]
    public void Une_planche_d_identite_se_relit_avec_ses_reperes()
    {
        var attente = Attente();
        var travail = new TravailEnAttente
        {
            PhotosDirectory = @"E:\DCIM\100CANON",
            Titre = "Identité 35×45",
            Identite = new IdentiteEnAttente
            {
                Country = "France",
                Document = "Passeport",
                WidthMm = 35,
                HeightMm = 45,
                HeadMinMm = 32,
                HeadMaxMm = 36,
                CrownMarginMm = 3,
                PhotoCourante = "visage2.jpg",
                Chemins = [@"E:\DCIM\100CANON\visage1.jpg", @"E:\DCIM\100CANON\visage2.jpg"],
                Photos =
                [
                    new PhotoIdentiteEnAttente
                    {
                        FileName = "visage2.jpg",
                        Selected = true,
                        Quantity = 2,
                        Copies = 8,
                        Prete = true,
                        CropX = 0.12, CropY = 0.05, CropWidth = 0.55, CropHeight = 0.70,
                        CrownX = 0.50, CrownY = 0.11,
                        ChinX = 0.50, ChinY = 0.62,
                        HeadX = 0.34, HeadY = 0.09, HeadWidth = 0.32, HeadHeight = 0.55,
                        AxeVisage = 0.48,
                        Redressement = -1.5,
                        NoirEtBlanc = true,
                        FondBlanc = true,
                        Corrections = new ImageAdjustments { Exposure = 0.3 },
                    },
                ],
            },
        };

        attente.Enregistrer(travail);
        var relu = attente.Lire(travail.Id);

        var identite = relu!.Identite;
        Assert.NotNull(identite);
        Assert.Equal("France", identite!.Country);
        Assert.Equal(45, identite.HeightMm, 3);
        Assert.Equal(3, identite.CrownMarginMm);
        Assert.Equal("visage2.jpg", identite.PhotoCourante);
        Assert.Equal(2, identite.Chemins.Count);

        var photo = identite.Photos.Single();
        Assert.True(photo.Prete);
        Assert.Equal(8, photo.Copies);
        Assert.Equal(0.11, photo.CrownY!.Value, 6);
        Assert.Equal(0.62, photo.ChinY!.Value, 6);
        Assert.Equal(0.32, photo.HeadWidth!.Value, 6);
        Assert.Equal(0.48, photo.AxeVisage, 6);
        Assert.Equal(-1.5, photo.Redressement, 6);
        Assert.True(photo.NoirEtBlanc);
        Assert.True(photo.FondBlanc);
        Assert.Equal(0.3, photo.Corrections.Exposure, 6);
    }

    /// <summary>
    /// Une commande de TIRAGES n'a pas de section identité : c'est ce qui décide dans quel
    /// écran l'accueil la rouvre. Une planche rouverte dans la grille des tirages y
    /// trouverait un cadre libre, sans gabarit ni repères — précisément ce qui ne permet
    /// pas de faire une photo d'identité.
    /// </summary>
    [Fact]
    public void Une_commande_de_tirages_n_a_pas_de_section_identite()
    {
        var attente = Attente();
        var travail = Travail();

        attente.Enregistrer(travail);

        Assert.Null(attente.Lire(travail.Id)!.Identite);
    }

    /// <summary>
    /// Une commande venue d'une clé USB se met de côté comme les autres.
    ///
    /// C'est le point de la refonte : la première version ne savait mettre de côté qu'une
    /// commande de borne, alors que le geste vaut pour toute commande en préparation.
    /// </summary>
    [Fact]
    public void Une_commande_sans_borne_se_met_de_cote_aussi()
    {
        var attente = Attente();
        attente.Enregistrer(Travail());

        var listee = Assert.Single(attente.Lister());
        Assert.Null(listee.KioskOid);
        Assert.Equal("100CANON", listee.Titre);
    }

    /// <summary>
    /// Remettre de côté MET À JOUR la même entrée, elle n'en crée pas une seconde.
    ///
    /// L'écran garde son identifiant pour toute sa vie : sans cela, chaque aller-retour
    /// laisserait un doublon sur l'accueil, et on ne saurait plus laquelle reprendre.
    /// </summary>
    [Fact]
    public void Remettre_de_cote_met_a_jour_au_lieu_d_empiler()
    {
        var attente = Attente();
        var travail = Travail();

        attente.Enregistrer(travail);

        travail.Resume = "2 photo(s) · 2 cochée(s) · 10x15 · 1,20 €";
        attente.Enregistrer(travail);

        var seule = Assert.Single(attente.Lister());
        Assert.Equal("2 photo(s) · 2 cochée(s) · 10x15 · 1,20 €", seule.Resume);
    }

    /// <summary>La plus récemment mise de côté d'abord : c'est celle qu'on reprend le plus souvent.</summary>
    [Fact]
    public void La_plus_recente_est_en_tete()
    {
        var attente = Attente();

        var ancienne = Travail();
        ancienne.Titre = "ancienne";
        ancienne.SavedAt = DateTimeOffset.Now - TimeSpan.FromHours(3);
        attente.Enregistrer(ancienne);

        var recente = Travail();
        recente.Titre = "récente";
        attente.Enregistrer(recente);

        Assert.Equal(["récente", "ancienne"], attente.Lister().Select(t => t.Titre));
    }

    /// <summary>
    /// La taille personnalisée fait partie de la mise en attente.
    ///
    /// Un travail fait en 5,5 × 8 doit rouvrir dans cette taille : le rouvrir au format du
    /// catalogue remettrait tous les cadres au centre, au mauvais rapport.
    /// </summary>
    [Fact]
    public void La_taille_personnalisee_est_reprise()
    {
        var attente = Attente();
        var travail = Travail();
        travail.CustomWidthMm = 55;
        travail.CustomHeightMm = 80;
        travail.PaperCode = "10x15";
        attente.Enregistrer(travail);

        var relu = attente.Lire(travail.Id)!;
        Assert.True(relu.EnTaillePersonnalisee);
        Assert.Equal(55, relu.CustomWidthMm, 3);
        Assert.Equal(80, relu.CustomHeightMm, 3);
        Assert.Equal("10x15", relu.PaperCode);
    }

    /// <summary>Rien en attente : la liste est vide, et non pleine d'entrées fantômes.</summary>
    [Fact]
    public void Sans_rien_en_attente_la_liste_est_vide()
    {
        Assert.Empty(Attente().Lister());
        Assert.Null(Attente().Lire(Guid.NewGuid()));
    }

    /// <summary>
    /// Un fichier abîmé s'efface au lieu de bloquer : l'accueil doit s'afficher, quitte à
    /// perdre une mise de côté. Bloquer le comptoir sur un fichier de confort serait pire.
    /// </summary>
    [Fact]
    public void Un_fichier_abime_ne_bloque_pas_l_accueil()
    {
        var attente = Attente();
        attente.Enregistrer(Travail());

        var chemin = Directory.GetFiles(Path.Combine(_root, "attente"), "*.json").Single();
        File.WriteAllText(chemin, "{ ceci n'est pas du JSON");

        Assert.Empty(attente.Lister());
        Assert.False(File.Exists(chemin));
    }

    /// <summary>Passé la rétention, une commande jamais reprise s'efface d'elle-même.</summary>
    [Fact]
    public void Au_dela_de_la_retention_l_attente_s_efface()
    {
        var attente = Attente();

        var vieille = Travail();
        vieille.SavedAt = DateTimeOffset.Now - AttenteStore.Retention - TimeSpan.FromDays(1);
        attente.Enregistrer(vieille);

        Assert.Empty(attente.Lister());
        Assert.Null(attente.Lire(vieille.Id));
    }

    /// <summary>Avant l'échéance, rien ne bouge.</summary>
    [Fact]
    public void Avant_l_echeance_l_attente_reste()
    {
        var attente = Attente();

        var travail = Travail();
        travail.SavedAt = DateTimeOffset.Now - AttenteStore.Retention + TimeSpan.FromDays(1);
        attente.Enregistrer(travail);

        Assert.Single(attente.Lister());
    }

    /// <summary>Le tirage est sorti : ce qui attendait au nom de la borne n'a plus d'objet.</summary>
    [Fact]
    public void L_attente_d_une_borne_disparait_a_l_impression()
    {
        var attente = Attente();
        var importateur = Importateur(attente);
        var borne = importateur.Pending().Single();

        importateur.MarkInProgress(borne);
        attente.Enregistrer(Travail(borne.Oid));
        Assert.NotNull(attente.PourLaBorne(borne.Oid));

        importateur.MarkPrinted(borne.Oid);

        Assert.Null(attente.PourLaBorne(borne.Oid));
        Assert.Empty(attente.Lister());
    }

    /// <summary>Retrait à la main : même règle, la commande est close.</summary>
    [Fact]
    public void L_attente_d_une_borne_disparait_au_retrait()
    {
        var attente = Attente();
        var importateur = Importateur(attente);
        var borne = importateur.Pending().Single();

        attente.Enregistrer(Travail(borne.Oid));
        importateur.Dismiss(borne);

        Assert.Null(attente.PourLaBorne(borne.Oid));
    }

    /// <summary>
    /// Une commande de borne close n'emporte QUE ce qui attend en son nom.
    ///
    /// Une commande du comptoir mise de côté au même moment n'a rien à voir avec elle, et
    /// la perdre parce qu'une borne a été tirée serait incompréhensible.
    /// </summary>
    [Fact]
    public void Fermer_une_borne_n_emporte_pas_les_autres_attentes()
    {
        var attente = Attente();
        var importateur = Importateur(attente);
        var borne = importateur.Pending().Single();

        attente.Enregistrer(Travail(borne.Oid));
        var duComptoir = Travail();
        attente.Enregistrer(duComptoir);

        importateur.MarkPrinted(borne.Oid);

        var restante = Assert.Single(attente.Lister());
        Assert.Equal(duComptoir.Id, restante.Id);
    }

    /// <summary>
    /// Une photo citée mais disparue du dossier se relit sans erreur : c'est l'écran qui
    /// l'ignore au chargement. Les deux listes n'ont aucune raison de coïncider un mois
    /// plus tard.
    /// </summary>
    [Fact]
    public void Une_photo_disparue_ne_fait_pas_echouer_la_relecture()
    {
        var attente = Attente();
        var travail = Travail();
        travail.Photos.Add(new PhotoEnAttente { FileName = "jamais-arrivee.jpg", Quantity = 2 });
        attente.Enregistrer(travail);

        var relu = attente.Lire(travail.Id);

        Assert.NotNull(relu);
        Assert.Equal(3, relu!.Photos.Count);
    }

    private void CreerDiLand()
    {
        Directory.CreateDirectory(DossierPhotos);
        foreach (var nom in new[] { "photo1.jpg", "photo2.jpg" })
            File.WriteAllBytes(Path.Combine(DossierPhotos, nom), [0xFF, 0xD8, 0xFF, 0xE0]);

        using var connexion = new SqliteConnection(
            $"Data Source={Path.Combine(Depot, "Database.db")}");
        connexion.Open();

        using var creation = connexion.CreateCommand();
        creation.CommandText = """
            CREATE TABLE "Order" (
                Oid INTEGER PRIMARY KEY, Number INTEGER, DailyNumber TEXT, Date TEXT,
                DirectoryName TEXT, EndUserName TEXT, GCRecord INTEGER);
            CREATE TABLE Product (Oid INTEGER PRIMARY KEY, Name TEXT, GCRecord INTEGER);
            CREATE TABLE OrderLine (
                Oid INTEGER PRIMARY KEY, "Order" INTEGER, Product INTEGER,
                Description TEXT, Price REAL, GCRecord INTEGER);
            CREATE TABLE OrderLineImage (
                Oid INTEGER PRIMARY KEY, OrderLine INTEGER, FileName TEXT,
                OriginalFileName TEXT, Quantity INTEGER, ApplyCrop INTEGER,
                CropX REAL, CropY REAL, CropWidth REAL, CropHeight REAL,
                Angle REAL, FineRotationAngle REAL, Width INTEGER, Height INTEGER,
                GCRecord INTEGER);

            INSERT INTO Product (Oid, Name) VALUES (1, '10x15');

            INSERT INTO "Order" (Oid, Number, DailyNumber, Date, DirectoryName, EndUserName, GCRecord)
            VALUES (10, 6878, '03-001', '2026-08-03 16:48:12', '20260803-1648-borne.COM', 'YU', NULL);

            INSERT INTO OrderLine (Oid, "Order", Product, Description, Price, GCRecord)
            VALUES (500, 10, 1, '', 0.60, NULL);

            INSERT INTO OrderLineImage
                (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                 CropX, CropY, CropWidth, CropHeight, Angle, FineRotationAngle,
                 Width, Height, GCRecord)
            VALUES (900, 500, 'photo1.jpg', '', 1, 0, 0, 0, 1536, 2048, 0, 0, 1536, 2048, NULL),
                   (901, 500, 'photo2.jpg', '', 1, 0, 0, 0, 1536, 2048, 0, 0, 1536, 2048, NULL);
            """;
        creation.ExecuteNonQuery();
    }
}
