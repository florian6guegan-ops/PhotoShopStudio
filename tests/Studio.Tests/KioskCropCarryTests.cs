using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// Le cadrage que le CLIENT a validé à la borne survit à l'ouverture de la commande.
///
/// <b>Le bug.</b> La 5ᵉ passe a corrigé la lecture des recadrages de DiLand (des pixels
/// lus comme des fractions), mais cette correction ne servait qu'un seul des deux chemins.
/// « Reprendre » fabrique des <c>DraftItem</c> qui portent le recadrage ; « Modifier »,
/// lui, recopie les FICHIERS puis rescanne le dossier — l'écran ne voyait donc que des
/// images, et le recadrage, les rotations et les quantités du client disparaissaient à
/// l'ouverture. Or « Modifier » est le chemin le plus utilisé des deux : c'est celui qu'on
/// prend pour contrôler la commande avant d'engager du papier.
///
/// Les valeurs sont celles relevées sur la base de la boutique le 03/08/2026 : image
/// 1536 × 2048, recadrage <c>X=0 Y=44 W=1536 H=1958</c>, soit un rapport 0,784 — le 8x10
/// commandé.
///
/// L'ORDRE dans lequel l'écran repose ces valeurs sur une vignette est l'autre moitié du
/// correctif ; il vit dans <c>PhotoGridView.AppliquerLeCadrageDeLaBorne</c> et n'est pas
/// couvert ici — le projet d'interface n'est pas référencé par les essais.
/// </summary>
public class KioskCropCarryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "KioskCrop-" + Guid.NewGuid().ToString("N"));

    public KioskCropCarryTests() => CreerDiLand();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Depot => Path.Combine(_root, "depot");
    private string DossierPhotos => Path.Combine(Depot, "Orders", "20260803-1648-borne.COM", "F");

    private static readonly Product Dix15 = new()
    {
        Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102, Enabled = true,
    };

    private static readonly Product Treize18 = new()
    {
        Code = "13x18", Name = "13x18", WidthMm = 178, HeightMm = 127, Enabled = true,
    };

    private DiLandImporter Importateur() => new(
        new DiLandRepository(Depot, Path.Combine(_root, "travail")),
        new OrderService(
            new OrderFolderStore(Path.Combine(_root, "commandes")),
            new DailyCounter(Path.Combine(_root, "compteur.json"))),
        [Dix15, Treize18],
        Path.Combine(_root, "diland", "reprises.json"));

    private DiLandImporter.StagedOrder Preparer()
    {
        var importateur = Importateur();
        return importateur.Archiver(importateur.Pending().Single());
    }

    /// <summary>
    /// Le contrôle qui compte : le recadrage du client arrive dans l'écran des photos,
    /// ramené en fractions de l'image.
    /// </summary>
    [Fact]
    public void Le_recadrage_du_client_arrive_dans_l_ecran_des_photos()
    {
        var cadrage = Preparer().Cadrages["recadree.jpg"];

        Assert.Equal(0, cadrage.Crop.X, 6);
        Assert.Equal(44 / 2048.0, cadrage.Crop.Y, 6);
        Assert.Equal(1, cadrage.Crop.Width, 6);
        Assert.Equal(1958 / 2048.0, cadrage.Crop.Height, 6);

        Assert.True(cadrage.Crop.IsValid);
        Assert.False(cadrage.Crop.IsFull);
    }

    /// <summary>
    /// Le redressement et le quart de tour suivent le recadrage.
    ///
    /// Ils font partie du même geste : une photo redressée de deux degrés et recadrée
    /// dessus n'a de sens que si les deux arrivent ensemble.
    /// </summary>
    [Fact]
    public void Le_quart_de_tour_et_le_redressement_suivent()
    {
        var cadrage = Preparer().Cadrages["tournee.jpg"];

        Assert.Equal(1, cadrage.QuartsDeTour);        // 90° dans DiLand
        Assert.Equal(-1.75, cadrage.RedressementDegres, 3);
    }

    /// <summary>La quantité commandée par le client est reprise (décision du 03/08/2026).</summary>
    [Fact]
    public void La_quantite_commandee_est_reprise()
    {
        Assert.Equal(3, Preparer().Cadrages["recadree.jpg"].Quantite);
    }

    /// <summary>
    /// Le produit vient de LA LIGNE, pas du produit majoritaire de la commande.
    ///
    /// Trois photos en 10x15 et une en 13x18 : poser le 10x15 sur tout le monde ferait
    /// tirer la quatrième au mauvais format, et il n'y aurait rien à l'écran pour le dire.
    /// </summary>
    [Fact]
    public void Chaque_photo_porte_le_produit_de_sa_ligne()
    {
        var cadrages = Preparer().Cadrages;

        Assert.Equal("10x15", cadrages["recadree.jpg"].CodeProduit);
        Assert.Equal("13x18", cadrages["grande.jpg"].CodeProduit);
    }

    /// <summary>
    /// Une photo commandée sur DEUX lignes n'apparaît qu'une fois dans la grille : elle ne
    /// peut donc porter qu'un cadrage, et c'est celui de la première ligne — la même règle
    /// que la recopie des fichiers.
    /// </summary>
    [Fact]
    public void Une_photo_sur_deux_lignes_n_est_cadree_qu_une_fois()
    {
        var cadrages = Preparer().Cadrages;

        // « partagee.jpg » figure en 10x15 (ligne 500) et en 13x18 (ligne 501)
        Assert.Equal("10x15", cadrages["partagee.jpg"].CodeProduit);
    }

    /// <summary>
    /// Un rectangle incohérent retombe sur l'image entière, pour CETTE photo seulement.
    ///
    /// Mieux vaut une photo cadrée au centre qu'une ouverture qui échoue devant le client,
    /// ou qu'un tirage faux qu'on ne découvre que sur le papier.
    /// </summary>
    [Fact]
    public void Un_recadrage_absurde_retombe_sur_l_image_entiere()
    {
        var cadrages = Preparer().Cadrages;

        Assert.True(cadrages["absurde.jpg"].Crop.IsFull);

        // et les autres n'en souffrent pas
        Assert.False(cadrages["recadree.jpg"].Crop.IsFull);
    }

    /// <summary>
    /// « Reprendre » et « Modifier » lisent le MÊME recadrage.
    ///
    /// C'est la régression qu'on veut interdire : deux conversions finiraient par diverger,
    /// et le même bouton ne tirerait plus la même chose selon l'écran par lequel on passe.
    /// </summary>
    [Fact]
    public void Reprendre_et_Modifier_lisent_le_meme_recadrage()
    {
        var importateur = Importateur();
        var commande = importateur.Pending().Single();

        var parModifier = importateur.Archiver(commande).Cadrages["recadree.jpg"];

        var creee = importateur.Import(commande).Created;
        Assert.NotNull(creee);

        var parReprendre = creee!.Envelopes
            .SelectMany(e => e.Lines)
            .Where(l => l.ProductCode == "10x15")
            .SelectMany(l => l.Items)
            // OrderItem.FileName est le nom DANS la commande, renuméroté à la copie :
            // c'est OriginalName qui garde celui du fichier d'origine
            .Single(i => i.OriginalName.Equals("recadree.jpg", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(parModifier.Crop.X, parReprendre.Crop.X, 6);
        Assert.Equal(parModifier.Crop.Y, parReprendre.Crop.Y, 6);
        Assert.Equal(parModifier.Crop.Width, parReprendre.Crop.Width, 6);
        Assert.Equal(parModifier.Crop.Height, parReprendre.Crop.Height, 6);
    }

    /// <summary>
    /// Une commande dont DiLand ne connaît plus le contenu s'ouvre quand même, sans
    /// cadrage : il n'y a alors plus rien à reprendre, et un écran vide serait pire.
    /// </summary>
    [Fact]
    public void Sans_contenu_en_base_la_commande_s_ouvre_sans_cadrage()
    {
        using (var connexion = new SqliteConnection($"Data Source={Path.Combine(Depot, "Database.db")}"))
        {
            connexion.Open();
            using var vidage = connexion.CreateCommand();
            vidage.CommandText = "DELETE FROM OrderLineImage; DELETE FROM OrderLine;";
            vidage.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var prete = Preparer();

        Assert.True(prete.PhotoCount > 0);   // les fichiers sont là
        Assert.Empty(prete.Cadrages);        // le contenu, non
    }

    private void CreerDiLand()
    {
        Directory.CreateDirectory(DossierPhotos);
        foreach (var nom in new[]
                 { "recadree.jpg", "tournee.jpg", "grande.jpg", "partagee.jpg", "absurde.jpg" })
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

            INSERT INTO Product (Oid, Name) VALUES (1, '10x15'), (2, '13x18');

            INSERT INTO "Order" (Oid, Number, DailyNumber, Date, DirectoryName, EndUserName, GCRecord)
            VALUES (10, 6878, '03-001', '2026-08-03 16:48:12', '20260803-1648-borne.COM', 'YU', NULL);

            INSERT INTO OrderLine (Oid, "Order", Product, Description, Price, GCRecord)
            VALUES (500, 10, 1, '', 0.60, NULL),
                   (501, 10, 2, '', 1.50, NULL);

            INSERT INTO OrderLineImage
                (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                 CropX, CropY, CropWidth, CropHeight, Angle, FineRotationAngle,
                 Width, Height, GCRecord)
            VALUES
                -- le cas réel de la boutique : un recadrage EN PIXELS
                (900, 500, 'recadree.jpg', 'IMG_1.jpg', 3, 1, 0, 44, 1536, 1958, 0, 0, 1536, 2048, NULL),
                -- quart de tour et redressement fin
                (901, 500, 'tournee.jpg',  'IMG_2.jpg', 1, 0, 0, 0, 1536, 2048, 90, -1.75, 1536, 2048, NULL),
                -- la même photo sur les deux lignes : la première doit gagner
                (902, 500, 'partagee.jpg', 'IMG_3.jpg', 1, 0, 0, 0, 1536, 2048, 0, 0, 1536, 2048, NULL),
                -- un rectangle qui déborde de l'image : à ignorer sans rien casser
                (903, 500, 'absurde.jpg',  'IMG_4.jpg', 1, 1, 0, 0, 9000, 9000, 0, 0, 1536, 2048, NULL),
                (904, 501, 'grande.jpg',   'IMG_5.jpg', 1, 0, 0, 0, 1536, 2048, 0, 0, 1536, 2048, NULL),
                (905, 501, 'partagee.jpg', 'IMG_3.jpg', 1, 0, 0, 0, 1536, 2048, 0, 0, 1536, 2048, NULL);
            """;
        creation.ExecuteNonQuery();
    }
}
