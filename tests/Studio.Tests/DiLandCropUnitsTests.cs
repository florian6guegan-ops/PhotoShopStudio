using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// L'unité des recadrages des bornes, et leur redressement.
///
/// Deux défauts trouvés le 03/08/2026 en lisant la VRAIE base de la boutique, et qui se
/// voyaient sur le papier sans qu'aucun message ne les signale :
///
/// 1. <b>DiLand exprime les recadrages en PIXELS</b>, quand le code les prenait pour des
///    fractions. Le <c>CropSpec</c> obtenu ne passait pas <c>IsValid</c> et l'on retombait
///    sur l'image entière : tous les recadrages faits par les clients étaient perdus.
///    Relevé : 1231 images, 986 recadrées, <b>aucune</b> dont <c>CropWidth</c> soit ≤ 1.
///
/// 2. <b>Les bornes redressent.</b> Le code passait 0 en commentant le contraire ;
///    113 images portaient un <c>FineRotationAngle</c>, de −5° à +7°.
///
/// Les valeurs des essais sont celles de la boutique, pas des valeurs inventées.
/// </summary>
public class DiLandCropUnitsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DiLandCrop-" + Guid.NewGuid().ToString("N"));

    public DiLandCropUnitsTests()
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

    private DiLandRepository Depuis()
    {
        var depot = new DiLandRepository(Depot, Path.Combine(_root, "travail"));
        Assert.True(depot.RefreshSnapshot());
        return depot;
    }

    private DiLandOrder Commande() =>
        Assert.Single(Depuis().ReadKioskOrdersAfter(0));

    // ----- la conversion elle-même -----

    /// <summary>
    /// Le cas exact de la boutique : <c>7ce78654-…​.jpg</c>, image 1536 × 2048, recadrée
    /// à <c>X=0 Y=44 W=1536 H=1958</c> pour un tirage 8x10. Le rapport obtenu (0,784) doit
    /// être celui du tirage commandé, à un cheveu près.
    /// </summary>
    [Fact]
    public void Un_recadrage_en_pixels_devient_une_fraction()
    {
        var (x, y, largeur, hauteur) =
            DiLandOrderPhoto.EnFractions(0, 44, 1536, 1958, 1536, 2048);

        Assert.Equal(0, x, 4);
        Assert.Equal(44.0 / 2048, y, 4);
        Assert.Equal(1, largeur, 4);
        Assert.Equal(1958.0 / 2048, hauteur, 4);

        // et le rectangle obtenu est enfin recevable — c'est tout l'enjeu
        Assert.True(new CropSpec(x, y, largeur, hauteur).IsValid);
    }

    /// <summary>
    /// Un recadrage déjà fractionnaire n'est pas retouché : une version future de DiLand
    /// pourrait changer d'unité, et l'on ne veut pas diviser deux fois.
    /// </summary>
    [Fact]
    public void Un_recadrage_deja_fractionnaire_est_laisse_tel_quel()
    {
        var (x, y, largeur, hauteur) =
            DiLandOrderPhoto.EnFractions(0.1, 0.2, 0.8, 0.7, 1536, 2048);

        Assert.Equal(0.1, x, 6);
        Assert.Equal(0.2, y, 6);
        Assert.Equal(0.8, largeur, 6);
        Assert.Equal(0.7, hauteur, 6);
    }

    /// <summary>
    /// Sans définition connue, on ne peut pas convertir : on rend les valeurs telles
    /// quelles plutôt que de diviser par zéro et de perdre la photo.
    /// </summary>
    [Fact]
    public void Sans_definition_connue_rien_nest_converti()
    {
        var (_, _, largeur, hauteur) =
            DiLandOrderPhoto.EnFractions(0, 44, 1536, 1958, 0, 0);

        Assert.Equal(1536, largeur, 6);
        Assert.Equal(1958, hauteur, 6);
    }

    // ----- la lecture de bout en bout -----

    [Fact]
    public void La_base_rend_des_recadrages_recevables()
    {
        var photo = Assert.Single(
            Depuis().LinesOf(Commande()).SelectMany(l => l.Photos),
            p => p.FileName == "recadree.jpg");

        Assert.True(photo.ApplyCrop);
        Assert.True(new CropSpec(photo.CropX, photo.CropY, photo.CropWidth, photo.CropHeight).IsValid);
        Assert.Equal(1958.0 / 2048, photo.CropHeight, 4);
    }

    [Fact]
    public void Le_redressement_de_la_borne_est_repris()
    {
        var photo = Assert.Single(
            Depuis().LinesOf(Commande()).SelectMany(l => l.Photos),
            p => p.FileName == "penchee.jpg");

        Assert.Equal(-2, photo.FineRotationDegrees, 3);
    }

    /// <summary>
    /// Le contrôle qui compte : ce que la commande Studio emporte réellement. C'est là que
    /// le recadrage se perdait, et là qu'il doit se retrouver.
    /// </summary>
    [Fact]
    public void La_commande_reprise_garde_recadrage_et_redressement()
    {
        var depot = Depuis();
        var commande = Assert.Single(depot.ReadKioskOrdersAfter(0));

        var magasin = new OrderFolderStore(Path.Combine(_root, "commandes"));
        var importateur = new DiLandImporter(
            depot,
            new OrderService(magasin, new DailyCounter(Path.Combine(_root, "compteur.json"))),
            [new Product { Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102, Enabled = true }],
            Path.Combine(_root, "journal.json"));

        var creee = importateur.Import(commande).Created;
        Assert.NotNull(creee);

        // les articles gardent l'ordre des photos de la ligne ; leurs noms, eux, sont
        // renumérotés par OrderService (001.jpg, 002.jpg…) — on ne s'y fie donc pas
        var articles = creee!.Envelopes.SelectMany(e => e.Lines).SelectMany(l => l.Items).ToList();
        Assert.Equal(2, articles.Count);

        var recadree = articles[0];
        Assert.False(recadree.Crop.IsFull);
        Assert.Equal(44.0 / 2048, recadree.Crop.Y, 4);
        Assert.Equal(1958.0 / 2048, recadree.Crop.Height, 4);

        Assert.Equal(-2, articles[1].FineRotationDegrees, 3);
    }

    // ----- le décor -----

    private void CreerDiLand()
    {
        var dossier = Path.Combine(Depot, "Orders", "20260803-1648-borne.COM", "F");
        Directory.CreateDirectory(dossier);
        foreach (var nom in new[] { "recadree.jpg", "penchee.jpg" })
            File.WriteAllBytes(Path.Combine(dossier, nom), [0xFF, 0xD8, 0xFF, 0xE0]);

        using var connexion = new SqliteConnection(
            $"Data Source={Path.Combine(Depot, "Database.db")}");
        connexion.Open();

        using var creation = connexion.CreateCommand();

        // Les valeurs sont celles de la boutique : recadrage en PIXELS, redressement en
        // degrés entiers.
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
            VALUES (500, 10, 1, '', 22.55, NULL);

            INSERT INTO OrderLineImage
                (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                 CropX, CropY, CropWidth, CropHeight, Angle, FineRotationAngle,
                 Width, Height, GCRecord)
            VALUES (900, 500, 'recadree.jpg', 'IMG_0143.jpeg', 1, 1,
                    0, 44, 1536, 1958, 0, 0, 1536, 2048, NULL),
                   (901, 500, 'penchee.jpg', '', 1, 0,
                    0, 0, 1536, 2048, 0, -2, 1536, 2048, NULL);
            """;
        creation.ExecuteNonQuery();
    }
}
