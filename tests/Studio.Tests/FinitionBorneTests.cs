using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// La finition choisie par le client à la borne, sur les DEUX chemins qui ouvrent une
/// commande — « Reprendre », qui crée la commande Studio, et « Modifier », qui recopie les
/// fichiers pour les retoucher avant de tirer.
///
/// <b>Ce que ces essais protègent.</b> « Modifier » recopie les FICHIERS puis rescanne le
/// dossier : tout ce qui n'est pas explicitement porté avec eux disparaît à l'ouverture.
/// Le recadrage du client s'y était déjà perdu ; la finition s'y perdait à son tour, et
/// une commande lustrée s'ouvrait sans finition — le tirage repartait alors sur la machine
/// dont le rouleau avait la bonne LARGEUR, en brillant, sans que rien ne le signale.
/// </summary>
public class FinitionBorneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "FinitionBorne-" + Guid.NewGuid().ToString("N"));

    public FinitionBorneTests() => CreerDiLand(avecPaperType: true);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Depot => Path.Combine(_root, "depot");

    private string DossierPhotos =>
        Path.Combine(Depot, "Orders", "20260811-1030-borne.COM", "F");

    private static readonly Product Dix15 = new()
    {
        Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102, Enabled = true,
    };

    private DiLandImporter Importateur() => new(
        new DiLandRepository(Depot, Path.Combine(_root, "travail")),
        new OrderService(
            new OrderFolderStore(Path.Combine(_root, "commandes")),
            new DailyCounter(Path.Combine(_root, "compteur.json"))),
        [Dix15],
        Path.Combine(_root, "diland", "reprises.json"));

    /// <summary>
    /// Le contrôle qui compte : « Modifier » porte la finition du client jusqu'à l'écran
    /// des photos, ligne par ligne.
    /// </summary>
    [Fact]
    public void Modifier_porte_la_finition_du_client()
    {
        var importateur = Importateur();
        var cadrages = importateur.Archiver(importateur.Pending().Single()).Cadrages;

        Assert.Equal(FinitionPapier.Lustre, cadrages["lustree.jpg"].Finition);
        Assert.Equal(FinitionPapier.Brillant, cadrages["brillante.jpg"].Finition);
    }

    /// <summary>
    /// « Reprendre » et « Modifier » lisent la MÊME finition.
    ///
    /// C'est la régression qu'on veut interdire, et c'est la même que pour le recadrage :
    /// le même client ne doit pas repartir avec deux papiers différents selon le bouton
    /// que l'opérateur a pressé.
    /// </summary>
    [Fact]
    public void Reprendre_et_Modifier_lisent_la_meme_finition()
    {
        var importateur = Importateur();
        var commande = importateur.Pending().Single();

        var parModifier = importateur.Archiver(commande).Cadrages["lustree.jpg"].Finition;

        var creee = importateur.Import(commande).Created;
        Assert.NotNull(creee);

        var parReprendre = creee!.Envelopes
            .SelectMany(e => e.Lines)
            .SelectMany(l => l.Items)
            // OrderItem.FileName est le nom DANS la commande, renuméroté à la copie :
            // c'est OriginalName qui garde celui du fichier d'origine
            .Single(i => i.OriginalName.Equals("lustree.jpg", StringComparison.OrdinalIgnoreCase))
            .Finish;

        Assert.Equal(FinitionPapier.Lustre, parModifier);
        Assert.Equal(parModifier, parReprendre);
    }

    /// <summary>
    /// Une commande qui mélange les deux finitions part en DEUX enveloppes : une enveloppe
    /// s'envoie d'un bloc sur une seule machine, et sur le DE100 la finition c'est le
    /// rouleau. Le cas est réel — commande 10-013 du 10/08/2026, un client qui a pris les
    /// deux dans le même panier.
    /// </summary>
    [Fact]
    public void Une_commande_mixte_part_en_deux_enveloppes()
    {
        var importateur = Importateur();
        var creee = importateur.Import(importateur.Pending().Single()).Created;

        Assert.NotNull(creee);

        var finitions = creee!.Envelopes
            .Select(e => e.Lines.SelectMany(l => l.Items).Select(i => i.Finish).Distinct().Single())
            .ToList();

        Assert.Equal(2, creee.Envelopes.Count);
        Assert.Contains(FinitionPapier.Lustre, finitions);
        Assert.Contains(FinitionPapier.Brillant, finitions);
    }

    /// <summary>
    /// <b>Portabilité.</b> Une installation de DiLand dont la table <c>OrderLine</c> n'a pas
    /// la colonne <c>PaperType</c> doit continuer à rendre ses commandes — sans finition,
    /// mais entières. La nommer sans précaution ferait échouer la requête, et le magasin ne
    /// verrait plus AUCUNE commande de borne pour une information qu'il ne réclame peut-être
    /// même pas.
    /// </summary>
    [Fact]
    public void Une_base_sans_colonne_PaperType_rend_quand_meme_les_commandes()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Depot, recursive: true);
        CreerDiLand(avecPaperType: false);

        var importateur = Importateur();
        var commande = importateur.Pending().Single();
        var lignes = importateur.Archiver(commande).Cadrages;

        Assert.Equal(2, lignes.Count);
        Assert.All(lignes.Values, c => Assert.Null(c.Finition));
    }

    private void CreerDiLand(bool avecPaperType)
    {
        Directory.CreateDirectory(DossierPhotos);
        foreach (var nom in new[] { "lustree.jpg", "brillante.jpg" })
            File.WriteAllBytes(Path.Combine(DossierPhotos, nom), [0xFF, 0xD8, 0xFF, 0xE0]);

        using var connexion = new SqliteConnection(
            $"Data Source={Path.Combine(Depot, "Database.db")}");
        connexion.Open();

        // la colonne de finition est celle de DiLand, pas la nôtre : les essais doivent
        // pouvoir décrire une base qui ne l'a pas
        var colonnePapier = avecPaperType ? ", PaperType INTEGER" : "";

        using var creation = connexion.CreateCommand();
        creation.CommandText = $"""
            CREATE TABLE "Order" (
                Oid INTEGER PRIMARY KEY, Number INTEGER, DailyNumber TEXT, Date TEXT,
                DirectoryName TEXT, EndUserName TEXT, GCRecord INTEGER);
            CREATE TABLE Product (Oid INTEGER PRIMARY KEY, Name TEXT, GCRecord INTEGER);
            CREATE TABLE OrderLine (
                Oid INTEGER PRIMARY KEY, "Order" INTEGER, Product INTEGER,
                Description TEXT, Price REAL, GCRecord INTEGER{colonnePapier});
            CREATE TABLE OrderLineImage (
                Oid INTEGER PRIMARY KEY, OrderLine INTEGER, FileName TEXT,
                OriginalFileName TEXT, Quantity INTEGER, ApplyCrop INTEGER,
                CropX REAL, CropY REAL, CropWidth REAL, CropHeight REAL,
                Angle REAL, FineRotationAngle REAL, Width INTEGER, Height INTEGER,
                GCRecord INTEGER);

            INSERT INTO Product (Oid, Name) VALUES (1, '10x15');

            INSERT INTO "Order" (Oid, Number, DailyNumber, Date, DirectoryName, EndUserName, GCRecord)
            VALUES (10, 7100, '11-001', '2026-08-11 10:30:00', '20260811-1030-borne.COM', 'MAYA', NULL);

            -- deux lignes du même produit, deux finitions : le cas de la commande 10-013
            INSERT INTO OrderLine (Oid, "Order", Product, Description, Price, GCRecord{(avecPaperType ? ", PaperType" : "")})
            VALUES (500, 10, 1, '', 0.60, NULL{(avecPaperType ? ", 3" : "")}),
                   (501, 10, 1, '', 0.60, NULL{(avecPaperType ? ", 1" : "")});

            INSERT INTO OrderLineImage
                (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                 CropX, CropY, CropWidth, CropHeight, Angle, FineRotationAngle,
                 Width, Height, GCRecord)
            VALUES
                (900, 500, 'lustree.jpg', 'lustree.jpg', 1, 0, 0, 0, 1, 1, 0, 0, 1536, 2048, NULL),
                (901, 501, 'brillante.jpg', 'brillante.jpg', 1, 0, 0, 0, 1, 1, 0, 0, 1536, 2048, NULL);
            """;
        creation.ExecuteNonQuery();
    }
}
