using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// L'archive des commandes de bornes : les photos vivent CHEZ NOUS, trente jours.
///
/// Demandé par l'exploitant le 03/08/2026. Jusque-là, l'historique ne gardait que du
/// texte — contenu, prix, client — et pour retrouver les photos il fallait redescendre
/// dans les dossiers de DiLand. Or DiLand les purge quand il l'entend, sans prévenir :
/// une commande close pouvait survivre à ses propres photos, et l'opérateur qui
/// redemandait les fichiers le lendemain tombait sur du vide.
///
/// Studio recopie donc les photos à la prise en charge, les sert depuis sa copie, et les
/// efface avec l'entrée du journal — ni avant (le client peut revenir), ni après (ce sont
/// des photos de clients, et le disque n'est pas extensible).
/// </summary>
public class KioskArchiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "KioskArchive-" + Guid.NewGuid().ToString("N"));

    public KioskArchiveTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "depot", "Orders"));
        CreerDiLand();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Depot => Path.Combine(_root, "depot");
    private string Journal => Path.Combine(_root, "diland", "reprises.json");
    private string DossierDiLand => Path.Combine(Depot, "Orders", "20260803-1648-borne.COM", "F");

    private DiLandImporter Importateur() => new(
        new DiLandRepository(Depot, Path.Combine(_root, "travail")),
        new OrderService(
            new OrderFolderStore(Path.Combine(_root, "commandes")),
            new DailyCounter(Path.Combine(_root, "compteur.json"))),
        [new Product { Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102, Enabled = true }],
        Journal);

    [Fact]
    public void Prendre_en_charge_recopie_les_photos_chez_nous()
    {
        var importateur = Importateur();
        var borne = importateur.Pending().Single();

        importateur.MarkInProgress(borne);

        var entree = importateur.Journal.Find(borne.Oid);
        Assert.NotNull(entree);
        Assert.NotEmpty(entree!.ArchiveDirectory);

        // sous NOS données, et non sous celles de DiLand
        Assert.StartsWith(importateur.ArchiveRoot, entree.ArchiveDirectory);
        Assert.Equal(2, Directory.GetFiles(entree.ArchiveDirectory).Length);
    }

    /// <summary>
    /// Le contrôle qui compte : DiLand a purgé ses dossiers, et l'historique sert quand
    /// même les photos. C'est tout l'objet de l'archive.
    /// </summary>
    [Fact]
    public void L_historique_sert_les_photos_meme_si_DiLand_a_tout_efface()
    {
        var importateur = Importateur();
        var borne = importateur.Pending().Single();

        importateur.MarkInProgress(borne);
        importateur.MarkPrinted(borne.Oid);

        // DiLand fait le ménage, comme il le fait en vrai
        Directory.Delete(Path.Combine(Depot, "Orders", "20260803-1648-borne.COM"), recursive: true);

        var entree = Assert.Single(importateur.Journal.History());
        var dossier = importateur.ArchiveDe(entree);

        Assert.NotNull(dossier);
        Assert.Equal(2, Directory.GetFiles(dossier!).Length);
    }

    /// <summary>Les photos de DiLand ne sont jamais touchées : il peut encore tirer de son côté.</summary>
    [Fact]
    public void Archiver_ne_touche_pas_aux_photos_de_DiLand()
    {
        var importateur = Importateur();
        var avant = Directory.GetFiles(DossierDiLand).OrderBy(f => f).ToList();

        importateur.Archiver(importateur.Pending().Single());

        Assert.Equal(avant, Directory.GetFiles(DossierDiLand).OrderBy(f => f).ToList());
    }

    /// <summary>Archiver deux fois ne duplique rien : c'est le geste d'un opérateur qui hésite.</summary>
    [Fact]
    public void Archiver_deux_fois_ne_duplique_pas()
    {
        var importateur = Importateur();
        var borne = importateur.Pending().Single();

        importateur.Archiver(borne);
        var seconde = importateur.Archiver(borne, refaire: true);

        Assert.Equal(2, Directory.GetFiles(seconde.PhotosDirectory).Length);
    }

    /// <summary>
    /// Passé la rétention, les photos partent AVEC l'entrée.
    ///
    /// Les deux vont ensemble : une copie qu'on ne sait plus rattacher à personne n'a
    /// aucune raison de rester sur le disque, et un mois de commandes de bornes pèse
    /// plusieurs gigaoctets.
    /// </summary>
    [Fact]
    public void Au_dela_de_la_retention_les_photos_sont_effacees_avec_l_entree()
    {
        var importateur = Importateur();
        var borne = importateur.Pending().Single();

        importateur.MarkInProgress(borne);
        importateur.MarkPrinted(borne.Oid);

        var archive = importateur.Journal.Find(borne.Oid)!.ArchiveDirectory;
        Assert.True(Directory.Exists(archive));

        // on vieillit la clôture d'un jour de trop, puis on relit le journal à neuf :
        // c'est à la lecture que la purge passe
        Vieillir(borne.Oid, KioskOrderJournal.Retention + TimeSpan.FromDays(1));

        var relu = new KioskOrderJournal(Journal);
        Assert.Empty(relu.History());
        Assert.False(Directory.Exists(archive));
    }

    /// <summary>Avant l'échéance, rien ne bouge — le client peut encore revenir.</summary>
    [Fact]
    public void Avant_l_echeance_les_photos_restent()
    {
        var importateur = Importateur();
        var borne = importateur.Pending().Single();

        importateur.MarkInProgress(borne);
        importateur.MarkPrinted(borne.Oid);

        var archive = importateur.Journal.Find(borne.Oid)!.ArchiveDirectory;

        Vieillir(borne.Oid, KioskOrderJournal.Retention - TimeSpan.FromDays(1));

        var relu = new KioskOrderJournal(Journal);
        Assert.Single(relu.History());
        Assert.True(Directory.Exists(archive));
    }

    /// <summary>Recule la date de clôture d'une entrée, en écrivant dans le journal.</summary>
    private void Vieillir(long oid, TimeSpan age)
    {
        var journal = new KioskOrderJournal(Journal);
        var entree = journal.Find(oid)!;
        entree.ClosedAt = DateTimeOffset.Now - age;

        // Describe enregistre le fichier ; c'est la seule écriture publique qui ne change
        // pas l'état de l'entrée
        journal.Describe(oid, entree.Number, entree.DailyNumber, entree.OrderedAt,
            entree.CustomerName, entree.Summary, entree.Total, entree.DirectoryName);
    }

    private void CreerDiLand()
    {
        Directory.CreateDirectory(DossierDiLand);
        foreach (var nom in new[] { "photo1.jpg", "photo2.jpg" })
            File.WriteAllBytes(Path.Combine(DossierDiLand, nom), [0xFF, 0xD8, 0xFF, 0xE0]);

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
            VALUES (500, 10, 1, '', 1.5, NULL);

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
