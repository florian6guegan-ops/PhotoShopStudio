using System.IO;
using System.Text.Json;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Core.Mail;
using Studio.Imaging;
using Studio.Imaging.Faces;
using Studio.App.Infrastructure;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;
using Studio.Store;
using Studio.Store.DiLand;
using Studio.Web;

namespace Studio.App;

/// <summary>Mode de l'exécutable (config/mode.json) : poste opérateur ou borne client.</summary>
public sealed class ModeConfig
{
    public string Mode { get; set; } = "operateur";
    /// <summary>Borne : URL du poste opérateur (API commandes).</summary>
    public string OperatorUrl { get; set; } = "http://127.0.0.1:8123";
    public string BorneName { get; set; } = "Borne1";
    /// <summary>Code de sortie staff du mode borne.</summary>
    public string StaffPin { get; set; } = "2468";

    public bool IsKiosk => Mode.Equals("borne", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Composition de l'application : chemins de données et services partagés.</summary>
public sealed class AppServices
{
    public required string DataRoot { get; init; }
    public string CatalogDir => Path.Combine(DataRoot, "catalog");
    public string ProductsJson => Path.Combine(CatalogDir, "products.json");

    public required ProductCatalog Catalog { get; set; }
    public required OrderFolderStore Store { get; init; }
    public required OrderService Orders { get; init; }
    public required PrintOrchestrator Printer { get; set; }

    private PendingPrintQueue? _attente;

    /// <summary>
    /// Commandes en attente d'imprimante, reprises toutes seules dès que la machine répond.
    ///
    /// Reconstruite quand l'orchestrateur l'est (rechargement du catalogue) : elle tient
    /// une référence dessus, et une file branchée sur l'ancien n'imprimerait plus rien.
    /// </summary>
    public PendingPrintQueue Attente => _attente ??=
        new PendingPrintQueue(Printer, () => Orders.Recent(30)) { Log = m => FileLog.Write(m) };

    /// <summary>Accès au minilab Fuji, partagé avec l'orchestrateur d'impression.</summary>
    public required De100BridgePrinter Minilab { get; init; }
    public required ThumbnailService Thumbnails { get; init; }

    private DiLandImporter? _diland;

    /// <summary>
    /// Reprise des commandes déposées par les bornes dans DiLand.
    ///
    /// Tant que DiLand tourne en boutique, les bornes lui envoient les commandes ; on les
    /// récupère sans les lui prendre. Construit au premier usage : le catalogue peut être
    /// rechargé, et le dépôt DiLand peut être absent sur un poste de développement.
    /// </summary>
    public DiLandImporter DiLandImport => _diland ??= new DiLandImporter(
        new DiLandRepository(DiLandRepository.DefaultRoot, Path.Combine(DataRoot, "diland")),
        Orders,
        Catalog.All.ToList(),
        Path.Combine(DataRoot, "diland", "reprises.json"));

    private readonly Lazy<FaceDetector> _faces = new(() => new FaceDetector(
        Path.Combine(AppContext.BaseDirectory, "models", "face_detection_yunet_2023mar.onnx")));

    /// <summary>Détecteur de visage (YuNet), chargé au premier usage.</summary>
    public FaceDetector Faces => _faces.Value;

    /// <summary>Serveur d'upload téléphone + API bornes.</summary>
    public required UploadServer Upload { get; init; }

    public required ModeConfig Mode { get; init; }
    public required TicketConfig Ticket { get; init; }
    public required BackupConfig Backup { get; init; }

    /// <summary>Dossier des fichiers de configuration, propre à ce poste.</summary>
    public string ConfigDir => Path.Combine(DataRoot, "config");

    private MailSettings? _mail;

    /// <summary>
    /// Réglages d'envoi des photos par courriel.
    ///
    /// Ils vivent dans les DONNÉES du poste (<c>config\mail.json</c>) et non dans le
    /// dépôt, qui est public : ils portent un mot de passe d'application. C'est aussi ce
    /// qui permet à chaque poste opérateur d'avoir les siens — un nouveau poste se
    /// configure depuis l'écran Paramètres, sans toucher au code ni au catalogue.
    /// </summary>
    public MailSettings Mail => _mail ??= MailSettings.Load(ConfigDir);

    /// <summary>Enregistre les réglages de courriel et les reprend aussitôt.</summary>
    public void SaveMail(MailSettings reglages)
    {
        MailSettings.Save(ConfigDir, reglages);
        _mail = reglages;
    }

    /// <summary>
    /// Le produit « envoi par courriel », créé au catalogue à la première utilisation.
    ///
    /// Passe par le catalogue comme n'importe quel tarif : c'est une ligne de caisse, elle
    /// doit figurer au ticket, au total et aux statistiques (voir <see cref="MailProduct"/>).
    /// </summary>
    public Product ProduitEnvoiCourriel() => MailProduct.Obtenir(Catalog, produit =>
    {
        ProductCatalog.Save(ProductsJson, Catalog.All.Append(produit));
        ReloadCatalog();
    });

    /// <summary>
    /// Le WiFi du magasin, pour le code QR de connexion de l'écran « téléphone ».
    ///
    /// Il est saisi à la main parce que ce poste n'a PAS de carte sans fil : Windows n'a
    /// donc aucun profil à lire. Laissé vide, on retombe sur la lecture automatique, qui
    /// vaudra pour un poste portable.
    /// </summary>
    public required WifiConfig Wifi { get; init; }

    /// <summary>
    /// Les impressions en cours. Partagé par toute l'application : c'est lui qui permet
    /// de rendre la main à l'opérateur pendant qu'une commande s'imprime.
    /// </summary>
    public SuiviImpressions Impressions { get; } = new();

    private bool _uploadStarted;

    /// <summary>Démarre Kestrel, ouvre le pare-feu et branche l'API bornes, une seule fois.</summary>
    public async Task EnsureUploadServerAsync()
    {
        if (_uploadStarted) return;

        Upload.KioskOrders = new KioskOrderReceiver(Catalog, Orders,
            await Task.Run(() => Store.ScanRecent(days: 3)));
        Upload.KioskOrders.OrderReceived += order =>
        {
            if (!Ticket.Enabled) return;
            try
            {
                EscPosTicket.Send(EscPosTicket.Build(order, Catalog, Ticket), Ticket);
            }
            catch
            {
                // ticket indisponible : la commande est déjà créée, l'opérateur peut réimprimer le ticket
            }
        };

        await Upload.StartAsync();
        _uploadStarted = true;
        // la règle pare-feu peut être lente (netsh) : après coup, hors du fil d'interface —
        // le serveur écoute déjà, seul l'accès depuis le réseau en dépend
        _ = Task.Run(() => Firewall.EnsureRule(Upload.Port));
    }

    /// <summary>Entretien au démarrage (poste opérateur) : archivage des vieilles commandes + sauvegarde si échue.</summary>
    public void RunMaintenanceInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                Archiver.ArchiveOldOrders(
                    Path.Combine(DataRoot, "orders"),
                    Path.Combine(DataRoot, "archive"));
            }
            catch
            {
                // l'archivage réessaiera au prochain démarrage
            }
            try
            {
                BackupRunner.RunIfDue(DataRoot, Backup);
            }
            catch
            {
                // sauvegarde indisponible (NAS éteint…) : réessai au prochain démarrage
            }
        });
    }

    /// <summary>Charge (ou crée avec ses valeurs par défaut) un fichier de config JSON.</summary>
    private static T LoadConfig<T>(string path) where T : new()
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ProductCatalog.JsonOptions) ?? new T();
            }
            catch (JsonException)
            {
                return new T(); // config corrompue : valeurs par défaut, on n'écrase pas le fichier
            }
        }
        var fresh = new T();
        File.WriteAllText(path, JsonSerializer.Serialize(fresh, ProductCatalog.JsonOptions));
        return fresh;
    }

    public static AppServices Load(string dataRoot = @"D:\PhotoStudioData")
    {
        foreach (var sub in new[] { "orders", "catalog", Path.Combine("catalog", "icc"), "counters", "config", "logs", "cache", "incoming" })
            Directory.CreateDirectory(Path.Combine(dataRoot, sub));

        var productsJson = Path.Combine(dataRoot, "catalog", "products.json");
        if (!File.Exists(productsJson))
            ProductCatalog.Save(productsJson, ProductCatalog.CreateDefaultProducts());

        var catalog = ProductCatalog.Load(productsJson);
        var store = new OrderFolderStore(Path.Combine(dataRoot, "orders"));
        var counter = new DailyCounter(Path.Combine(dataRoot, "counters", "daily.json"));

        MagickInit.Configure();

        var minilab = new De100BridgePrinter { Log = message => FileLog.Write(message) };

        // Les agrandissements journalisaient déjà média, placement et durées — dans le vide,
        // faute d'abonné. C'est la seule trace du temps passé à réduire, convertir et spouler.
        Studio.Printing.LargeFormat.LargeFormatPrinter.Log = message => FileLog.Write(message);

        // Un envoi refusé par le serveur ne laissait aucune trace : le client repartait en
        // croyant avoir ses photos, et on n'avait rien à relire le lendemain.
        PhotoMailer.Log = message => FileLog.Write(message);

        var services = new AppServices
        {
            DataRoot = dataRoot,
            Catalog = catalog,
            Store = store,
            Orders = new OrderService(store, counter),
            // le minilab se connecte à la demande : construire l'objet ne démarre pas le relais
            Minilab = minilab,
            Printer = new PrintOrchestrator(catalog, store, Path.Combine(dataRoot, "catalog"), minilab)
            {
                Log = message => FileLog.Write(message),
            },
            Thumbnails = new ThumbnailService(Path.Combine(dataRoot, "cache")),
            Upload = new UploadServer(Path.Combine(dataRoot, "incoming")),
            Mode = LoadConfig<ModeConfig>(Path.Combine(dataRoot, "config", "mode.json")),
            Ticket = LoadConfig<TicketConfig>(Path.Combine(dataRoot, "config", "ticket.json")),
            Backup = LoadConfig<BackupConfig>(Path.Combine(dataRoot, "config", "backup.json")),
            Wifi = LoadConfig<WifiConfig>(Path.Combine(dataRoot, "config", "wifi.json")),
        };

        // Ce que la MACHINE a réellement sorti, par opposition à ce qu'on lui a envoyé.
        // L'événement arrive du relais, donc d'un fil de fond : le suivi, lui, est lu par
        // l'interface — on repasse par le répartiteur avant d'y toucher.
        minilab.JobFinished += (_, resultat) =>
        {
            var reussi = resultat.Outcome == De100JobOutcome.Printed;
            var repartiteur = System.Windows.Application.Current?.Dispatcher;

            if (repartiteur is null)
                services.Impressions.TirageTermine(resultat.JobId, reussi);
            else
                repartiteur.BeginInvoke(() => services.Impressions.TirageTermine(resultat.JobId, reussi));
        };

        return services;
    }

    /// <summary>Après modification du catalogue : recharge et recâble l'impression.</summary>
    public void ReloadCatalog()
    {
        Catalog = ProductCatalog.Load(ProductsJson);
        Printer = new PrintOrchestrator(Catalog, Store, CatalogDir) { Log = message => FileLog.Write(message) };

        // la file tient l'ancien orchestrateur : la laisser en place ferait imprimer sur
        // un catalogue périmé
        _attente = null;
    }
}
