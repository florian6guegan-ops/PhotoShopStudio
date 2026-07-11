using Studio.Core.Domain;
using Studio.Store;

namespace Studio.Tests;

public class StoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioTests-" + Guid.NewGuid().ToString("N"));

    public StoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Order MakeOrder() => new()
    {
        DailyNumber = 42,
        Source = "Borne1",
        Status = OrderStatus.Submitted,
        Envelopes =
        {
            new Envelope
            {
                Number = 1,
                PrinterChannel = "DP-DS620",
                Lines =
                {
                    new OrderLine
                    {
                        ProductCode = "10x15",
                        UnitPrice = 0.25m,
                        Items = { new OrderItem { FileName = "001.jpg", OriginalName = "IMG_1234.jpg", Quantity = 3 } },
                    },
                },
            },
        },
    };

    [Fact]
    public void CreateSaveLoad_RoundTrips()
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var order = MakeOrder();

        var folder = store.Create(order);
        Assert.True(File.Exists(Path.Combine(folder, "order.json")));
        Assert.True(Directory.Exists(Path.Combine(folder, "photos")));

        var loaded = store.Load(folder);
        Assert.NotNull(loaded);
        Assert.Equal(order.Id, loaded!.Id);
        Assert.Equal(42, loaded.DailyNumber);
        Assert.Equal(OrderStatus.Submitted, loaded.Status);
        Assert.Equal(0.75m, loaded.Total);
        Assert.Equal("IMG_1234.jpg", loaded.Envelopes[0].Lines[0].Items[0].OriginalName);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNullInsteadOfCrashing()
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var order = MakeOrder();
        var folder = store.Create(order);

        File.WriteAllText(Path.Combine(folder, "order.json"), "{ tronqué au milieu de");

        Assert.Null(store.Load(folder));
    }

    [Fact]
    public void AtomicWrite_LeftoverTmp_DoesNotBreakNextWrite()
    {
        var path = Path.Combine(_root, "file.json");
        File.WriteAllText(path + ".tmp", "reste d'un crash");

        AtomicFile.WriteAllText(path, "contenu final");
        Assert.Equal("contenu final", File.ReadAllText(path));
    }

    [Fact]
    public void ScanRecent_FindsSavedOrders()
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        store.Create(MakeOrder());
        store.Create(MakeOrder());

        var orders = store.ScanRecent(days: 1);
        Assert.Equal(2, orders.Count);
    }

    [Fact]
    public void DailyCounter_IncrementsThenResetsOnNewDay()
    {
        var counter = new DailyCounter(Path.Combine(_root, "daily.json"));
        var day1 = new DateOnly(2026, 7, 11);
        var day2 = new DateOnly(2026, 7, 12);

        Assert.Equal(1, counter.Next(day1));
        Assert.Equal(2, counter.Next(day1));
        Assert.Equal(3, counter.Next(day1));
        Assert.Equal(1, counter.Next(day2)); // nouveau jour → repart à 1
    }

    [Fact]
    public void DailyCounter_CorruptFile_RestartsAtOne()
    {
        var path = Path.Combine(_root, "daily.json");
        File.WriteAllText(path, "pas du json");
        var counter = new DailyCounter(path);
        Assert.Equal(1, counter.Next(new DateOnly(2026, 7, 11)));
    }

    [Fact]
    public void DisplayNumber_FormatsDayAndCounter()
    {
        var order = new Order { DailyNumber = 7, CreatedAt = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.FromHours(2)) };
        Assert.Equal("11-007", order.DisplayNumber);
    }
}
