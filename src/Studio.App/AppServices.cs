using System.IO;
using Studio.Core.Catalog;
using Studio.Imaging;
using Studio.Imaging.Faces;
using Studio.Printing;
using Studio.Store;
using Studio.Web;

namespace Studio.App;

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

    /// <summary>Serveur d'upload téléphone (démarré au premier écran « Téléphone »).</summary>
    public required UploadServer Upload { get; init; }

    private bool _uploadStarted;

    /// <summary>Démarre Kestrel et ouvre le pare-feu, une seule fois.</summary>
    public async Task EnsureUploadServerAsync()
    {
        if (_uploadStarted) return;
        Firewall.EnsureRule(Upload.Port);
        await Upload.StartAsync();
        _uploadStarted = true;
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
        };
    }

    /// <summary>Après modification du catalogue : recharge et recâble l'impression.</summary>
    public void ReloadCatalog()
    {
        Catalog = ProductCatalog.Load(ProductsJson);
        Printer = new PrintOrchestrator(Catalog, Store, CatalogDir);
    }
}
