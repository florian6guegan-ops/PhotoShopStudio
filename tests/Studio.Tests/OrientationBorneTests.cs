using Microsoft.Data.Sqlite;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// L'orientation des photos venues des bornes.
///
/// <b>Le défaut que ces essais figent</b> (relevé le 05/08/2026 sur la base de la
/// boutique) : l'« Angle » de DiLand est la rotation TOTALE depuis le fichier brut,
/// orientation EXIF comprise — et non la rotation faite par le client. Studio applique
/// toujours l'EXIF avant les quarts de tour ; reprendre l'angle tel quel le comptait donc
/// deux fois. Une photo de téléphone en portrait (EXIF 8, Angle 270) était redressée puis
/// tournée de 270° de plus : elle partait couchée, et le recadrage validé par le client —
/// exprimé lui aussi dans le repère redressé — tombait à côté.
///
/// Sur 185 photos d'angle non nul, 183 avaient un Angle égal à leur orientation EXIF ; les
/// deux autres étaient de vraies rotations faites à la borne, sur des fichiers sans EXIF.
/// D'où la soustraction plutôt que l'abandon pur et simple de l'angle.
/// </summary>
public class OrientationBorneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DiLandOrient-" + Guid.NewGuid().ToString("N"));

    public OrientationBorneTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "depot", "Orders"));
        Directory.CreateDirectory(Path.Combine(_root, "travail"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    // ————— le lecteur d'orientation —————

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void L_orientation_EXIF_est_relue_telle_quelle(int orientation)
    {
        var chemin = Path.Combine(_root, $"exif{orientation}.jpg");
        File.WriteAllBytes(chemin, JpegAvecOrientation(orientation));

        Assert.Equal(orientation, OrientationExif.Lire(chemin));
    }

    /// <summary>Le gros bout : c'est la valeur 8 des photos de téléphone en portrait.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(6, 1)]
    [InlineData(8, 3)]
    public void L_orientation_se_traduit_en_quarts_de_tour_horaires(int orientation, int quarts)
    {
        var chemin = Path.Combine(_root, $"quarts{orientation}.jpg");
        File.WriteAllBytes(chemin, JpegAvecOrientation(orientation));

        Assert.Equal(quarts, OrientationExif.QuartsDeTour(chemin));
    }

    /// <summary>
    /// Un fichier sans EXIF, illisible ou absent ne redresse rien — et surtout n'empêche
    /// pas la commande de s'ouvrir.
    /// </summary>
    [Fact]
    public void Un_fichier_muet_ou_abime_ne_fait_pas_tourner_la_photo()
    {
        var sansExif = Path.Combine(_root, "sans-exif.jpg");
        File.WriteAllBytes(sansExif, [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]);

        var abime = Path.Combine(_root, "abime.jpg");
        File.WriteAllText(abime, "ceci n'est pas une photo");

        Assert.Equal(0, OrientationExif.QuartsDeTour(sansExif));
        Assert.Equal(0, OrientationExif.QuartsDeTour(abime));
        Assert.Equal(0, OrientationExif.QuartsDeTour(Path.Combine(_root, "jamais-ecrit.jpg")));
    }

    /// <summary>Un APP1 de XMP ne porte pas d'orientation : il ne doit pas être pris pour de l'EXIF.</summary>
    [Fact]
    public void Un_APP1_qui_n_est_pas_de_l_EXIF_est_saute()
    {
        var chemin = Path.Combine(_root, "xmp.jpg");
        File.WriteAllBytes(chemin, JpegAvecOrientation(8, xmpAvant: true));

        Assert.Equal(3, OrientationExif.QuartsDeTour(chemin));
    }

    // ————— la soustraction —————

    /// <summary>
    /// Le cas de la boutique : photo de téléphone en portrait. L'EXIF a déjà tout fait,
    /// il ne reste RIEN à tourner.
    /// </summary>
    [Fact]
    public void Une_photo_dont_l_angle_n_est_que_son_EXIF_ne_tourne_plus()
    {
        var chemin = Path.Combine(_root, "portrait.jpg");
        File.WriteAllBytes(chemin, JpegAvecOrientation(8));

        Assert.Equal(0, DiLandImporter.QuartsDeTourResiduels(Photo(angle: 270), chemin));
    }

    /// <summary>
    /// L'autre cas, plus rare mais réel : un fichier sans EXIF que le client a tourné à la
    /// borne. Ignorer l'angle le ferait sortir de travers.
    /// </summary>
    [Fact]
    public void Une_vraie_rotation_faite_a_la_borne_est_conservee()
    {
        var chemin = Path.Combine(_root, "tournee-main.jpg");
        File.WriteAllBytes(chemin, [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]);

        Assert.Equal(1, DiLandImporter.QuartsDeTourResiduels(Photo(angle: 90), chemin));
    }

    /// <summary>Client qui tourne d'un quart une photo déjà redressée par son EXIF.</summary>
    [Fact]
    public void Les_deux_rotations_se_cumulent_sans_se_repeter()
    {
        var chemin = Path.Combine(_root, "exif-et-main.jpg");
        File.WriteAllBytes(chemin, JpegAvecOrientation(8)); // 3 quarts déjà faits

        // le client a tourné d'un quart de plus : 3 + 1 = 4 quarts, soit 0° — DiLand note
        // donc un angle nul, et il reste bien un quart à défaire dans l'autre sens
        Assert.Equal(1, DiLandImporter.QuartsDeTourResiduels(Photo(angle: 0), chemin));
    }

    // ————— de bout en bout, par l'importateur —————

    /// <summary>
    /// Le parcours « Modifier » : c'est <c>Stage</c> qui pose le cadrage sur les vignettes,
    /// et il doit lire l'orientation sur la COPIE — l'original d'une commande traitée a son
    /// en-tête brouillé, donc illisible.
    /// </summary>
    [Fact]
    public void Le_cadrage_prepare_pour_l_ecran_ne_tourne_pas_deux_fois()
    {
        CreerDiLand(angle: 270, orientationExif: 8);

        var importateur = Importateur();
        var prete = importateur.Archiver(importateur.Pending().Single());

        Assert.Equal(0, prete.Cadrages["photo1.jpg"].QuartsDeTour);
    }

    /// <summary>Le parcours « Reprendre » doit rendre exactement la même chose.</summary>
    [Fact]
    public void Le_parcours_Reprendre_tourne_comme_le_parcours_Modifier()
    {
        CreerDiLand(angle: 270, orientationExif: 8);

        var importateur = Importateur();
        var resultat = importateur.Import(importateur.Pending().Single());

        var photo = resultat.Created!.Envelopes
            .SelectMany(e => e.Lines).SelectMany(l => l.Items).First();

        Assert.Equal(0, photo.RotationQuarterTurns);
    }

    /// <summary>
    /// Le recadrage du client, lui, n'a pas à être transposé : DiLand l'exprime dans le
    /// repère REDRESSÉ, celui-là même où l'image se retrouve une fois la rotation juste
    /// appliquée. C'est toute la raison pour laquelle corriger l'angle suffit.
    /// </summary>
    [Fact]
    public void Le_recadrage_du_client_traverse_intact()
    {
        CreerDiLand(angle: 270, orientationExif: 8);

        var importateur = Importateur();
        var cadrage = importateur.Archiver(importateur.Pending().Single()).Cadrages["photo1.jpg"];

        // 605 / 4000 et 2789 / 4000, les valeurs relevées en boutique
        Assert.Equal(0.15125, cadrage.Crop.X, 5);
        Assert.Equal(0.697250, cadrage.Crop.Width, 5);
    }

    // ————— le harnais —————

    private static DiLandOrderPhoto Photo(double angle) =>
        new("photo1.jpg", "IMG_0143.jpeg", 1, true, 0, 0, 1, 1, angle);

    private string Depot => Path.Combine(_root, "depot");

    private DiLandImporter Importateur()
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var commandes = new OrderService(store, new DailyCounter(Path.Combine(_root, "daily.json")));

        return new DiLandImporter(
            new DiLandRepository(Depot, Path.Combine(_root, "travail")),
            commandes,
            [new Product { Code = "10x15", Name = "10x15", WidthMm = 152, HeightMm = 102,
                           Output = ProductOutput.FujiMinilab, Enabled = true, Price = 0.60m }],
            Path.Combine(_root, "diland-reprises.json"));
    }

    /// <summary>
    /// Une commande de borne d'une seule photo, avec les cotes réelles d'un téléphone :
    /// 4000 × 6016 une fois redressée, recadrée au format d'une photo d'identité.
    /// </summary>
    private void CreerDiLand(double angle, int orientationExif)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(Depot, "Database.db")}");
        c.Open();

        using (var creation = c.CreateCommand())
        {
            creation.CommandText = $"""
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
                VALUES (10, 12449, '05-012', '2026-08-05 12:48:00', '20260805-1248-borne.COM', '', NULL);

                INSERT INTO OrderLine (Oid, "Order", Product, Description, Price, GCRecord)
                VALUES (500, 10, 1, '', 1.5, NULL);

                INSERT INTO OrderLineImage
                    (Oid, OrderLine, FileName, OriginalFileName, Quantity, ApplyCrop,
                     CropX, CropY, CropWidth, CropHeight, Angle, FineRotationAngle,
                     Width, Height, GCRecord)
                VALUES (900, 500, 'photo1.jpg', 'IMG_0143.jpeg', 1, 1,
                        605, 960, 2789, 3585, {angle}, 0, 4000, 6016, NULL);
                """;
            creation.ExecuteNonQuery();
        }

        var photos = Path.Combine(Depot, "Orders", "20260805-1248-borne.COM", "F");
        Directory.CreateDirectory(photos);
        File.WriteAllBytes(Path.Combine(photos, "photo1.jpg"), JpegAvecOrientation(orientationExif));
    }

    /// <summary>
    /// Un JPEG réduit à ce que le lecteur regarde : l'en-tête, un APP1 EXIF, et la fin.
    ///
    /// Fabriqué à la main plutôt que rendu par une bibliothèque d'images — c'est le seul
    /// moyen de poser une orientation EXIF choisie sans faire entrer Magick.NET dans les
    /// essais de <c>Studio.Store</c>.
    /// </summary>
    /// <param name="xmpAvant">Glisse un APP1 étranger devant, comme le font les téléphones.</param>
    private static byte[] JpegAvecOrientation(int orientation, bool xmpAvant = false)
    {
        var fichier = new List<byte> { 0xFF, 0xD8 }; // SOI

        if (xmpAvant)
        {
            var xmp = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
            fichier.AddRange([0xFF, 0xE1]);
            fichier.AddRange(SurDeuxOctets(xmp.Length + 2));
            fichier.AddRange(xmp);
        }

        // TIFF petit-boutiste : « II », 42, puis l'IFD au huitième octet
        var tiff = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 };
        tiff.AddRange([1, 0]);                                  // une entrée
        tiff.AddRange([0x12, 0x01]);                            // tag 0x0112, Orientation
        tiff.AddRange([3, 0]);                                  // type SHORT
        tiff.AddRange([1, 0, 0, 0]);                            // un élément
        tiff.AddRange([(byte)orientation, 0, 0, 0]);            // la valeur, écrite sur place
        tiff.AddRange([0, 0, 0, 0]);                            // pas d'IFD suivant

        var charge = new List<byte>("Exif\0\0"u8.ToArray());
        charge.AddRange(tiff);

        fichier.AddRange([0xFF, 0xE1]);
        fichier.AddRange(SurDeuxOctets(charge.Count + 2));
        fichier.AddRange(charge);

        fichier.AddRange([0xFF, 0xDA, 0x00, 0x02]); // SOS : plus rien à lire au-delà
        fichier.AddRange([0xFF, 0xD9]);             // EOI

        return [.. fichier];
    }

    private static byte[] SurDeuxOctets(int valeur) => [(byte)(valeur >> 8), (byte)(valeur & 0xFF)];
}
