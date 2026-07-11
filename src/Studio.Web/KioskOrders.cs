using System.Collections.Concurrent;
using System.Text.Json;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Store;

namespace Studio.Web;

/// <summary>Une photo de commande borne : nom du fichier envoyé + produit + quantité.</summary>
public sealed record KioskItemDto(string File, string ProductCode, int Quantity);

/// <summary>Commande envoyée par une borne. Id = clé d'idempotence (retries réseau sans doublon).</summary>
public sealed record KioskOrderDto(Guid Id, string Source, string? CustomerName, List<KioskItemDto> Items);

/// <summary>Accusé : le numéro à annoncer au client.</summary>
public sealed record KioskAck(int DailyNumber, string DisplayNumber, decimal Total);

/// <summary>
/// Réception des commandes borne côté opérateur, idempotente : le même GUID
/// renvoie le même numéro, qu'il s'agisse d'un retry réseau ou d'un double envoi.
/// </summary>
public sealed class KioskOrderReceiver
{
    private readonly ProductCatalog _catalog;
    private readonly OrderService _orders;
    private readonly ConcurrentDictionary<Guid, KioskAck> _acks = new();
    private readonly object _submitLock = new();

    /// <param name="recentOrders">Commandes rechargées au démarrage (idempotence après redémarrage).</param>
    public KioskOrderReceiver(ProductCatalog catalog, OrderService orders, IEnumerable<Order> recentOrders)
    {
        _catalog = catalog;
        _orders = orders;
        foreach (var order in recentOrders)
            _acks[order.Id] = new KioskAck(order.DailyNumber, order.DisplayNumber, order.Total);
    }

    /// <summary>Créée après réception d'une commande (ticket de caisse, notification…).</summary>
    public event Action<Order>? OrderReceived;

    /// <param name="photosFolder">Dossier où l'API a enregistré les fichiers de la commande.</param>
    public KioskAck Submit(KioskOrderDto dto, string photosFolder)
    {
        if (_acks.TryGetValue(dto.Id, out var known)) return known;

        lock (_submitLock)
        {
            if (_acks.TryGetValue(dto.Id, out known)) return known;

            var items = dto.Items.Select(i => new DraftItem(
                Path.Combine(photosFolder, Path.GetFileName(i.File)),
                _catalog.Require(i.ProductCode),
                Math.Clamp(i.Quantity, 1, 99),
                CropSpec.Full, 0, null, new ImageAdjustments())).ToList();

            var order = _orders.CreateOrder(dto.Source, items, dto.CustomerName, dto.Id);
            var ack = new KioskAck(order.DailyNumber, order.DisplayNumber, order.Total);
            _acks[dto.Id] = ack;
            OrderReceived?.Invoke(order);
            return ack;
        }
    }

    public static KioskOrderDto ParseDto(string json) =>
        JsonSerializer.Deserialize<KioskOrderDto>(json, JsonOpts)
        ?? throw new InvalidDataException("Commande borne illisible");

    public static string SerializeDto(KioskOrderDto dto) => JsonSerializer.Serialize(dto, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
