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
                """;
            creation.ExecuteNonQuery();
        }

        Ajouter(connexion, 10, 36001, "31-001", "2026-07-31 10:00:00", "20260731-1000-aaaa", null);
        Ajouter(connexion, 11, 36002, "31-002", "2026-07-31 11:00:00", "20260731-1100-bbbb", null);
        // commande supprimée logiquement : DiLand l'ignore, nous aussi
        Ajouter(connexion, 12, 36003, "31-003", "2026-07-31 12:00:00", "20260731-1200-cccc", 1);

        foreach (var dossier in new[] { "20260731-1000-aaaa", "20260731-1100-bbbb" })
        {
            var photos = Path.Combine(Depot, "Orders", dossier, "F");
            Directory.CreateDirectory(photos);
            File.WriteAllText(Path.Combine(photos, "photo1.jpg"), "x");
            File.WriteAllText(Path.Combine(photos, "photo2.jpg"), "x");
            // dérivé de DiLand : ne doit pas être compté comme une photo client
            File.WriteAllText(Path.Combine(photos, "O_photo1.jpg"), "x");
        }
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
