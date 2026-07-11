using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;

namespace Studio.Tests;

public class EscPosTicketTests
{
    [Fact]
    public void PriceLine_AlignsPriceToTheRight()
    {
        var line = EscPosTicket.PriceLine("2 x Tirage 10x15", 0.50m);
        Assert.Equal(42, line.Length);
        Assert.EndsWith("0,50 EUR", line);
        Assert.StartsWith("2 x Tirage 10x15", line);
    }

    [Fact]
    public void Build_ContainsNumberTotalAndCut()
    {
        var catalog = new ProductCatalog(new[]
        {
            new Product { Code = "10x15", Name = "Tirage 10×15", Price = 0.25m },
        });
        var order = new Order
        {
            DailyNumber = 42,
            CreatedAt = new DateTimeOffset(2026, 7, 11, 15, 0, 0, TimeSpan.FromHours(2)),
            Envelopes =
            {
                new Envelope
                {
                    Number = 1, PrinterChannel = "DS620",
                    Lines = { new OrderLine { ProductCode = "10x15", UnitPrice = 0.25m,
                        Items = { new OrderItem { FileName = "001.jpg", Quantity = 4 } } } },
                },
            },
        };

        var bytes = EscPosTicket.Build(order, catalog, new TicketConfig());
        var text = System.Text.Encoding.GetEncoding(858).GetString(bytes);

        Assert.StartsWith("\x1B@", text);                       // init ESC/POS
        Assert.Contains("N° 11-042", text);                     // numéro client
        Assert.Contains("TOTAL", text);
        Assert.Contains("1,00 EUR", text);                      // 4 × 0,25
        Assert.Contains("PHOTOCONCEPT", text);
        Assert.EndsWith("\x1DV\x42\0", text);                   // coupe partielle en fin
    }
}
