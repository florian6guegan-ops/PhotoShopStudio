using System.Text.Json;
using System.Text.Json.Serialization;
using Studio.Core.Domain;

namespace Studio.Store;

/// <summary>
/// Persistance des commandes : un dossier par commande, source de vérité sur disque.
///   orders/2026/07/20260711-042-a1b2c3d4/
///     order.json   (écriture atomique)
///     order.log    (journal d'événements, append-only, une ligne JSON par événement)
///     photos/      (originaux clients, toujours copiés)
///     renders/     (bitmaps finaux d'impression)
///     spool/       (état d'impression par enveloppe)
/// Pas de base de données : récupérable à la main dans l'Explorateur.
/// </summary>
public sealed class OrderFolderStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _ordersRoot;

    public OrderFolderStore(string ordersRoot)
    {
        _ordersRoot = ordersRoot;
        Directory.CreateDirectory(ordersRoot);
    }

    public string GetOrderFolder(Order order) =>
        Path.Combine(
            _ordersRoot,
            order.CreatedAt.Year.ToString("0000"),
            order.CreatedAt.Month.ToString("00"),
            $"{order.CreatedAt:yyyyMMdd}-{order.DailyNumber:000}-{order.Id.ToString("N")[..8]}");

    public string GetPhotosFolder(Order order) => Path.Combine(GetOrderFolder(order), "photos");
    public string GetRendersFolder(Order order) => Path.Combine(GetOrderFolder(order), "renders");
    public string GetSpoolFolder(Order order) => Path.Combine(GetOrderFolder(order), "spool");

    /// <summary>Crée le dossier de la commande et la persiste une première fois.</summary>
    public string Create(Order order)
    {
        var folder = GetOrderFolder(order);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "photos"));
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        Directory.CreateDirectory(Path.Combine(folder, "spool"));
        Save(order);
        AppendEvent(order, "created", $"source={order.Source}");
        return folder;
    }

    public void Save(Order order)
    {
        var folder = GetOrderFolder(order);
        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(
            Path.Combine(folder, "order.json"),
            JsonSerializer.Serialize(order, JsonOptions));
    }

    /// <summary>Journal d'audit append-only ; sert aussi de trace de récupération après incident.</summary>
    public void AppendEvent(Order order, string kind, string? detail = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now,
            kind,
            detail,
            status = order.Status.ToString(),
        });
        File.AppendAllText(Path.Combine(GetOrderFolder(order), "order.log"), line + Environment.NewLine);
    }

    public Order? Load(string orderFolder)
    {
        var json = AtomicFile.ReadAllTextOrNull(Path.Combine(orderFolder, "order.json"));
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<Order>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null; // dossier corrompu : ignoré du scan, récupérable à la main
        }
    }

    /// <summary>Scan de démarrage : recharge les commandes des N derniers jours.</summary>
    public List<Order> ScanRecent(int days = 30)
    {
        var result = new List<Order>();
        var cutoff = DateTime.Now.Date.AddDays(-days);

        for (var month = new DateTime(cutoff.Year, cutoff.Month, 1);
             month <= DateTime.Now.Date;
             month = month.AddMonths(1))
        {
            var monthFolder = Path.Combine(_ordersRoot, month.Year.ToString("0000"), month.Month.ToString("00"));
            if (!Directory.Exists(monthFolder)) continue;

            foreach (var orderFolder in Directory.EnumerateDirectories(monthFolder))
            {
                var order = Load(orderFolder);
                if (order is not null && order.CreatedAt.Date >= cutoff)
                    result.Add(order);
            }
        }

        return result.OrderByDescending(o => o.CreatedAt).ToList();
    }
}
