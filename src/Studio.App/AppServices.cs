using System.IO;
using System.Text.Json;
using Studio.Core.Catalog;
using Studio.Imaging;
using Studio.Imaging.Faces;
using Studio.Printing;
using Studio.Store;
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
    public required ThumbnailService Thumbnails { get; init; }

    private readonly Lazy<FaceDetector> _faces = new(() => new FaceDetector(
        Path.Combine(AppContext.BaseDirectory, "models", "face_detection_yunet_2023mar.onnx")));

    /// <summary>Détecteur de visage (YuNet), chargé au premier usage.</summary>
    public FaceDetector Faces => _faces.Value;

    /// <summary>Serveur d'upload téléphone + API bornes.</summary>
    public required UploadServer Upload { get; init; }

    public required ModeConfig Mode { get; init; }
    public required TicketConfig Ticket { get; init; }

    private bool _uploadStarted;

    /// <summary>Démarre Kestrel, ouvre le pare-feu et branche l'API bornes, une seule fois.</summary>
    public async Task EnsureUploadServerAsync()
    {
        if (_uploadStarted) return;

        Upload.KioskOrders = new KioskOrderReceiver(Catalog, Orders, Store.ScanRecent(days: 3));
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

        Firewall.EnsureRule(Upload.Port);
        await Upload.StartAsync();
        _uploadStarted = true;
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

        return new AppServices
        {
            DataRoot = dataRoot,
            Catalog = catalog,
            Store = store,
            Orders = new OrderService(store, counter),
            Printer = new PrintOrchestrator(catalog, store, Path.Combine(dataRoot, "catalog")),
            Thumbnails = new ThumbnailService(Path.Combine(dataRoot, "cache")),
            Upload = new UploadServer(Path.Combine(dataRoot, "incoming")),
            Mode = LoadConfig<ModeConfig>(Path.Combine(dataRoot, "config", "mode.json")),
            Ticket = LoadConfig<TicketConfig>(Path.Combine(dataRoot, "config", "ticket.json")),
        };
    }

    /// <summary>Après modification du catalogue : recharge et recâble l'impression.</summary>
    public void ReloadCatalog()
    {
        Catalog = ProductCatalog.Load(ProductsJson);
        Printer = new PrintOrchestrator(Catalog, Store, CatalogDir);
    }
}
