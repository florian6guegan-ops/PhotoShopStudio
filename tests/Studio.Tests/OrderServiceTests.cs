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
        new(path, product, qty, CropSpec.Full, 0, null, new ImageAdjustments());

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
}
