using Microsoft.Data.Sqlite;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// Lecture des commandes déposées par les bornes dans DiLand.
///
/// L'exigence tient en une phrase : récupérer les commandes SANS priver DiLand des
/// siennes. Ces tests vérifient qu'on ne touche jamais à sa base ni à ses dossiers.
/// </summary>
public class DiLandRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DiLandTest-" + Guid.NewGuid().ToString("N"));
    private readonly string _travail;

    public DiLandRepositoryTests()
    {
        _travail = Path.Combine(_root, "travail");
        Directory.CreateDirectory(Path.Combine(_root, "depot", "Orders"));
        Directory.CreateDirectory(_travail);
        CreerBaseFactice();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Depot => Path.Combine(_root, "depot");

    private DiLandRepository Depot_() => new(Depot, _travail);

    /// <summary>Reproduit la structure de DiLand : table Order et dossiers de photos.</summary>
    private void CreerBaseFactice()
    {
        using var connexion = new SqliteConnection($"Data Source={Path.Combine(Depot, "Database.db")}");
        connexion.Open();

        using (var creation = connexion.CreateCommand())
        {
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
                """;
            creation.ExecuteNonQuery();
        }

        Ajouter(connexion, 10, 36001, "31-001", "2026-07-31 10:00:00", ComptoirDir, null);
        Ajouter(connexion, 11, 36002, "31-002", "2026-07-31 11:00:00", BorneDir, null);
        // commande supprimée logiquement : DiLand l'ignore, nous aussi
        Ajouter(connexion, 12, 36003, "31-003", "2026-07-31 12:00:00", "20260731-1200-cccc", 1);

        AjouterContenu(connexion);

        foreach (var dossier in new[] { ComptoirDir, BorneDir })
        {
            var photos = Path.Combine(Depot, "Orders", dossier, "F");
            Directory.CreateDirectory(photos);
            File.WriteAllText(Path.Combine(photos, "photo1.jpg"), "x");
            File.WriteAllText(Path.Combine(photos, "photo2.jpg"), "x");
            // dérivé de DiLand : ne doit pas être compté comme une photo client
            File.WriteAllText(Path.Combine(photos, "O_photo1.jpg"), "x");
        }
    }

    /// <summary>Nom de dossier d'une commande du comptoir : pas de suffixe.</summary>
    private const string ComptoirDir = "20260731-1000-aaaa";

    /// <summary>Nom de dossier d'une commande de borne : DiLand suffixe en .COM.</summary>
    private const string BorneDir = "20260731-1100-bbbb.COM";

    /// <summary>
    /// Une ligne « 10x15 » avec deux photos, dont une commandée en double : c'est la forme
    /// qu'ont réellement les commandes de bornes.
    /// </summary>
    private static void AjouterContenu(SqliteConnection c)
    {
        using var commande = c.CreateCommand();
        commande.CommandText = """
            INSERT INTO OrderLine (Oid, "Order", Product, Description, Price, GCRecord)
            VALUES (500, 11, 1, '', 1.5, NULL);
            INSERT INTO OrderLineImage
                (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                 CropX, CropY, CropWidth, CropHeight, Angle, GCRecord)
            VALUES (900, 500, 'photo1.jpg', 'IMG_0143.jpeg', 2, 1, 0.1, 0.2, 0.8, 0.7, 90, NULL),
                   (901, 500, 'photo2.jpg', '', 1, 0, 0, 0, 1, 1, 0, NULL),
                   (902, 500, 'photo3.jpg', 'IMG_0999.jpeg', 1, 0, 0, 0, 1, 1, 0, 1);
            """;
        commande.ExecuteNonQuery();
    }

    private static void Ajouter(SqliteConnection c, long oid, int numero, string jour,
        string date, string dossier, int? gcRecord)
    {
        using var commande = c.CreateCommand();
        commande.CommandText = """
            INSERT INTO "Order" (Oid, Number, DailyNumber, Date, DirectoryName, EndUserName, GCRecord)
            VALUES ($oid, $num, $jour, $date, $dir, '', $gc)
            """;
        commande.Parameters.AddWithValue("$oid", oid);
        commande.Parameters.AddWithValue("$num", numero);
        commande.Parameters.AddWithValue("$jour", jour);
        commande.Parameters.AddWithValue("$date", date);
        commande.Parameters.AddWithValue("$dir", dossier);
        commande.Parameters.AddWithValue("$gc", gcRecord.HasValue ? gcRecord.Value : DBNull.Value);
        commande.ExecuteNonQuery();
    }

    // — sûreté : on ne touche pas à DiLand —

    /// <summary>
    /// Le point capital : la base de DiLand n'est jamais ouverte. On lit une copie, donc
    /// aucun verrou ne peut faire attendre ses écritures.
    /// </summary>
    [Fact]
    public void La_base_de_DiLand_n_est_jamais_ouverte()
    {
        var depot = Depot_();
        var base_ = new FileInfo(depot.DatabasePath);
        var avant = (base_.LastWriteTimeUtc, base_.Length);

        depot.RefreshSnapshot();
        depot.ReadOrdersAfter(0);

        base_.Refresh();
        Assert.Equal(avant, (base_.LastWriteTimeUtc, base_.Length));
        Assert.False(File.Exists(depot.DatabasePath + "-wal"), "aucun journal ne doit apparaître");
        Assert.False(File.Exists(depot.DatabasePath + "-shm"), "aucune mémoire partagée ne doit apparaître");
    }

    /// <summary>Les photos de DiLand doivent rester à leur place : il en a besoin pour tirer.</summary>
    [Fact]
    public void Les_photos_de_DiLand_ne_sont_ni_deplacees_ni_supprimees()
    {
        var depot = Depot_();
        var dossier = Path.Combine(Depot, "Orders", "20260731-1000-aaaa", "F");
        var avant = Directory.GetFiles(dossier).OrderBy(f => f).ToList();

        depot.RefreshSnapshot();
        foreach (var commande in depot.ReadOrdersAfter(0))
            depot.PhotosOf(commande);

        Assert.Equal(avant, Directory.GetFiles(dossier).OrderBy(f => f).ToList());
    }

    // — lecture —

    [Fact]
    public void Les_commandes_sont_lues_de_la_plus_ancienne_a_la_plus_recente()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();

        var commandes = depot.ReadOrdersAfter(0);

        Assert.Equal([36001, 36002], commandes.Select(c => c.Number));
    }

    [Fact]
    public void Les_commandes_supprimees_sont_ignorees()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();

        Assert.DoesNotContain(depot.ReadOrdersAfter(0), c => c.Number == 36003);
    }

    /// <summary>Le curseur évite de tout relire à chaque passage.</summary>
    [Fact]
    public void Le_curseur_ne_renvoie_que_les_nouvelles_commandes()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();

        var premieres = depot.ReadOrdersAfter(0);
        var suivantes = depot.ReadOrdersAfter(premieres.Max(c => c.Oid));

        Assert.NotEmpty(premieres);
        Assert.Empty(suivantes);
    }

    [Fact]
    public void Les_derives_de_DiLand_ne_sont_pas_comptes_comme_des_photos()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();

        var commande = depot.ReadOrdersAfter(0).First();
        var photos = depot.PhotosOf(commande);

        Assert.Equal(2, commande.PhotoCount);
        Assert.Equal(2, photos.Count);
        Assert.DoesNotContain(photos, p => Path.GetFileName(p).StartsWith("O_"));
    }

    /// <summary>
    /// Les commandes de bornes sont celles que la boutique veut récupérer. DiLand suffixe
    /// leur dossier en .COM après les avoir intégrées depuis IncomingOrders.
    /// </summary>
    [Fact]
    public void Les_commandes_de_bornes_sont_reconnues()
    {
        var borne = new DiLandOrder(1, 100, "31-001", DateTime.Now, "20260731-1620-mw2c1jd5.COM", "", 2);
        var comptoir = new DiLandOrder(2, 101, "31-002", DateTime.Now, "20260731-1108-gvqxmcpq", "", 3);

        Assert.True(borne.IsFromKiosk);
        Assert.False(comptoir.IsFromKiosk);
        Assert.Contains("borne", borne.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Une borne dépose parfois une commande en cours : sans numéro ni photo.</summary>
    [Fact]
    public void Une_commande_incomplete_est_reconnue()
    {
        var brouillon = new DiLandOrder(1, 0, "", DateTime.Now, "20260731-1605-bfbh0gr0", "", 0);
        var complete = new DiLandOrder(2, 36136, "31-022", DateTime.Now, "20260731-1627-a2obewaa", "", 1);

        Assert.False(brouillon.IsComplete);
        Assert.True(complete.IsComplete);
    }

    [Fact]
    public void Un_depot_absent_ne_fait_pas_echouer_la_lecture()
    {
        var depot = new DiLandRepository(Path.Combine(_root, "nexiste-pas"), _travail);

        Assert.False(depot.IsAvailable);
        Assert.False(depot.RefreshSnapshot());
        Assert.Empty(depot.ReadOrdersAfter(0));
    }

    // — ce que la boutique veut récupérer : les bornes, et rien d'autre —

    /// <summary>
    /// La demande est explicite : seules les commandes de bornes intéressent la boutique.
    /// Celles du comptoir sont déjà saisies chez nous, les reprendre ferait doublon.
    /// </summary>
    [Fact]
    public void Seules_les_commandes_de_bornes_sont_recuperees()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();

        var bornes = depot.ReadKioskOrdersAfter(0);

        Assert.Equal([36002], bornes.Select(c => c.Number));
    }

    /// <summary>Le contenu doit être repris tel quel : le produit et le nombre de tirages.</summary>
    [Fact]
    public void Le_produit_et_le_nombre_de_tirages_sont_repris()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();
        var borne = depot.ReadKioskOrdersAfter(0).Single();

        var ligne = depot.LinesOf(borne).Single();

        Assert.Equal("10x15", ligne.ProductName);
        Assert.Equal(1.5m, ligne.Price);
    }

    /// <summary>
    /// Le nombre de tirages est la somme des quantités, pas le nombre de photos : deux
    /// exemplaires d'une même photo comptent pour deux.
    /// </summary>
    [Fact]
    public void Une_photo_commandee_en_double_compte_pour_deux()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();
        var borne = depot.ReadKioskOrdersAfter(0).Single();

        var ligne = depot.LinesOf(borne).Single();

        Assert.Equal(2, ligne.Photos.Count);
        Assert.Equal(3, ligne.PrintCount);
    }

    /// <summary>Le recadrage fait à la borne doit suivre, sinon le tirage n'est pas le bon.</summary>
    [Fact]
    public void Le_recadrage_fait_a_la_borne_est_conserve()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();
        var borne = depot.ReadKioskOrdersAfter(0).Single();

        var photo = depot.LinesOf(borne).Single().Photos[0];

        Assert.True(photo.ApplyCrop);
        Assert.Equal(0.1, photo.CropX, 3);
        Assert.Equal(0.8, photo.CropWidth, 3);
        Assert.Equal(90, photo.Angle, 3);
    }

    [Fact]
    public void Une_photo_supprimee_de_la_commande_est_ignoree()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();
        var borne = depot.ReadKioskOrdersAfter(0).Single();

        var photos = depot.LinesOf(borne).Single().Photos;

        Assert.DoesNotContain(photos, p => p.FileName == "photo3.jpg");
    }

    /// <summary>Le fichier stocké est un identifiant illisible ; l'opérateur a besoin du nom du client.</summary>
    [Fact]
    public void Le_nom_d_origine_du_client_est_affiche_quand_il_existe()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();
        var photos = depot.LinesOf(depot.ReadKioskOrdersAfter(0).Single()).Single().Photos;

        Assert.Equal("IMG_0143.jpeg", photos[0].DisplayName);
        Assert.Equal("photo2.jpg", photos[1].DisplayName);   // repli quand la borne n'a pas transmis le nom
    }

    /// <summary>
    /// La liste complète des produits DiLand sert à mesurer la couverture du catalogue
    /// Studio : un format proposé en borne mais absent de chez nous ferait perdre une
    /// ligne le jour où un client le commande, pas avant.
    /// </summary>
    [Fact]
    public void Tous_les_produits_de_DiLand_sont_lisibles_pas_seulement_les_vendus()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();

        Assert.Equal(["10x15"], depot.AllProductNames());
    }

    [Fact]
    public void Le_chemin_de_la_photo_pointe_dans_le_dossier_de_la_commande()
    {
        var depot = Depot_();
        depot.RefreshSnapshot();
        var borne = depot.ReadKioskOrdersAfter(0).Single();
        var photo = depot.LinesOf(borne).Single().Photos[0];

        Assert.True(File.Exists(depot.PhotoPath(borne, photo)));
    }

    [Fact]
    public void La_copie_n_est_refaite_que_si_la_base_a_change()
    {
        var depot = Depot_();
        Assert.True(depot.RefreshSnapshot());

        var copie = Directory.GetFiles(_travail).Single();
        var premiere = new FileInfo(copie).LastWriteTimeUtc;

        Assert.True(depot.RefreshSnapshot());
        Assert.Equal(premiere, new FileInfo(copie).LastWriteTimeUtc);
    }
}
