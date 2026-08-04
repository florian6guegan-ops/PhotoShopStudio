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
/// <param name="Angle">Rotation appliquée à la borne, en degrés (0, 90, 180, 270).</param>
/// <param name="FineRotationDegrees">
/// Redressement fin appliqué à la borne, en degrés — le « Tilt ». Les bornes le proposent
/// bel et bien : 113 des 1231 images de la base de la boutique en portent un, de −5° à
/// +7° (relevé du 03/08/2026). Il était ignoré, et ces photos sortaient de travers.
/// </param>
public sealed record DiLandOrderPhoto(
    string FileName,
    string OriginalFileName,
    int Quantity,
    bool ApplyCrop,
    double CropX,
    double CropY,
    double CropWidth,
    double CropHeight,
    double Angle,
    double FineRotationDegrees = 0)
{
    /// <summary>
    /// Nom à montrer à l'opérateur. Le fichier stocké est un identifiant illisible ; le nom
    /// d'origine du client permet de retrouver la photo dont il parle.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(OriginalFileName) ? FileName : OriginalFileName;

    /// <summary>
    /// Fabrique une photo à partir des valeurs BRUTES de DiLand, en ramenant le recadrage
    /// en fractions de l'image.
    ///
    /// <b>DiLand exprime les recadrages en PIXELS de l'image source, pas en fractions.</b>
    /// Le code les a longtemps pris pour des fractions : <c>CropSpec(0, 44, 1536, 1958)</c>
    /// ne passait alors pas <c>IsValid</c>, on retombait sur l'image entière, et TOUS les
    /// recadrages faits par les clients aux bornes étaient silencieusement perdus. Relevé
    /// le 03/08/2026 sur la base de la boutique : 1231 images, 986 recadrées, et pas une
    /// seule dont <c>CropWidth</c> soit ≤ 1.
    ///
    /// La conversion est ici, et non chez l'appelant, parce que DEUX lecteurs la
    /// réclament — la base (voir <see cref="DiLandRepository"/>) et le fichier
    /// <c>Order.xml</c> — et que deux conversions finiraient par diverger.
    /// </summary>
    /// <param name="sourceWidth">Largeur de l'image en pixels, telle que DiLand l'a notée.</param>
    /// <param name="sourceHeight">Hauteur de l'image en pixels.</param>
    public static DiLandOrderPhoto FromRaw(
        string fileName, string originalFileName, int quantity,
        bool applyCrop, double cropX, double cropY, double cropWidth, double cropHeight,
        double angle, double fineRotationDegrees, int sourceWidth, int sourceHeight)
    {
        var (x, y, largeur, hauteur) = EnFractions(
            cropX, cropY, cropWidth, cropHeight, sourceWidth, sourceHeight);

        return new DiLandOrderPhoto(
            fileName, originalFileName, quantity,
            applyCrop, x, y, largeur, hauteur,
            angle, fineRotationDegrees);
    }

    /// <summary>
    /// Ramène un recadrage en fractions de l'image.
    ///
    /// Les valeurs sont tenues pour des PIXELS dès que la définition de la source est
    /// connue et que la largeur dépasse 1 : un recadrage d'un pixel de large n'existe pas,
    /// donc la question ne se pose jamais sur un cas réel. Sans définition, ou sur des
    /// valeurs déjà fractionnaires, on les rend telles quelles — une version future de
    /// DiLand pourrait changer d'unité, et ce serait alors le seul endroit à revoir.
    /// </summary>
    internal static (double X, double Y, double Width, double Height) EnFractions(
        double cropX, double cropY, double cropWidth, double cropHeight,
        int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (cropX, cropY, cropWidth, cropHeight);
        if (cropWidth <= 1 && cropHeight <= 1) return (cropX, cropY, cropWidth, cropHeight);

        return (cropX / sourceWidth, cropY / sourceHeight,
                cropWidth / sourceWidth, cropHeight / sourceHeight);
    }
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

    /// <summary>
    /// Où les bornes déposent, avant que DiLand n'intègre. Un paquet complet y porte
    /// l'extension <c>.COM</c> ; un <c>.TMP</c> est encore en cours de réception.
    /// </summary>
    public string IncomingOrdersDirectory => Path.Combine(_root, "IncomingOrders");

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
    /// Les commandes de bornes lues SUR LE DISQUE, dans leur <c>Order.xml</c> — sans la
    /// base, et sans que DiLand tourne.
    ///
    /// Deux dossiers, et les deux comptent :
    ///
    /// - <c>IncomingOrders</c> : arrivées, pas encore intégrées. C'est le cas quand DiLand
    ///   est fermé, quand il vient de tomber, ou quand sa tâche d'import est bloquée — et
    ///   il tombe presque tous les jours. Ces commandes-là n'existaient nulle part pour
    ///   nous ;
    /// - <c>Orders</c> : intégrées. La base les connaît aussi, mais le disque reste lisible
    ///   quand elle est verrouillée, abîmée, ou purgée de cette commande.
    ///
    /// Les <c>.TMP</c> sont écartés : la borne écrit encore dedans, et DiLand ne les
    /// renomme en <c>.COM</c> qu'une fois le transfert complet.
    /// </summary>
    public IReadOnlyList<DiLandOrderXml.Contenu> ReadKioskOrdersFromDisk(int limit = 200)
    {
        var trouvees = new List<DiLandOrderXml.Contenu>();

        foreach (var racine in new[] { IncomingOrdersDirectory, OrdersDirectory })
        {
            if (!Directory.Exists(racine)) continue;

            foreach (var dossier in Directory.EnumerateDirectories(racine))
            {
                if (!dossier.EndsWith(".COM", StringComparison.OrdinalIgnoreCase)) continue;
                if (!DiLandOrderXml.Porte(dossier)) continue;

                if (DiLandOrderXml.Lire(dossier) is { } contenu && contenu.Order.IsComplete)
                    trouvees.Add(contenu);
            }
        }

        return trouvees
            .OrderBy(c => c.Order.Date)
            .TakeLast(limit)
            .ToList();
    }

    /// <summary>
    /// Le dossier d'une commande, où qu'il soit — intégré ou encore en attente d'intégration.
    ///
    /// Les commandes lues sur le disque peuvent vivre dans <c>IncomingOrders</c>, où
    /// <see cref="OrdersDirectory"/> ne les trouverait pas.
    /// </summary>
    private string OrderDirectory(DiLandOrder order)
    {
        var integre = Path.Combine(OrdersDirectory, order.DirectoryName);
        if (Directory.Exists(integre)) return integre;

        var entrant = Path.Combine(IncomingOrdersDirectory, order.DirectoryName);
        return Directory.Exists(entrant) ? entrant : integre;
    }

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

        // Width et Height sont indispensables, et non décoratifs : le recadrage est en
        // PIXELS et ne se ramène en fractions que par eux (voir DiLandOrderPhoto.FromRaw).
        commande.CommandText = """
            SELECT FileName, COALESCE(OriginalFileName, ''), COALESCE(Quantity, 1),
                   COALESCE(ApplyCrop, 0), COALESCE(CropX, 0), COALESCE(CropY, 0),
                   COALESCE(CropWidth, 1), COALESCE(CropHeight, 1), COALESCE(Angle, 0),
                   COALESCE(FineRotationAngle, 0), COALESCE(Width, 0), COALESCE(Height, 0)
            FROM OrderLineImage
            WHERE OrderLine = $ligne AND GCRecord IS NULL
              AND FileName IS NOT NULL AND FileName <> ''
            ORDER BY Oid
            """;
        commande.Parameters.AddWithValue("$ligne", ligne);

        using var lecteur = commande.ExecuteReader();
        while (lecteur.Read())
        {
            photos.Add(DiLandOrderPhoto.FromRaw(
                fileName: lecteur.GetString(0),
                originalFileName: lecteur.GetString(1),
                quantity: lecteur.GetInt32(2),
                applyCrop: lecteur.GetInt64(3) != 0,
                cropX: lecteur.GetDouble(4),
                cropY: lecteur.GetDouble(5),
                cropWidth: lecteur.GetDouble(6),
                cropHeight: lecteur.GetDouble(7),
                angle: lecteur.GetDouble(8),
                fineRotationDegrees: lecteur.GetDouble(9),
                sourceWidth: lecteur.GetInt32(10),
                sourceHeight: lecteur.GetInt32(11)));
        }

        return photos;
    }

    /// <summary>
    /// Tous les produits que DiLand connaît, pas seulement ceux déjà vendus.
    ///
    /// Sert à vérifier la couverture du catalogue Studio : un format proposé en borne mais
    /// absent de chez nous ferait perdre une ligne le jour où un client le commande.
    /// </summary>
    public IReadOnlyList<string> AllProductNames()
    {
        if (!File.Exists(SnapshotPath)) return [];

        var noms = new List<string>();

        using var connexion = OpenSnapshot();
        using var commande = connexion.CreateCommand();
        commande.CommandText = """
            SELECT DISTINCT Name FROM Product
            WHERE GCRecord IS NULL AND Name IS NOT NULL AND Name <> ''
            ORDER BY Name
            """;

        using var lecteur = commande.ExecuteReader();
        while (lecteur.Read()) noms.Add(lecteur.GetString(0));

        return noms;
    }

    /// <summary>
    /// Marque que DiLand ajoute au NOM DES FICHIERS d'une commande qu'il a traitée.
    ///
    /// Tout y passe : <c>Order.xml</c> devient <c>Order.xml_p</c>, et chaque photo
    /// <c>F\xxx.jpg</c> devient <c>F\xxx.jpg_p</c>. Sa base, elle, garde le nom d'origine
    /// — c'est donc au lecteur de faire le rapprochement.
    /// </summary>
    private const string SuffixeTraite = "_p";

    /// <summary>
    /// Emplacement d'une photo sur le disque, dans le dossier de sa commande.
    ///
    /// On essaie le nom de la base, puis le nom marqué <see cref="SuffixeTraite"/>. Sans
    /// cette reprise, TOUTE commande déjà traitée par DiLand devenait inouvrable : la
    /// base annonçait ses photos, le disque les avait sous un autre nom, et l'écran
    /// répondait « aucune photo n'a pu être récupérée » (constaté le 01/08/2026 sur les
    /// neuf commandes en attente, sauf la seule que DiLand n'avait pas encore touchée).
    ///
    /// Le chemin nu est rendu quand aucun des deux n'existe : à l'appelant de constater
    /// que le fichier manque, comme avant.
    /// </summary>
    public string PhotoPath(DiLandOrder order, DiLandOrderPhoto photo)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(photo);

        var attendu = Path.Combine(OrderDirectory(order), "F", photo.FileName);
        if (File.Exists(attendu)) return attendu;

        var traite = attendu + SuffixeTraite;
        return File.Exists(traite) ? traite : attendu;
    }

    /// <summary>
    /// Le nom sous lequel une photo doit être RECOPIÉE, débarrassé de la marque de DiLand.
    ///
    /// Recopier <c>xxx.jpg_p</c> tel quel donnerait un fichier dont l'extension n'est plus
    /// celle d'une image : plus rien ne le reconnaîtrait comme une photo, ni la planche de
    /// vignettes ni le catalogue de formats.
    /// </summary>
    public static string CleanFileName(string fileName) =>
        fileName.EndsWith(SuffixeTraite, StringComparison.Ordinal)
            ? fileName[..^SuffixeTraite.Length]
            : fileName;

    /// <summary>
    /// Exécute une requête de LECTURE sur la copie de la base, et rend les lignes telles
    /// quelles.
    ///
    /// Ouverte au diagnostic : la base de DiLand porte des informations qu'aucune API ne
    /// donne — notamment ce qu'il envoie réellement au minilab pour un format donné. Elle
    /// travaille sur la COPIE, comme tout le reste de cette classe : la base de DiLand
    /// n'est jamais ouverte.
    /// </summary>
    /// <param name="sql">Requête de lecture. L'appelant est responsable de son innocuité.</param>
    /// <param name="maxLignes">Garde-fou : une table de commandes compte des dizaines de milliers de lignes.</param>
    public IReadOnlyList<IReadOnlyList<string>> Interroger(string sql, int maxLignes = 200)
    {
        if (!File.Exists(SnapshotPath)) return [];

        using var connexion = OpenSnapshot();
        connexion.Open();

        using var commande = connexion.CreateCommand();
        commande.CommandText = sql;

        var lignes = new List<IReadOnlyList<string>>();
        using var lecteur = commande.ExecuteReader();

        // l'en-tête d'abord : sans les noms de colonnes, une sonde ne dit rien
        lignes.Add([.. Enumerable.Range(0, lecteur.FieldCount).Select(lecteur.GetName)]);

        while (lecteur.Read() && lignes.Count <= maxLignes)
            lignes.Add([.. Enumerable.Range(0, lecteur.FieldCount)
                .Select(i => lecteur.IsDBNull(i) ? "" : lecteur.GetValue(i).ToString() ?? "")]);

        return lignes;
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

        var dossier = Path.Combine(OrderDirectory(order), "F");
        if (!Directory.Exists(dossier)) return [];

        return Directory.EnumerateFiles(dossier)
            .Where(f => !Path.GetFileName(f).StartsWith("O_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Vrai si le fichier porte la marque des commandes déjà traitées par DiLand.</summary>
    public static bool IsProcessedName(string fileName) =>
        fileName.EndsWith(SuffixeTraite, StringComparison.Ordinal);

    /// <summary>
    /// Longueur du début de fichier que DiLand brouille, en octets.
    ///
    /// Relevée le 01/08/2026 sur les commandes de la boutique : exactement 1024. Une photo
    /// de 1 Mo ne se décode avec AUCUNE autre valeur — un préfixe plus court laisse du
    /// brouillage dans l'en-tête, un plus long abîme les données d'image qui suivent.
    /// </summary>
    private const int LongueurBrouillee = 1024;

    /// <summary>Clé du brouillage : un simple XOR, le même octet partout.</summary>
    private const byte CleBrouillage = 0x07;

    /// <summary>
    /// Recopie une photo de DiLand en la remettant en clair.
    ///
    /// <b>DiLand ne se contente pas de renommer les fichiers d'une commande traitée : il
    /// en BROUILLE le début.</b> Les 1024 premiers octets sont passés au XOR 0x07, ce qui
    /// détruit l'en-tête de l'image — <c>FF D8 FF E0</c> devient <c>F8 DF F8 E7</c>. Le
    /// fichier reste une photo entière, mais plus aucun logiciel ne sait l'ouvrir.
    ///
    /// C'est ce qui restait après la correction du nom : les commandes s'ouvraient enfin,
    /// et chaque photo était aussitôt écartée comme illisible (01/08/2026).
    ///
    /// La copie est refaite à chaque fois pour un fichier brouillé, sans quoi une copie
    /// abîmée par une version antérieure resterait en place indéfiniment.
    /// </summary>
    /// <param name="ecraser">
    /// Refaire la copie même si le fichier est déjà là. Sans cela, un second
    /// téléchargement demandé par l'opérateur ne rendait que la copie précédente.
    /// </param>
    public void CopyPhotoTo(DiLandOrder order, DiLandOrderPhoto photo, string destination,
        bool ecraser = false)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(photo);

        CopyFileTo(PhotoPath(order, photo), destination, ecraser);
    }

    /// <summary>
    /// Recopie un fichier de DiLand en le remettant en clair, quel que soit le chemin par
    /// lequel on l'a trouvé — par la base, ou en balayant le dossier de la commande.
    /// </summary>
    public void CopyFileTo(string source, string destination, bool ecraser = false)
    {
        if (!IsProcessedName(source))
        {
            if (ecraser || !File.Exists(destination)) File.Copy(source, destination, overwrite: true);
            return;
        }

        // Un fichier brouillé est TOUJOURS réécrit : une copie abîmée par une version
        // antérieure resterait sinon en place indéfiniment.
        var octets = File.ReadAllBytes(source);
        var combien = Math.Min(LongueurBrouillee, octets.Length);
        for (var i = 0; i < combien; i++) octets[i] ^= CleBrouillage;

        File.WriteAllBytes(destination, octets);
    }

    private int CountPhotos(string directoryName)
    {
        var dossier = Path.Combine(OrdersDirectory, directoryName, "F");
        if (!Directory.Exists(dossier)) return 0;

        return Directory.EnumerateFiles(dossier)
            .Count(f => !Path.GetFileName(f).StartsWith("O_", StringComparison.OrdinalIgnoreCase));
    }
}
