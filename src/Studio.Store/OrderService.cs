using Studio.Core.Domain;

namespace Studio.Store;

/// <summary>
/// Format « personnalisé » : la taille demandée et le nombre de planches que le logiciel a
/// retenu pour la contenir.
///
/// <see cref="SheetCount"/> vient de l'appelant et n'est PAS recalculé à la création de la
/// commande : c'est lui qui fixe le prix, et un prix annoncé au client ne doit pas bouger
/// parce que le catalogue a changé entre l'annonce et l'encaissement.
/// </summary>
public sealed record CustomSheetSpec(double CellWidthMm, double CellHeightMm, int SheetCount);

/// <summary>
/// Taille d'une case de planche identité, en millimètres : celle du document visé.
///
/// Un type nommé plutôt qu'un couple de <c>double?</c> — deux paramètres facultatifs
/// voisins et de même type s'inversent sans que le compilateur bronche, et une planche
/// espagnole sortirait en 32 × 26 mm.
/// </summary>
public sealed record SheetCellSize(double WidthMm, double HeightMm);

/// <summary>Photo choisie par le client avec son produit, avant création de la commande.</summary>
public sealed record DraftItem(
    string SourcePath,
    Product Product,
    int Quantity,
    CropSpec Crop,
    int RotationQuarterTurns,
    double FineRotationDegrees,
    FitMode? FitOverride,
    ImageAdjustments Adjustments,
    /// <summary>Planches identité : nombre de photos sur la planche, null = celui du produit.</summary>
    int? SheetCopiesOverride = null,
    /// <summary>
    /// Finition choisie, null = DEVMODE par défaut du produit.
    ///
    /// Elle vient soit du catalogue (voir <c>Product.Finishes</c>, cas de la DNP, où elle
    /// désigne un DEVMODE), soit du CLIENT quand la commande arrive d'une borne — et là
    /// elle désigne un ROULEAU, donc la machine DE100 qui recevra l'enveloppe. Voir
    /// <see cref="FinitionPapier"/>.
    /// </summary>
    string? Finish = null,
    /// <summary>Contour noir de découpe, quand le tirage sort avec des marges blanches.</summary>
    bool CutBorder = false,
    /// <summary>
    /// Taille personnalisée : <paramref name="Product"/> désigne alors le PAPIER retenu, et
    /// la photo n'occupe qu'une case de la planche. Null = tirage ordinaire.
    /// </summary>
    CustomSheetSpec? CustomSheet = null,
    /// <summary>
    /// Planches identité : taille d'une case en millimètres, celle du DOCUMENT visé.
    /// Null = la cellule du produit s'applique (voir <see cref="OrderItem.SheetCellWidthMm"/>).
    ///
    /// Posée en DERNIER paramètre à dessein : les appelants passent les précédents par
    /// position, et intercaler un paramètre les déplacerait tous.
    /// </summary>
    SheetCellSize? SheetCell = null,
    /// <summary>
    /// Prix unitaire imposé, qui l'emporte sur celui du catalogue. Null = le catalogue
    /// décide, ce qui est le cas de tous les tirages.
    ///
    /// Il existe pour les planches d'IDENTITÉ, dont le prix dépend du DOCUMENT et non du
    /// papier : 10 € pour un document français, 15 € pour un étranger, sur le même produit.
    /// Voir <see cref="TarifsIdentite"/>.
    /// </summary>
    decimal? UnitPriceOverride = null,
    /// <summary>
    /// Montage : code de la FEUILLE sur laquelle composer les agrandissements, ou null pour
    /// un fichier par tirage.
    ///
    /// ⚠ Sans effet sur le prix — voir <see cref="OrderLine.MontageSheetCode"/>. C'est toute
    /// la différence avec <paramref name="CustomSheet"/>, qui, lui, facture le papier.
    /// </summary>
    string? MontageSheetCode = null);

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

    /// <summary>Commandes des N derniers jours, la plus récente d'abord.</summary>
    public IReadOnlyList<Order> Recent(int days = 30) => _store.ScanRecent(days);

    /// <param name="id">Clé d'idempotence fournie par le créateur (borne, téléphone) ; null = générée.</param>
    public Order CreateOrder(string source, IReadOnlyList<DraftItem> items, string? customerName = null, Guid? id = null)
    {
        if (items.Count == 0) throw new ArgumentException("Aucune photo sélectionnée", nameof(items));

        var order = new Order
        {
            Id = id ?? Guid.NewGuid(),
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

        // Une enveloppe par canal d'impression ET PAR FINITION, une ligne par produit.
        //
        // La finition entre dans le découpage parce qu'une enveloppe part d'un bloc sur UNE
        // machine, et que sur le DE100 la finition, c'est le rouleau : deux machines, deux
        // rouleaux, et l'on ne peut pas tirer du brillant et du lustré sur la même. La
        // commande 10-013 du 10/08/2026 est exactement ce cas — un client a pris les deux
        // dans le même panier — et elle serait partie entière sur une seule machine.
        //
        // Quand rien ne déclare de finition — tout le comptoir aujourd'hui — la clé est
        // partout nulle et le découpage est celui d'avant, au groupe près.
        var envelopeNumber = 0;
        foreach (var channelGroup in items.GroupBy(i => (i.Product.Channel, i.Finish)))
        {
            var envelope = new Envelope
            {
                Number = ++envelopeNumber,
                PrinterChannel = channelGroup.Key.Channel,
            };

            foreach (var productGroup in channelGroup.GroupBy(i => i.Product.Code))
            {
                var product = productGroup.First().Product;
                // le tarif dégressif se calcule sur le total du produit dans la commande,
                // pas photo par photo : 30 tirages en 3 images restent 30 tirages
                var totalQuantity = productGroup.Sum(i => i.Quantity);

                // planche personnalisée : c'est le PAPIER qui est facturé, donc les paliers
                // se comptent en planches et non en photos casées dessus
                var planche = productGroup.First().CustomSheet;
                var factureSur = planche is null ? totalQuantity : planche.SheetCount;

                var line = new OrderLine
                {
                    ProductCode = product.Code,
                    // Le prix imposé l'emporte : c'est celui des planches d'identité, qui
                    // dépend du document. Sans lui, tout vient du catalogue, comme avant.
                    UnitPrice = productGroup.Select(i => i.UnitPriceOverride).FirstOrDefault(p => p is not null)
                                ?? product.UnitPriceFor(Math.Max(factureSur, 1)),
                    CustomCellWidthMm = planche?.CellWidthMm,
                    CustomCellHeightMm = planche?.CellHeightMm,
                    SheetCount = planche?.SheetCount ?? 0,
                    // le montage suit le PRODUIT : toutes les photos d'une même ligne
                    // partent sur la même feuille, sans quoi la grille serait à trous
                    MontageSheetCode = productGroup
                        .Select(i => i.MontageSheetCode)
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
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
                        FineRotationDegrees = item.FineRotationDegrees,
                        FitOverride = item.FitOverride,
                        CutBorder = item.CutBorder,
                        SheetCopiesOverride = item.SheetCopiesOverride,
                        SheetCellWidthMm = item.SheetCell?.WidthMm,
                        SheetCellHeightMm = item.SheetCell?.HeightMm,
                        Finish = item.Finish,
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
