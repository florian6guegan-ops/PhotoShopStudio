using System.Text.Json;
using System.Text.Json.Serialization;
using Studio.Core.Domain;

namespace Studio.Core.Catalog;

/// <summary>Catalogue des produits vendables, chargé depuis catalog/products.json.</summary>
public sealed class ProductCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, Product> _byCode;

    public ProductCatalog(IEnumerable<Product> products)
    {
        _byCode = products.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<Product> All => _byCode.Values;
    public IEnumerable<Product> Enabled => _byCode.Values.Where(p => p.Enabled);

    public Product? Find(string code) => _byCode.GetValueOrDefault(code);

    public Product Require(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Produit inconnu dans le catalogue : « {code} »");

    public static ProductCatalog Load(string productsJsonPath)
    {
        using var stream = File.OpenRead(productsJsonPath);
        var products = JsonSerializer.Deserialize<List<Product>>(stream, JsonOptions)
                       ?? throw new InvalidDataException($"Catalogue vide ou illisible : {productsJsonPath}");
        return new ProductCatalog(products);
    }

    public static void Save(string productsJsonPath, IEnumerable<Product> products)
    {
        var json = JsonSerializer.Serialize(products.ToList(), JsonOptions);
        var tmp = productsJsonPath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(productsJsonPath))
            File.Replace(tmp, productsJsonPath, productsJsonPath + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, productsJsonPath);
    }

    /// <summary>Catalogue de démarrage : formats courants mappés sur Microsoft Print to PDF (aucun risque papier).</summary>
    public static List<Product> CreateDefaultProducts() => new()
    {
        new Product { Code = "10x15", Name = "Tirage 10×15", WidthMm = 102, HeightMm = 152, PrinterName = "Microsoft Print to PDF", Price = 0.25m },
        new Product { Code = "13x18", Name = "Tirage 13×18", WidthMm = 127, HeightMm = 178, PrinterName = "Microsoft Print to PDF", Price = 0.80m },
        new Product { Code = "15x20", Name = "Tirage 15×20", WidthMm = 152, HeightMm = 203, PrinterName = "Microsoft Print to PDF", Price = 1.50m },
        new Product { Code = "20x30", Name = "Agrandissement 20×30", WidthMm = 203, HeightMm = 305, PrinterName = "Microsoft Print to PDF", Price = 4.00m },
        new Product
        {
            Code = "ID-FR-6", Name = "Photos d'identité 35×45 (planche de 6)",
            WidthMm = 102, HeightMm = 152, PrinterName = "Microsoft Print to PDF", Price = 6.00m,
            Sheet = new SheetSpec { Copies = 6, CellWidthMm = 35, CellHeightMm = 45 },
        },
    };
}
