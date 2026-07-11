using Studio.Core.Domain;

namespace Studio.Store;

public sealed record DayStat(DateOnly Day, int Orders, int Prints, decimal Revenue);
public sealed record ProductStat(string ProductCode, int Prints, decimal Revenue);

/// <summary>Statistiques simples calculées depuis les commandes (fonctions pures).</summary>
public static class OrderStats
{
    /// <summary>Par jour, du plus récent au plus ancien. Les commandes annulées sont ignorées.</summary>
    public static List<DayStat> ByDay(IEnumerable<Order> orders) =>
        Countable(orders)
            .GroupBy(o => DateOnly.FromDateTime(o.CreatedAt.LocalDateTime))
            .Select(g => new DayStat(
                g.Key,
                g.Count(),
                g.Sum(o => o.Envelopes.SelectMany(e => e.Lines).Sum(l => l.TotalPrints)),
                g.Sum(o => o.Total)))
            .OrderByDescending(s => s.Day)
            .ToList();

    /// <summary>Par produit, trié par chiffre d'affaires décroissant.</summary>
    public static List<ProductStat> ByProduct(IEnumerable<Order> orders) =>
        Countable(orders)
            .SelectMany(o => o.Envelopes.SelectMany(e => e.Lines))
            .GroupBy(l => l.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProductStat(g.Key, g.Sum(l => l.TotalPrints), g.Sum(l => l.Total)))
            .OrderByDescending(s => s.Revenue)
            .ToList();

    private static IEnumerable<Order> Countable(IEnumerable<Order> orders) =>
        orders.Where(o => o.Status is not (OrderStatus.Cancelled or OrderStatus.Draft));
}
