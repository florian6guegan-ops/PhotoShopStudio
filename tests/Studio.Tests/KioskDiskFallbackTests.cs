using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// Les commandes de bornes lues SUR LE DISQUE, quand la base ne les a pas.
///
/// C'est le cas courant en boutique : DiLand tombe en panne de mémoire presque tous les
/// jours, et entre le dépôt d'une borne et son relèvement, la commande n'existe que dans
/// son dossier. Elle était alors invisible pour tout le monde.
///
/// Deux choses s'y jouent, et se trompent silencieusement :
///
/// - une commande vue des DEUX côtés ne doit paraître qu'une fois — un doublon coûterait
///   un tirage complet ;
/// - une commande vieille de plusieurs semaines ne doit PAS remonter dans la liste du
///   jour, sous peine de la noyer sous des commandes déjà servies.
/// </summary>
public class KioskDiskFallbackTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "KioskDisk-" + Guid.NewGuid().ToString("N"));

    public KioskDiskFallbackTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "depot", "Orders"));
        Directory.CreateDirectory(Path.Combine(_root, "depot", "IncomingOrders"));
        CreerBaseVide();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Depot => Path.Combine(_root, "depot");

    private DiLandImporter Importateur() => new(
        new DiLandRepository(Depot, Path.Combine(_root, "travail")),
        new OrderService(
            new OrderFolderStore(Path.Combine(_root, "commandes")),
            new DailyCounter(Path.Combine(_root, "compteur.json"))),
        [new Product { Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102, Enabled = true }],
        Path.Combine(_root, "diland", "reprises.json"));

    /// <summary>
    /// Une commande déposée pendant que DiLand était tombé : rien en base, tout sur le
    /// disque. C'est le cas qui motive toute cette lecture.
    /// </summary>
    [Fact]
    public void Une_commande_absente_de_la_base_est_vue_sur_le_disque()
    {
        DeposerSurLeDisque("IncomingOrders", DateTime.Now, numero: 7001);

        var attente = Importateur().Pending();

        Assert.Equal([7001], attente.Select(c => c.Number));
    }

    /// <summary>Les commandes intégrées se lisent aussi du disque, base verrouillée ou non.</summary>
    [Fact]
    public void Une_commande_integree_se_lit_aussi_du_disque()
    {
        DeposerSurLeDisque("Orders", DateTime.Now, numero: 7002);

        Assert.Single(Importateur().Pending());
    }

    /// <summary>
    /// Un dossier <c>.TMP</c> est encore en cours de réception : la borne y écrit, et
    /// DiLand ne le renomme en <c>.COM</c> qu'une fois le transfert complet. Le proposer
    /// ferait ouvrir une commande à moitié arrivée.
    /// </summary>
    [Fact]
    public void Un_paquet_encore_en_cours_de_reception_est_ignore()
    {
        DeposerSurLeDisque("IncomingOrders", DateTime.Now, numero: 7003, extension: ".TMP");

        Assert.Empty(Importateur().Pending());
    }

    /// <summary>
    /// Le dossier <c>Orders</c> de DiLand garde des mois. Y verser tout ce qui n'est pas en
    /// base noierait la liste du jour sous des commandes déjà servies — et une liste qu'on
    /// ne croit plus ne se lit plus.
    /// </summary>
    [Fact]
    public void Une_commande_trop_ancienne_ne_remonte_pas()
    {
        DeposerSurLeDisque("Orders",
            DateTime.Now - DiLandImporter.FenetreDuDisque - TimeSpan.FromDays(2),
            numero: 7004);

        Assert.Empty(Importateur().Pending());
    }

    /// <summary>
    /// Le contrôle qui compte : la même commande des deux côtés ne paraît qu'UNE fois. Un
    /// doublon coûterait un tirage complet, papier compris.
    /// </summary>
    [Fact]
    public void Une_commande_vue_des_deux_cotes_ne_parait_qu_une_fois()
    {
        var quand = DateTime.Now;
        var dossier = DeposerSurLeDisque("Orders", quand, numero: 7005);
        InscrireEnBase(oid: 42, numero: 7005, quand, dossier);

        var attente = Importateur().Pending();

        var seule = Assert.Single(attente);
        Assert.Equal(7005, seule.Number);

        // et c'est la version de la BASE qui l'emporte : elle porte le vrai Oid, celui
        // auquel le journal et les commandes Studio déjà créées se réfèrent
        Assert.Equal(42, seule.Oid);
    }

    /// <summary>Le contenu d'une commande du disque se lit, sans base derrière.</summary>
    [Fact]
    public void Le_contenu_d_une_commande_du_disque_est_lisible()
    {
        DeposerSurLeDisque("IncomingOrders", DateTime.Now, numero: 7006);

        var importateur = Importateur();
        var resume = importateur.Summarize(importateur.Pending().Single());

        Assert.Equal(1, resume.PhotoCount);
        Assert.Equal("10x15 × 1", Assert.Single(resume.Lines));
    }

    // — le décor —

    /// <summary>Dépose une commande complète sur le disque et rend son nom de dossier.</summary>
    private string DeposerSurLeDisque(string racine, DateTime quand, int numero,
        string extension = ".COM")
    {
        var nom = $"{quand:yyyyMMdd-HHmm}-{numero}{extension}";
        var dossier = Path.Combine(Depot, racine, nom);
        Directory.CreateDirectory(Path.Combine(dossier, "F"));
        File.WriteAllBytes(Path.Combine(dossier, "F", "photo.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);

        File.WriteAllText(Path.Combine(dossier, DiLandOrderXml.FileName), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Order Sys_GlobalUniqueId="{Guid.NewGuid()}" Number="{numero}"
                   DailyNumber="03-001" Date="{quand:MM/dd/yyyy HH:mm:ss}"
                   EndUserName="" Price="1.50">
              <Lines>
                <OrderLine Sys_Product_Alias="10x15" Price="1.50" Quantity="1">
                  <Images>
                    <OrderImageOrderLineImage FileName="photo.jpg" OriginalFileName=""
                        Quantity="1" ApplyCrop="False" CropX="0" CropY="0"
                        CropWidth="1536" CropHeight="2048" Angle="0"
                        FineRotationAngle="0" Width="1536" Height="2048"/>
                  </Images>
                </OrderLine>
              </Lines>
            </Order>
            """);

        return nom;
    }

    private void InscrireEnBase(long oid, int numero, DateTime quand, string dossier)
    {
        using var connexion = new SqliteConnection($"Data Source={Path.Combine(Depot, "Database.db")}");
        connexion.Open();

        using var commande = connexion.CreateCommand();
        commande.CommandText = """
            INSERT INTO "Order" (Oid, Number, DailyNumber, Date, DirectoryName, EndUserName, GCRecord)
            VALUES ($oid, $numero, '03-001', $date, $dossier, '', NULL);
            INSERT INTO OrderLine (Oid, "Order", Product, Description, Price, GCRecord)
            VALUES ($oid, $oid, 1, '', 1.5, NULL);
            INSERT INTO OrderLineImage
                (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                 CropX, CropY, CropWidth, CropHeight, Angle, FineRotationAngle,
                 Width, Height, GCRecord)
            VALUES ($oid, $oid, 'photo.jpg', '', 1, 0, 0, 0, 1536, 2048, 0, 0, 1536, 2048, NULL);
            """;
        commande.Parameters.AddWithValue("$oid", oid);
        commande.Parameters.AddWithValue("$numero", numero);
        commande.Parameters.AddWithValue("$date", quand.ToString("yyyy-MM-dd HH:mm:ss"));
        commande.Parameters.AddWithValue("$dossier", dossier);
        commande.ExecuteNonQuery();
    }

    private void CreerBaseVide()
    {
        using var connexion = new SqliteConnection($"Data Source={Path.Combine(Depot, "Database.db")}");
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
            """;
        creation.ExecuteNonQuery();
    }
}
