using Microsoft.Data.Sqlite;

namespace Studio.Store.DiLand;

/// <summary>Une photo commandée, telle que la borne l'a envoyée.</summary>
/// <param name="FileName">Fichier déposé dans le sous-dossier <c>F</c> de la commande.</param>
/// <param name="OriginalFileName">Nom d'origine chez le client, par exemple « IMG_0143.jpeg ».</param>
/// <param name="Quantity">Nombre de tirages demandés pour cette photo.</param>
/// <param name="ApplyCrop">Vrai si le client a recadré à la borne.</param>
/// <param name="CropX">Bord gauche du recadrage, en fraction de la largeur.</param>
/// <param name="CropY">Bord haut du recadrage, en fraction de la hauteur.</param>
/// <param name="CropWidth">Largeur du recadrage, en fraction de la largeur.</param>
/// <param name="CropHeight">Hauteur du recadrage, en fraction de la hauteur.</param>
/// <param name="Angle">Rotation appliquée à la borne, en degrés.</param>
public sealed record DiLandOrderPhoto(
    string FileName,
    string OriginalFileName,
    int Quantity,
    bool ApplyCrop,
    double CropX,
    double CropY,
    double CropWidth,
    double CropHeight,
    double Angle)
{
    /// <summary>
    /// Nom à montrer à l'opérateur. Le fichier stocké est un identifiant illisible ; le nom
    /// d'origine du client permet de retrouver la photo dont il parle.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(OriginalFileName) ? FileName : OriginalFileName;
}

/// <summary>Une ligne de commande : un produit, et les photos tirées dessus.</summary>
/// <param name="Oid">Identifiant interne DiLand.</param>
/// <param name="ProductName">Nom du produit, par exemple « 10x15 ».</param>
/// <param name="Price">Prix de la ligne tel que la borne l'a calculé.</param>
/// <param name="Photos">Photos de la ligne, dans l'ordre où la borne les a envoyées.</param>
public sealed record DiLandOrderLine(
    long Oid,
    string ProductName,
    decimal Price,
    IReadOnlyList<DiLandOrderPhoto> Photos)
{
    /// <summary>
    /// Nombre de tirages de la ligne : c'est la somme des quantités, pas le nombre de
    /// photos. Un client qui demande deux exemplaires d'une photo compte pour deux.
    /// </summary>
    public int PrintCount => Photos.Sum(p => Math.Max(1, p.Quantity));

    public override string ToString() => $"{ProductName} — {PrintCount} tirage(s)";
}

/// <summary>Une commande reçue par DiLand depuis une borne.</summary>
/// <param name="Oid">Identifiant interne DiLand ; sert de curseur de reprise.</param>
/// <param name="Number">Numéro de commande affiché à l'opérateur.</param>
/// <param name="DailyNumber">Numéro du jour, par exemple « 31-004 ».</param>
/// <param name="Date">Date de dépôt.</param>
/// <param name="DirectoryName">Dossier des photos, sous <c>Repositories\Default\Orders</c>.</param>
/// <param name="EndUserName">Nom saisi par le client, souvent vide.</param>
/// <param name="PhotoCount">Nombre de fichiers photo trouvés pour la commande.</param>
public sealed record DiLandOrder(
    long Oid,
    int Number,
    string DailyNumber,
    DateTime Date,
    string DirectoryName,
    string EndUserName,
    int PhotoCount)
{
    /// <summary>
    /// Vrai si la commande vient d'une borne et non du comptoir.
    ///
    /// DiLand suffixe en <c>.COM</c> le dossier des commandes reçues par le réseau : la
    /// borne dépose un paquet dans <c>IncomingOrders\&lt;horodatage&gt;.TMP</c>, et une
    /// fois intégré le dossier devient <c>Orders\&lt;horodatage&gt;.COM</c>. Vérifié le
    /// 31/07/2026 : les six derniers paquets correspondent aux six derniers .COM.
    /// </summary>
    public bool IsFromKiosk =>
        DirectoryName.EndsWith(".COM", StringComparison.OrdinalIgnoreCase);

    /// <summary>Une commande sans numéro ni photo est un brouillon que la borne n'a pas fini d'envoyer.</summary>
    public bool IsComplete => Number > 0 && PhotoCount > 0;

    public override string ToString() =>
        $"#{Number} ({DailyNumber}) du {Date:dd/MM HH:mm}{(IsFromKiosk ? " — borne" : "")}";
}

