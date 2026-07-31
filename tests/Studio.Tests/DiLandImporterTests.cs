using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// Reprise dans Studio des commandes déposées par les bornes.
///
/// Ce que la boutique demande : récupérer les commandes de bornes comme DiLand le fait —
/// avec leurs produits, leurs quantités et leurs recadrages — sans que DiLand perde les
/// siennes.
/// </summary>
public class DiLandImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DiLandImport-" + Guid.NewGuid().ToString("N"));

    public DiLandImporterTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "depot", "Orders"));
        Directory.CreateDirectory(Path.Combine(_root, "travail"));
        CreerDiLand();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Depot => Path.Combine(_root, "depot");
    private string Registre => Path.Combine(_root, "diland-reprises.json");

    /// <summary>Deux produits homonymes : le catalogue réel a un « 10x15 » minilab et un « 10x15 » DNP.</summary>
    private static IReadOnlyList<Product> Catalogue =>
    [
        new Product { Code = "10x15-dnp", Name = "10x15", WidthMm = 152, HeightMm = 102,
                      Output = ProductOutput.Printer, PrinterName = "DS620", Enabled = true },
        new Product { Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102,
                      Output = ProductOutput.FujiMinilab, Enabled = true },
        new Product { Code = "21x29-7", Name = "21x29,7", WidthMm = 297, HeightMm = 210,
                      Output = ProductOutput.FujiMinilab, Enabled = true },
    ];

    private DiLandImporter Importateur(IReadOnlyList<Product>? catalogue = null)
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var commandes = new OrderService(store, new DailyCounter(Path.Combine(_root, "daily.json")));
        return new DiLandImporter(
            new DiLandRepository(Depot, Path.Combine(_root, "travail")),
            commandes,
            catalogue ?? Catalogue,
            Registre);
    }

    /// <summary>Une commande de borne, une du comptoir, et un produit hors catalogue.</summary>
    private void CreerDiLand()
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(Depot, "Database.db")}");
        c.Open();

        using (var creation = c.CreateCommand())
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
                    Angle REAL, GCRecord INTEGER);

                INSERT INTO Product (Oid, Name) VALUES (1, '10x15'), (2, 'Agenda spirale');

                INSERT INTO "Order" (Oid, Number, DailyNumber, Date, DirectoryName, EndUserName, GCRecord)
                VALUES (10, 12444, '31-005', '2026-07-31 15:00:11', '20260731-1509-borne.COM', '', NULL),
                       (11, 12445, '31-006', '2026-07-31 15:30:00', '20260731-1530-comptoir', '', NULL);

                INSERT INTO OrderLine (Oid, "Order", Product, Description, Price, GCRecord)
                VALUES (500, 10, 1, '', 1.5, NULL),
                       (501, 10, 2, '', 9.9, NULL),
                       (502, 11, 1, '', 0.5, NULL);

                INSERT INTO OrderLineImage
                    (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                     CropX, CropY, CropWidth, CropHeight, Angle, GCRecord)
                VALUES (900, 500, 'photo1.jpg', 'IMG_0143.jpeg', 2, 1, 0.1, 0.2, 0.8, 0.7, 90, NULL),
                       (901, 500, 'photo2.jpg', '', 1, 0, 0, 0, 1, 1, 0, NULL),
                       (902, 501, 'photo1.jpg', '', 1, 0, 0, 0, 1, 1, 0, NULL),
                       (903, 502, 'photo1.jpg', '', 1, 0, 0, 0, 1, 1, 0, NULL);
                """;
            creation.ExecuteNonQuery();
        }

        foreach (var dossier in new[] { "20260731-1509-borne.COM", "20260731-1530-comptoir" })
        {
            var photos = Path.Combine(Depot, "Orders", dossier, "F");
            Directory.CreateDirectory(photos);
            File.WriteAllText(Path.Combine(photos, "photo1.jpg"), "x");
            File.WriteAllText(Path.Combine(photos, "photo2.jpg"), "x");
        }
    }

    // — ce qui est repris —

    [Fact]
    public void Seules_les_commandes_de_bornes_sont_a_reprendre()
    {
        var attente = Importateur().Pending();

        Assert.Equal([12444], attente.Select(c => c.Number));
    }

    [Fact]
    public void La_commande_est_creee_dans_Studio_avec_ses_photos()
    {
        var resultat = Importateur().Import(Importateur().Pending().Single());

        Assert.True(resultat.Succeeded);
        Assert.Equal(DiLandImporter.SourceName, resultat.Created!.Source);
    }

    /// <summary>Deux exemplaires demandés à la borne doivent rester deux exemplaires.</summary>
    [Fact]
    public void Les_quantites_de_la_borne_sont_conservees()
    {
        var importateur = Importateur();
        var resultat = importateur.Import(importateur.Pending().Single());

        var tirages = resultat.Created!.Envelopes
            .SelectMany(e => e.Lines)
            .SelectMany(l => l.Items)
            .Sum(i => i.Quantity);

        Assert.Equal(3, tirages);
    }

    [Fact]
    public void Le_recadrage_fait_a_la_borne_est_repris()
    {
        var importateur = Importateur();
        var resultat = importateur.Import(importateur.Pending().Single());

        var photo = resultat.Created!.Envelopes
            .SelectMany(e => e.Lines).SelectMany(l => l.Items)
            .First(i => i.Quantity == 2);

        Assert.Equal(0.1, photo.Crop.X, 3);
        Assert.Equal(0.8, photo.Crop.Width, 3);
        Assert.Equal(1, photo.RotationQuarterTurns);   // 90° = un quart de tour
    }

    /// <summary>
    /// Un produit que Studio ne vend pas ne doit pas faire perdre le reste de la commande :
    /// on reprend ce qu'on sait faire et on le signale.
    /// </summary>
    [Fact]
    public void Un_produit_inconnu_est_signale_sans_perdre_la_commande()
    {
        var importateur = Importateur();
        var resultat = importateur.Import(importateur.Pending().Single());

        Assert.True(resultat.Succeeded);
        Assert.Contains(resultat.Warnings, a => a.Contains("Agenda spirale"));
    }

    [Fact]
    public void Une_commande_sans_aucun_produit_connu_n_est_pas_creee()
    {
        var importateur = Importateur([]);

        var resultat = importateur.Import(importateur.Pending().Single());

        Assert.False(resultat.Succeeded);
        Assert.Null(resultat.Created);
    }

    // — pas de doublon —

    /// <summary>
    /// Le dossier d'une commande Studio porte son numéro du jour : sans registre, une
    /// deuxième reprise créerait un doublon au lieu d'écraser.
    /// </summary>
    [Fact]
    public void Une_commande_n_est_reprise_qu_une_fois()
    {
        var importateur = Importateur();
        var borne = importateur.Pending().Single();

        Assert.True(importateur.Import(borne).Succeeded);
        var seconde = importateur.Import(borne);

        Assert.False(seconde.Succeeded);
        Assert.Empty(importateur.Pending());
    }

    /// <summary>Le registre survit au redémarrage de l'application, sinon tout serait repris deux fois.</summary>
    [Fact]
    public void Le_registre_survit_au_redemarrage()
    {
        var premier = Importateur();
        premier.Import(premier.Pending().Single());

        Assert.Empty(Importateur().Pending());
    }

    // — choix du produit —

    /// <summary>À nom égal, c'est le minilab qui tire les commandes de bornes.</summary>
    [Fact]
    public void A_nom_egal_le_minilab_l_emporte_sur_la_DNP()
    {
        var produit = Importateur().MatchProduct("10x15");

        Assert.Equal(ProductOutput.FujiMinilab, produit!.Output);
    }

    [Theory]
    [InlineData("21x29,7")]
    [InlineData("21x29.7")]
    [InlineData(" 21X29,7 ")]
    public void Le_nom_du_produit_est_reconnu_malgre_la_casse_et_la_virgule(string nom)
    {
        Assert.Equal("21x29-7", Importateur().MatchProduct(nom)!.Code);
    }

    [Fact]
    public void Un_produit_absent_du_catalogue_ne_renvoie_rien()
    {
        Assert.Null(Importateur().MatchProduct("Agenda spirale"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 1)]
    [InlineData(180, 2)]
    [InlineData(270, 3)]
    [InlineData(360, 0)]
    [InlineData(-90, 3)]
    public void Les_degres_de_DiLand_deviennent_des_quarts_de_tour(double degres, int quarts)
    {
        Assert.Equal(quarts, DiLandImporter.QuarterTurns(degres));
    }

    // — sûreté : DiLand garde ses commandes —

    /// <summary>
    /// Le point capital : DiLand doit pouvoir tirer ses commandes après notre passage.
    /// Ses photos restent en place et sa base n'est pas modifiée.
    /// </summary>
    [Fact]
    public void DiLand_garde_ses_photos_et_sa_base_apres_la_reprise()
    {
        var dossier = Path.Combine(Depot, "Orders", "20260731-1509-borne.COM", "F");
        var photosAvant = Directory.GetFiles(dossier).OrderBy(f => f).ToList();
        var base_ = new FileInfo(Path.Combine(Depot, "Database.db"));
        var baseAvant = (base_.LastWriteTimeUtc, base_.Length);

        var importateur = Importateur();
        importateur.Import(importateur.Pending().Single());

        Assert.Equal(photosAvant, Directory.GetFiles(dossier).OrderBy(f => f).ToList());
        base_.Refresh();
        Assert.Equal(baseAvant, (base_.LastWriteTimeUtc, base_.Length));
    }
}
