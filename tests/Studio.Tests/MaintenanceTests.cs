using Studio.Core.Domain;
using Studio.Store;

namespace Studio.Tests;

public class MaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioMaint-" + Guid.NewGuid().ToString("N"));

    public MaintenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Order MakeOrder(DateTimeOffset at, string product, int qty, decimal price, OrderStatus status = OrderStatus.Ready) =>
        new()
        {
            CreatedAt = at,
            DailyNumber = 1,
            Status = status,
            Envelopes =
            {
                new Envelope
                {
                    Number = 1,
                    Lines = { new OrderLine { ProductCode = product, UnitPrice = price,
                        Items = { new OrderItem { FileName = "a.jpg", Quantity = qty } } } },
                },
            },
        };

    [Fact]
    public void Stats_ByDayAndProduct_IgnoreCancelled()
    {
        var today = DateTimeOffset.Now;
        var orders = new[]
        {
            MakeOrder(today, "10x15", 4, 0.25m),
            MakeOrder(today, "20x30", 1, 4.00m),
            MakeOrder(today.AddDays(-1), "10x15", 2, 0.25m),
            MakeOrder(today, "10x15", 99, 0.25m, OrderStatus.Cancelled),
        };

        var byDay = OrderStats.ByDay(orders);
        Assert.Equal(2, byDay.Count);
        Assert.Equal(2, byDay[0].Orders);          // aujourd'hui : 2 commandes comptées
        Assert.Equal(5, byDay[0].Prints);          // 4 + 1
        Assert.Equal(5.00m, byDay[0].Revenue);     // 1,00 + 4,00

        var byProduct = OrderStats.ByProduct(orders);
        Assert.Equal("20x30", byProduct[0].ProductCode); // CA le plus fort d'abord
        Assert.Equal(6, byProduct.Single(p => p.ProductCode == "10x15").Prints);
    }

    [Fact]
    public void Archiver_MovesOnlyOldOrders()
    {
        var orders = Path.Combine(_root, "orders");
        var archive = Path.Combine(_root, "archive");
        var oldDay = DateTime.Now.AddDays(-120);
        var recentDay = DateTime.Now.AddDays(-5);

        var oldDir = Path.Combine(orders, oldDay.Year.ToString("0000"), oldDay.Month.ToString("00"),
            $"{oldDay:yyyyMMdd}-001-abcd1234");
        var recentDir = Path.Combine(orders, recentDay.Year.ToString("0000"), recentDay.Month.ToString("00"),
            $"{recentDay:yyyyMMdd}-002-efgh5678");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(recentDir);
        File.WriteAllText(Path.Combine(oldDir, "order.json"), "{}");

        var moved = Archiver.ArchiveOldOrders(orders, archive, olderThanDays: 90);

        Assert.Equal(1, moved);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(recentDir));
        Assert.True(File.Exists(Path.Combine(archive, oldDay.Year.ToString("0000"),
            oldDay.Month.ToString("00"), $"{oldDay:yyyyMMdd}-001-abcd1234", "order.json")));

        // ré-exécution : plus rien à faire (idempotent)
        Assert.Equal(0, Archiver.ArchiveOldOrders(orders, archive, olderThanDays: 90));
    }

    [Fact]
    public void Backup_IsDue_Logic()
    {
        var config = new BackupConfig { Enabled = true, Destination = @"X:\sauvegarde", EveryHours = 24 };
        var now = DateTimeOffset.Now;

        Assert.True(BackupRunner.IsDue(config, null, now));                       // jamais lancée
        Assert.False(BackupRunner.IsDue(config, now.AddHours(-2), now));          // trop tôt
        Assert.True(BackupRunner.IsDue(config, now.AddHours(-25), now));          // échue
        Assert.False(BackupRunner.IsDue(new BackupConfig(), null, now));          // désactivée par défaut
        Assert.False(BackupRunner.IsDue(
            new BackupConfig { Enabled = true, Destination = "" }, null, now));   // destination vide
    }
}
