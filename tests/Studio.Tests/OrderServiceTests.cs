using Studio.Core.Domain;
using Studio.Store;

namespace Studio.Tests;

public class OrderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioTests-" + Guid.NewGuid().ToString("N"));
    private readonly OrderFolderStore _store;
    private readonly OrderService _service;

    private static readonly Product P10x15 = new()
    { Code = "10x15", Name = "10×15", WidthMm = 102, HeightMm = 152, PrinterName = "DP-DS620", Price = 0.25m };

    private static readonly Product P20x30 = new()
    { Code = "20x30", Name = "20×30", WidthMm = 203, HeightMm = 305, PrinterName = "FUJIFILM DE100", Price = 4m };

    public OrderServiceTests()
    {
        Directory.CreateDirectory(_root);
        _store = new OrderFolderStore(Path.Combine(_root, "orders"));
        _service = new OrderService(_store, new DailyCounter(Path.Combine(_root, "daily.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakePhoto(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }); // en-tête JPEG factice
        return path;
    }

    private static DraftItem Draft(string path, Product product, int qty = 1) =>
        new(path, product, qty, CropSpec.Full, 0, 0, null, new ImageAdjustments());

    /// <summary>
    /// Une planche d'identité est facturée d'après le DOCUMENT et non d'après le papier :
    /// 10 € pour un document français, 15 € pour un étranger, sur le même produit du
    /// catalogue. Voir <see cref="TarifsIdentite"/>.
    /// </summary>
    [Fact]
    public void CreateOrder_UnPrixImpose_lEmporteSurLeCatalogue()
    {
        var photo = MakePhoto("identite.jpg");

        var order = _service.CreateOrder("Operateur",
        [
            Draft(photo, P10x15) with { UnitPriceOverride = 15m },
        ]);

        var ligne = order.Envelopes.Single().Lines.Single();
        Assert.Equal(15m, ligne.UnitPrice);
        Assert.Equal(15m, order.Total);
    }

    [Fact]
    public void CreateOrder_SansPrixImpose_leCatalogueDecide()
    {
        var photo = MakePhoto("tirage.jpg");

        var order = _service.CreateOrder("Operateur", [Draft(photo, P10x15, 2)]);

        Assert.Equal(0.25m, order.Envelopes.Single().Lines.Single().UnitPrice);
    }

    [Fact]
    public void CreateOrder_GroupsEnvelopesByPrinterChannel()
    {
        var a = MakePhoto("a.jpg");
        var b = MakePhoto("b.jpg");

        var order = _service.CreateOrder("Operateur", new[]
        {
            Draft(a, P10x15, 2),
            Draft(b, P10x15),
            Draft(a, P20x30),
        });

        Assert.Equal(2, order.Envelopes.Count); // DS620 et DE100
        var ds620 = order.Envelopes.Single(e => e.PrinterChannel == "DP-DS620");
        Assert.Equal(3, ds620.Lines.Single().TotalPrints);
        Assert.Equal(1, order.DailyNumber);
        Assert.Equal(0.25m * 3 + 4m, order.Total);
    }

    [Fact]
    public void CreateOrder_CopiesEachSourceFileOnce()
    {
        var a = MakePhoto("a.jpg");

        var order = _service.CreateOrder("Operateur", new[]
        {
            Draft(a, P10x15),
            Draft(a, P20x30), // même photo, deux produits
        });

        var photos = Directory.GetFiles(_store.GetPhotosFolder(order));
        Assert.Single(photos); // copiée une seule fois
        // les deux items pointent vers le même fichier copié
        var items = order.Envelopes.SelectMany(e => e.Lines).SelectMany(l => l.Items).ToList();
        Assert.Equal(items[0].FileName, items[1].FileName);
        Assert.Equal("a.jpg", items[0].OriginalName);
    }

    [Fact]
    public void CreateOrder_PersistsAndReloads()
    {
        var order = _service.CreateOrder("Borne1", new[] { Draft(MakePhoto("x.jpg"), P10x15) });

        var reloaded = _store.Load(_store.GetOrderFolder(order));
        Assert.NotNull(reloaded);
        Assert.Equal(OrderStatus.Submitted, reloaded!.Status);
        Assert.Equal("Borne1", reloaded.Source);
    }

    [Fact]
    public void DailyNumbers_AreSequential()
    {
        var a = _service.CreateOrder("Operateur", new[] { Draft(MakePhoto("1.jpg"), P10x15) });
        var b = _service.CreateOrder("Operateur", new[] { Draft(MakePhoto("2.jpg"), P10x15) });
        Assert.Equal(a.DailyNumber + 1, b.DailyNumber);
    }

    /// <summary>
    /// DEUX TAILLES LIBRES SUR LE MÊME PAPIER FONT DEUX LIGNES.
    ///
    /// C'est le cas courant : un 7 × 10 et un 5,5 × 8 se casent tous deux sur du 10×15, donc
    /// sur le même produit du catalogue. Le regroupement se faisait sur le seul code
    /// produit et la ligne prenait <c>productGroup.First().CustomSheet</c> — la seconde
    /// taille aurait été tirée aux cotes de la première, sans un mot.
    ///
    /// Le défaut était hors d'atteinte tant que l'écran imposait une taille unique à toute
    /// la commande. Il a cessé de le faire le 20/08/2026 : « j'ai voulu mélanger 10×15 et
    /// 7 × 10, tout est sorti en 7 × 10 ».
    /// </summary>
    [Fact]
    public void Deux_tailles_libres_sur_le_meme_papier_font_deux_lignes()
    {
        var sept = new CustomSheetSpec(70, 100, SheetCount: 1, CellBorderMm: 0);
        var cinq = new CustomSheetSpec(55, 80, SheetCount: 1, CellBorderMm: 0);

        var commande = _service.CreateOrder("Operateur",
        [
            Draft(MakePhoto("a.jpg"), P10x15) with { CustomSheet = sept },
            Draft(MakePhoto("b.jpg"), P10x15) with { CustomSheet = cinq },
        ]);

        var lignes = commande.Envelopes.SelectMany(e => e.Lines).ToList();

        Assert.Equal(2, lignes.Count);
        Assert.All(lignes, l => Assert.Equal("10x15", l.ProductCode));

        // chaque ligne garde SES cotes de case
        Assert.Contains(lignes, l => l.CustomCellWidthMm == 70 && l.CustomCellHeightMm == 100);
        Assert.Contains(lignes, l => l.CustomCellWidthMm == 55 && l.CustomCellHeightMm == 80);

        // et chacune sa photo, aucune n'a été absorbée par l'autre
        Assert.All(lignes, l => Assert.Single(l.Items));
    }

    /// <summary>
    /// La contrepartie : deux photos de la MÊME taille libre restent UNE ligne. Sans quoi
    /// chaque photo se facturerait sa propre planche.
    /// </summary>
    [Fact]
    public void Deux_photos_de_la_meme_taille_libre_restent_une_ligne()
    {
        var sept = new CustomSheetSpec(70, 100, SheetCount: 1, CellBorderMm: 0);

        var commande = _service.CreateOrder("Operateur",
        [
            Draft(MakePhoto("a.jpg"), P10x15) with { CustomSheet = sept },
            Draft(MakePhoto("b.jpg"), P10x15) with { CustomSheet = sept },
        ]);

        var ligne = Assert.Single(commande.Envelopes.SelectMany(e => e.Lines));
        Assert.Equal(2, ligne.Items.Count);
        Assert.Equal(70, ligne.CustomCellWidthMm);
    }
}