/// <summary>
/// Lecture des commandes que les bornes déposent dans DiLand — sans jamais l'en priver.
///
/// PRINCIPE DE SÛRETÉ : on n'ouvre PAS la base de DiLand. Elle est en mode journal
/// classique (aucun fichier -wal), où un lecteur prend un verrou partagé qui ferait
/// attendre ses écritures. Sur un logiciel de production, c'est inacceptable. On copie
/// donc le fichier et on lit la copie : l'impact sur DiLand est nul par construction.
///
/// Rien n'est déplacé, renommé ni supprimé : DiLand traite ses commandes exactement
/// comme si nous n'existions pas.
/// </summary>
public sealed class DiLandRepository
{
    /// <summary>Emplacement habituel du dépôt DiLand sur le poste de la boutique.</summary>
    public const string DefaultRoot =
        @"C:\Program Files (x86)\DiLand Studio 2\Data\AllUsersData\Repositories\Default";

    private readonly string _root;
    private readonly string _workDir;

    private DateTime _snapshotSource = DateTime.MinValue;

    /// <param name="root">Dossier du dépôt DiLand.</param>
    /// <param name="workDir">Dossier de travail où déposer la copie de la base.</param>
    public DiLandRepository(string root, string workDir)
    {
        _root = root;
        _workDir = workDir;
    }

    public string DatabasePath => Path.Combine(_root, "Database.db");

    public string OrdersDirectory => Path.Combine(_root, "Orders");

    private string SnapshotPath => Path.Combine(_workDir, "diland-snapshot.db");

    /// <summary>Vrai si le dépôt DiLand est présent et lisible.</summary>
    public bool IsAvailable => File.Exists(DatabasePath);

    /// <summary>
    /// Rafraîchit la copie de travail si la base a changé depuis la dernière fois.
    /// Renvoie faux si la copie a échoué — DiLand écrivait sans doute au même moment,
    /// on réessaiera au prochain passage plutôt que d'insister.
    /// </summary>
    public bool RefreshSnapshot()
    {
        if (!IsAvailable) return false;

        var source = new FileInfo(DatabasePath);
        if (source.LastWriteTimeUtc <= _snapshotSource && File.Exists(SnapshotPath))
            return true;   // rien de neuf

        Directory.CreateDirectory(_workDir);
        try
        {
            File.Copy(DatabasePath, SnapshotPath, overwrite: true);
            _snapshotSource = source.LastWriteTimeUtc;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Commandes plus récentes que <paramref name="afterOid"/>, de la plus ancienne à la
    /// plus récente. L'identifiant interne sert de curseur : il ne recule jamais.
    /// </summary>
    public IReadOnlyList<DiLandOrder> ReadOrdersAfter(long afterOid, int limit = 200)
    {
        if (!File.Exists(SnapshotPath)) return [];

        var commandes = new List<DiLandOrder>();

        using var connexion = OpenSnapshot();
        using var commande = connexion.CreateCommand();
        commande.CommandText = """
            SELECT Oid, Number, DailyNumber, Date, DirectoryName, EndUserName
            FROM "Order"
            WHERE GCRecord IS NULL AND Oid > $apres AND DirectoryName IS NOT NULL
            ORDER BY Oid
            LIMIT $limite
            """;
        commande.Parameters.AddWithValue("$apres", afterOid);
        commande.Parameters.AddWithValue("$limite", limit);

        using var lecteur = commande.ExecuteReader();
        while (lecteur.Read())
        {
            var dossier = lecteur.GetString(4);
            commandes.Add(new DiLandOrder(
                Oid: lecteur.GetInt64(0),
                Number: lecteur.IsDBNull(1) ? 0 : lecteur.GetInt32(1),
                DailyNumber: lecteur.IsDBNull(2) ? "" : lecteur.GetString(2),
                Date: lecteur.IsDBNull(3) ? DateTime.MinValue : lecteur.GetDateTime(3),
                DirectoryName: dossier,
                EndUserName: lecteur.IsDBNull(5) ? "" : lecteur.GetString(5),
                PhotoCount: CountPhotos(dossier)));
        }

        return commandes;
    }

    /// <summary>
    /// Les commandes de bornes seules — c'est celles-là que la boutique veut récupérer.
    /// Les commandes du comptoir sont déjà saisies chez nous, les reprendre ferait doublon.
    ///
    /// Les brouillons qu'une borne n'a pas fini d'envoyer sont écartés : ils n'ont ni
    /// numéro ni photo, et leur dossier se remplit encore.
    /// </summary>
    public IReadOnlyList<DiLandOrder> ReadKioskOrdersAfter(long afterOid, int limit = 200) =>
        ReadOrdersAfter(afterOid, limit).Where(c => c.IsFromKiosk && c.IsComplete).ToList();

    /// <summary>
    /// Contenu d'une commande : les produits demandés et, pour chacun, les photos avec leur
    /// quantité et leur recadrage. C'est ce qui permet de refaire le tirage à l'identique
    /// plutôt que de repartir d'un tas de fichiers.
    /// </summary>
    public IReadOnlyList<DiLandOrderLine> LinesOf(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!File.Exists(SnapshotPath)) return [];

        using var connexion = OpenSnapshot();

        var lignes = new List<(long Oid, string Produit, decimal Prix)>();

        using (var commande = connexion.CreateCommand())
        {
            // le nom du produit vit dans une autre table ; sans lui la ligne ne dit rien
            commande.CommandText = """
                SELECT l.Oid, COALESCE(p.Name, l.Description, ''), COALESCE(l.Price, 0)
                FROM OrderLine l
                LEFT JOIN Product p ON p.Oid = l.Product
                WHERE l."Order" = $commande AND l.GCRecord IS NULL
                ORDER BY l.Oid
                """;
            commande.Parameters.AddWithValue("$commande", order.Oid);

            using var lecteur = commande.ExecuteReader();
            while (lecteur.Read())
                lignes.Add((lecteur.GetInt64(0), lecteur.GetString(1), lecteur.GetDecimal(2)));
        }

        return lignes
            .Select(l => new DiLandOrderLine(l.Oid, l.Produit, l.Prix, ReadPhotos(connexion, l.Oid)))
            .ToList();
    }

    private static IReadOnlyList<DiLandOrderPhoto> ReadPhotos(SqliteConnection connexion, long ligne)
    {
        var photos = new List<DiLandOrderPhoto>();

        using var commande = connexion.CreateCommand();
        commande.CommandText = """
            SELECT FileName, COALESCE(OriginalFileName, ''), COALESCE(Quantity, 1),
                   COALESCE(ApplyCrop, 0), COALESCE(CropX, 0), COALESCE(CropY, 0),
                   COALESCE(CropWidth, 1), COALESCE(CropHeight, 1), COALESCE(Angle, 0)
            FROM OrderLineImage
            WHERE OrderLine = $ligne AND GCRecord IS NULL
              AND FileName IS NOT NULL AND FileName <> ''
            ORDER BY Oid
            """;
        commande.Parameters.AddWithValue("$ligne", ligne);

        using var lecteur = commande.ExecuteReader();
        while (lecteur.Read())
        {
            photos.Add(new DiLandOrderPhoto(
                FileName: lecteur.GetString(0),
                OriginalFileName: lecteur.GetString(1),
                Quantity: lecteur.GetInt32(2),
                ApplyCrop: lecteur.GetInt64(3) != 0,
                CropX: lecteur.GetDouble(4),
                CropY: lecteur.GetDouble(5),
                CropWidth: lecteur.GetDouble(6),
                CropHeight: lecteur.GetDouble(7),
                Angle: lecteur.GetDouble(8)));
        }

        return photos;
    }

    /// <summary>Emplacement d'une photo sur le disque, dans le dossier de sa commande.</summary>
    public string PhotoPath(DiLandOrder order, DiLandOrderPhoto photo)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(photo);

        return Path.Combine(OrdersDirectory, order.DirectoryName, "F", photo.FileName);
    }

