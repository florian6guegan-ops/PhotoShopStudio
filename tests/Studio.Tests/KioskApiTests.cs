using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Web;

namespace Studio.Tests;

public class KioskApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioKiosk-" + Guid.NewGuid().ToString("N"));
    private UploadServer _server = null!;
    private KioskClient _client = null!;

    public async Task InitializeAsync()
    {
        var catalog = new ProductCatalog(new[]
        {
            new Product { Code = "10x15", Name = "Tirage 10×15", WidthMm = 102, HeightMm = 152, PrinterName = "PDF", Price = 0.25m },
        });
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var orders = new OrderService(store, new DailyCounter(Path.Combine(_root, "daily.json")));

        _server = new UploadServer(Path.Combine(_root, "incoming"), port: 18124)
        {
            KioskOrders = new KioskOrderReceiver(catalog, orders, Enumerable.Empty<Order>()),
        };
        await _server.StartAsync();
        _client = new KioskClient("http://localhost:18124");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task SameGuid_SubmittedTwice_SameOrderNumber()
    {
        Assert.True(await _client.PingAsync());

        var photo = Path.Combine(_root, "photo.jpg");
        await File.WriteAllBytesAsync(photo, new byte[] { 0xFF, 0xD8, 0xFF, 1, 2 });

        var dto = new KioskOrderDto(Guid.NewGuid(), "Borne1", null,
            new List<KioskItemDto> { new("001.jpg", "10x15", 3) });

        var first = await _client.SubmitAsync(dto, new[] { photo });
        var retry = await _client.SubmitAsync(dto, new[] { photo }); // retry réseau simulé

        Assert.Equal(first.DisplayNumber, retry.DisplayNumber);
        Assert.Equal(first.DailyNumber, retry.DailyNumber);
        Assert.Equal(0.75m, first.Total); // 3 × 0,25 €

        // une seule commande créée sur disque
        var monthDir = Path.Combine(_root, "orders",
            DateTime.Now.Year.ToString("0000"), DateTime.Now.Month.ToString("00"));
        Assert.Single(Directory.GetDirectories(monthDir));
    }

    [Fact]
    public async Task UnknownProduct_IsRejected()
    {
        var photo = Path.Combine(_root, "p2.jpg");
        await File.WriteAllBytesAsync(photo, new byte[] { 1 });

        var dto = new KioskOrderDto(Guid.NewGuid(), "Borne1", null,
            new List<KioskItemDto> { new("001.jpg", "INEXISTANT", 1) });

        await Assert.ThrowsAsync<HttpRequestException>(() => _client.SubmitAsync(dto, new[] { photo }));
    }
}
