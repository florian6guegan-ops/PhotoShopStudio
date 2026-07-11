using System.Windows.Controls;
using Studio.Store;

namespace Studio.App.Views;

public partial class StatsView : UserControl
{
    public StatsView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            var orders = await Task.Run(() => App.Services.Store.ScanRecent(days: 30));

            DaysList.ItemsSource = OrderStats.ByDay(orders)
                .Select(d => new Row(
                    $"{d.Day:ddd dd/MM} — {d.Orders} commande{(d.Orders > 1 ? "s" : "")}, {d.Prints} tirage{(d.Prints > 1 ? "s" : "")}",
                    $"{d.Revenue:0.00} €"))
                .ToList();

            var catalog = App.Services.Catalog;
            ProductsList.ItemsSource = OrderStats.ByProduct(orders)
                .Select(p => new Row(
                    $"{catalog.Find(p.ProductCode)?.Name ?? p.ProductCode} — {p.Prints} tirage{(p.Prints > 1 ? "s" : "")}",
                    $"{p.Revenue:0.00} €"))
                .ToList();
        };
    }

    private sealed record Row(string Label, string Revenue);
}