    private SqliteConnection OpenSnapshot()
    {
        var connexion = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = SnapshotPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connexion.Open();
        return connexion;
    }

    /// <summary>Identifiant de la commande la plus récente, pour démarrer sans tout relire.</summary>
    public long LastOrderId()
    {
        if (!File.Exists(SnapshotPath)) return 0;

        using var connexion = OpenSnapshot();
        using var commande = connexion.CreateCommand();
        commande.CommandText = """SELECT COALESCE(MAX(Oid), 0) FROM "Order" WHERE GCRecord IS NULL""";
        return Convert.ToInt64(commande.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Photos d'origine d'une commande. DiLand les range dans le sous-dossier <c>F</c> ;
    /// les fichiers préfixés <c>O_</c> sont ses propres dérivés, on les écarte.
    /// </summary>
    public IReadOnlyList<string> PhotosOf(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var dossier = Path.Combine(OrdersDirectory, order.DirectoryName, "F");
        if (!Directory.Exists(dossier)) return [];

        return Directory.EnumerateFiles(dossier)
            .Where(f => !Path.GetFileName(f).StartsWith("O_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private int CountPhotos(string directoryName)
    {
        var dossier = Path.Combine(OrdersDirectory, directoryName, "F");
        if (!Directory.Exists(dossier)) return 0;

        return Directory.EnumerateFiles(dossier)
            .Count(f => !Path.GetFileName(f).StartsWith("O_", StringComparison.OrdinalIgnoreCase));
    }
}
