using Studio.Core.Domain;

namespace Studio.Store;

/// <summary>Photo choisie par le client avec son produit, avant création de la commande.</summary>
public sealed record DraftItem(
    string SourcePath,
    Product Product,
    int Quantity,
    CropSpec Crop,
    int RotationQuarterTurns,
    FitMode? FitOverride,
    ImageAdjustments Adjustments);

/// <summary>
/// Transforme une sélection en commande persistée : numéro du jour, enveloppes
/// groupées par canal d'impression, et copie systématique des originaux dans le
/// dossier de la commande (le support client repart avec le client).
/// </summary>
public sealed class OrderService
{
    private readonly OrderFolderStore _store;
    private readonly DailyCounter _counter;

    public OrderService(OrderFolderStore store, DailyCounter counter)
    {
        _store = store;
        _counter = counter;
    }

    public Order CreateOrder(string source, IReadOnlyList<DraftItem> items, string? customerName = null)
    {
        if (items.Count == 0) throw new ArgumentException("Aucune photo sélectionnée", nameof(items));

        var order = new Order
        {
            Source = source,
            DailyNumber = _counter.Next(),
            Status = OrderStatus.Submitted,
            CustomerName = customerName,
        };

        // copie des originaux : noms séquentiels stables, un fichier source copié une seule fois
        var fileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in items)
        {
            if (!fileNames.ContainsKey(item.SourcePath))
                fileNames[item.SourcePath] = $"{++index:000}{Path.GetExtension(item.SourcePath).ToLowerInvariant()}";
        }

        // une enveloppe par canal d'impression, une ligne par produit
        var envelopeNumber = 0;
        foreach (var channelGroup in items.GroupBy(i => i.Product.Channel))
        {
            var envelope = new Envelope
            {
                Number = ++envelopeNumber,
                PrinterChannel = channelGroup.Key,
            };

            foreach (var productGroup in channelGroup.GroupBy(i => i.Product.Code))
            {
                var product = productGroup.First().Product;
                var line = new OrderLine
                {
                    ProductCode = product.Code,
                    UnitPrice = product.Price,
                };
                foreach (var item in productGroup)
                {
                    line.Items.Add(new OrderItem
                    {
                        FileName = fileNames[item.SourcePath],
                        OriginalName = Path.GetFileName(item.SourcePath),
                        Quantity = item.Quantity,
                        Crop = item.Crop,
                        RotationQuarterTurns = item.RotationQuarterTurns,
                        FitOverride = item.FitOverride,
                        Adjustments = item.Adjustments,
                    });
                }
                envelope.Lines.Add(line);
            }
            order.Envelopes.Add(envelope);
        }

        var folder = _store.Create(order);
        var photosDir = Path.Combine(folder, "photos");
        foreach (var (sourcePath, fileName) in fileNames)
            File.Copy(sourcePath, Path.Combine(photosDir, fileName), overwrite: false);

        _store.AppendEvent(order, "photos-copied", $"{fileNames.Count} fichiers");
        return order;
    }
}
