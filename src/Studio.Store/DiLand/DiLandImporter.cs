using Studio.Core.Domain;

namespace Studio.Store.DiLand;

/// <summary>Résultat de la reprise d'une commande de borne.</summary>
/// <param name="Source">La commande telle que DiLand l'a reçue.</param>
/// <param name="Created">La commande créée dans Studio, ou null si rien n'a pu être repris.</param>
/// <param name="Warnings">Ce qui n'a pas pu être repris, à montrer à l'opérateur.</param>
public sealed record DiLandImportOutcome(
    DiLandOrder Source,
    Order? Created,
    IReadOnlyList<string> Warnings)
{
    public bool Succeeded => Created is not null;

    public override string ToString() => Succeeded
        ? $"{Source} → commande {Created!.DailyNumber:000}"
        : $"{Source} → non repris ({string.Join(" ; ", Warnings)})";
}

/// <summary>
/// Reprend dans Studio les commandes que les bornes ont déposées dans DiLand.
///
/// DiLand garde les siennes : on ne fait que lire et copier. Ses photos ne sont ni
/// déplacées ni supprimées, et sa base n'est jamais ouverte (voir <see cref="DiLandRepository"/>).
///
/// Une commande n'est reprise qu'une fois. Le suivi est tenu à part dans un journal
/// (voir <see cref="KioskOrderJournal"/>), car le dossier d'une commande Studio porte son
/// numéro du jour : sans journal, une deuxième reprise créerait un doublon au lieu d'écraser.
///
/// Une commande reste affichée à l'opérateur TANT QUE le tirage n'est pas sorti. L'ouvrir
/// ou la reprendre la passe « en cours » ; seule l'impression la fait basculer dans
/// l'historique, qui se conserve un mois.
/// </summary>
public sealed class DiLandImporter
{
    /// <summary>Ce qui apparaît comme origine sur les commandes reprises.</summary>
    public const string SourceName = "borne";

    private readonly DiLandRepository _depot;
    private readonly OrderService _commandes;
    private readonly IReadOnlyList<Product> _catalogue;

    public DiLandImporter(
        DiLandRepository depot,
        OrderService commandes,
        IReadOnlyList<Product> catalogue,
        string registrePath)
    {
        _depot = depot;
        _commandes = commandes;
        _catalogue = catalogue;
        Journal = new KioskOrderJournal(registrePath);
    }

    /// <summary>Le suivi des commandes de bornes : ce qui reste à faire, et ce qui a été fait.</summary>
    public KioskOrderJournal Journal { get; }

    /// <summary>
    /// Commandes de bornes à traiter — celles que personne n'a prises, et celles qui sont
    /// en cours mais dont le tirage n'est pas sorti. De la plus ancienne à la plus récente :
    /// on sert dans l'ordre d'arrivée.
    ///
    /// Volontairement léger : l'écran d'accueil appelle cette méthode sur le fil de
    /// l'interface pour afficher le nombre de commandes en attente. Lire le détail de
    /// chaque commande ici bloquerait l'application au démarrage — le détail se lit
    /// écran par écran, avec <see cref="Summarize"/>.
    /// </summary>
    public IReadOnlyList<DiLandOrder> Pending(int limit = 50)
    {
        if (!_depot.RefreshSnapshot()) return [];

        Reconcile();

        return _depot.ReadKioskOrdersAfter(0, 4000)
            .Where(c => !Journal.IsClosed(c.Oid))
            .TakeLast(limit)
            .ToList();
    }

    /// <summary>Le contenu d'une commande et son total, en une seule lecture de la base.</summary>
    /// <param name="Lines">Un libellé par produit, avec le nombre de tirages.</param>
    /// <param name="Total">Ce que coûterait la commande à notre tarif.</param>
    /// <param name="Lines">Une ligne de texte par produit commandé.</param>
    /// <param name="Total">Prix de la commande, au tarif du catalogue Studio.</param>
    /// <param name="PhotoCount">
    /// Photos DISTINCTES de la commande. Une même photo commandée en 10×15 et en 13×18
    /// figure sur deux lignes mais ne compte qu'une fois — c'est ce que l'opérateur voit
    /// quand il ouvre la commande, et donc ce qu'il faut annoncer.
    /// </param>
    /// <param name="PrintCount">Nombre de tirages, exemplaires compris.</param>
    public sealed record KioskOrderSummary(
        IReadOnlyList<string> Lines, decimal Total, int PhotoCount, int PrintCount);

    /// <summary>
    /// Ce qu'il faut afficher pour une commande : son contenu et son prix.
    ///
    /// Les deux sortent d'une seule lecture des lignes — la liste en affiche cinquante,
    /// et interroger la base deux fois par commande se sentirait à l'écran.
    /// </summary>
    public KioskOrderSummary Summarize(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var libelles = new List<string>();
        var total = 0m;
        var tiragesTotal = 0;

        // même règle que la mise à disposition des fichiers : une photo présente sur deux
        // lignes n'est comptée qu'une fois
        var distinctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ligne in _depot.LinesOf(order))
        {
            foreach (var photo in ligne.Photos) distinctes.Add(photo.FileName);
            tiragesTotal += Math.Max(1, ligne.PrintCount);

            var produit = MatchProduct(ligne.ProductName);
            if (produit is null)
            {
                libelles.Add($"{ligne.ProductName} × {ligne.PrintCount} (non repris)");
                continue;
            }

            libelles.Add($"{ligne.ProductName} × {ligne.PrintCount}");

            var tirages = Math.Max(1, ligne.PrintCount);
            total += produit.UnitPriceFor(tirages) * tirages;
        }

        return new KioskOrderSummary(libelles, total, distinctes.Count, tiragesTotal);
    }

    /// <summary>L'état d'une commande de borne : à traiter, en cours, tirée, retirée.</summary>
    public KioskOrderStage StageOf(DiLandOrder order) =>
        Journal.Find(order.Oid)?.Stage ?? KioskOrderStage.Waiting;

    /// <summary>
    /// Les commandes closes du dernier mois, la plus récente d'abord. C'est l'historique :
    /// il vit dans le journal, pas dans DiLand, et survit donc aux purges de ce dernier.
    /// </summary>
    public IReadOnlyList<KioskOrderEntry> History()
    {
        Reconcile();
        return Journal.History();
    }

    /// <summary>
    /// Ferme les commandes dont le tirage est sorti depuis la dernière consultation.
    ///
    /// On ne se fie pas à un événement en mémoire : Studio peut être redémarré entre la
    /// reprise et l'impression, et l'impression peut être lancée depuis « Commandes du
    /// jour ». La vérité est sur le disque, dans la commande Studio elle-même — une
    /// commande de borne est tirée quand toutes les enveloppes de sa commande le sont.
    /// </summary>
    public void Reconcile()
    {
        var aSuivre = Journal.All()
            .Where(e => e.IsOpen && e.StudioOrderId is not null)
            .ToList();

        if (aSuivre.Count == 0) return;

        var recentes = _commandes.Recent((int)KioskOrderJournal.Retention.TotalDays)
            .ToDictionary(o => o.Id);

        foreach (var entree in aSuivre)
        {
            if (!recentes.TryGetValue(entree.StudioOrderId!.Value, out var commande)) continue;

            var tiree = commande.Envelopes.Count > 0 &&
                        commande.Envelopes.All(e => e.Status == EnvelopeStatus.Printed);

            if (tiree) Journal.MarkPrinted(entree.Oid);
        }
    }

    /// <summary>
    /// Ce que coûterait la commande à notre tarif. Les produits que Studio ne sait pas
    /// vendre ne sont pas comptés : le total affiché est celui de ce qu'on tirera.
    /// </summary>
    public decimal Quote(DiLandOrder order) => Summarize(order).Total;

    /// <summary>
    /// Recopie dans le journal ce qu'il faut pour afficher la commande plus tard.
    ///
    /// Appelé au moment où l'on prend la commande en charge, pas à chaque affichage :
    /// l'historique se constitue de ce qui a été traité, et cette lecture coûte une
    /// requête à la base de DiLand.
    /// </summary>
    private void Note(DiLandOrder order)
    {
        var resume = Summarize(order);

        Journal.Describe(
            order.Oid, order.Number, order.DailyNumber, order.Date,
            order.EndUserName ?? "",
            string.Join("   ·   ", resume.Lines),
            resume.Total);
    }

    /// <summary>
    /// Décrit le contenu d'une commande pour l'opérateur : un libellé par produit, avec le
    /// nombre de tirages, et la mention de ce que Studio ne sait pas vendre. C'est ce qu'il
    /// faut lire avant de décider de reprendre.
    /// </summary>
    public IReadOnlyList<string> Describe(DiLandOrder order) => Summarize(order).Lines;

    /// <summary>
    /// Reprend une commande : copie ses photos dans le dossier Studio et crée la commande
    /// avec ses produits, ses quantités et ses recadrages.
    ///
    /// Une commande déjà reprise n'est pas reprise deux fois.
    /// </summary>
    /// <param name="workDirectory">
    /// Dossier où recopier les photos avant de les reprendre.
    ///
    /// Il ne sert pas qu'à mettre les fichiers à l'abri : c'est aussi lui qui leur REND
    /// LEUR NOM. DiLand marque les fichiers des commandes qu'il a traitées d'un « _p »
    /// final, si bien que <c>photo.jpg</c> devient <c>photo.jpg_p</c> — un nom dont
    /// l'extension n'est plus celle d'une image. La recopie repart du nom de la base.
    ///
    /// Null pour lire les fichiers là où ils sont, ce qui reste correct pour une commande
    /// que DiLand n'a pas encore touchée.
    /// </param>
    public DiLandImportOutcome Import(DiLandOrder order, string? workDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(order);

        var avertissements = new List<string>();

        // une commande n'est reprise qu'une fois : une deuxième reprise créerait une
        // deuxième commande Studio, donc un doublon de tirage
        if (Journal.Find(order.Oid) is { } suivi && (suivi.StudioOrderId is not null || !suivi.IsOpen))
            return new DiLandImportOutcome(order, null, ["déjà reprise"]);

        var photos = new List<DraftItem>();

        // les fichiers sont recopiés une fois pour toutes, sous leur nom de base
        var prete = workDirectory is null ? null : Stage(order, workDirectory);

        foreach (var ligne in _depot.LinesOf(order))
        {
            var produit = MatchProduct(ligne.ProductName);
            if (produit is null)
            {
                avertissements.Add($"produit inconnu au catalogue : « {ligne.ProductName} »");
                continue;
            }

            foreach (var photo in ligne.Photos)
            {
                var chemin = prete is null
                    ? _depot.PhotoPath(order, photo)
                    : Path.Combine(prete.PhotosDirectory, photo.FileName);

                if (!File.Exists(chemin))
                {
                    avertissements.Add($"photo introuvable : {photo.DisplayName}");
                    continue;
                }

                photos.Add(new DraftItem(
                    SourcePath: chemin,
                    Product: produit,
                    Quantity: Math.Max(1, photo.Quantity),
                    Crop: CropOf(photo),
                    RotationQuarterTurns: QuarterTurns(photo.Angle),
                    FineRotationDegrees: 0, // les bornes ne redressent pas
                    FitOverride: null,
                    Adjustments: new ImageAdjustments()));
            }
        }

        if (photos.Count == 0)
        {
            avertissements.Add("aucune photo reprise");
            return new DiLandImportOutcome(order, null, avertissements);
        }

        var creee = _commandes.CreateOrder(
            SourceName,
            photos,
            customerName: string.IsNullOrWhiteSpace(order.EndUserName)
                ? $"Borne {order.DailyNumber}"
                : order.EndUserName);

        // reprise ≠ tirée : la commande reste affichée jusqu'à ce que le tirage sorte
        Note(order);
        Journal.MarkInProgress(order.Oid, creee.Id);

        return new DiLandImportOutcome(order, creee, avertissements);
    }

    /// <summary>Ce qu'il faut pour ouvrir une commande de borne dans l'écran des photos.</summary>
    /// <param name="PhotosDirectory">Dossier où les photos ont été recopiées.</param>
    /// <param name="ProductCode">Produit majoritaire de la commande, à présélectionner.</param>
    /// <param name="PhotoCount">Nombre de photos recopiées.</param>
    public sealed record StagedOrder(string PhotosDirectory, string? ProductCode, int PhotoCount);

    /// <summary>
    /// Recopie les photos d'une commande de borne dans un dossier de travail, pour qu'on
    /// puisse les recadrer et les corriger avant de tirer.
    ///
    /// On recopie plutôt que de travailler sur place : le dossier de DiLand contient aussi
    /// ses propres dérivés, qu'il ne faut pas montrer, et surtout ses fichiers doivent
    /// rester intacts puisqu'il peut encore tirer la commande de son côté.
    ///
    /// Une photo commandée en plusieurs exemplaires n'est recopiée qu'une fois : la
    /// quantité se règle ensuite dans l'écran des photos.
    /// </summary>
    /// <param name="folderName">
    /// Nom du sous-dossier à créer ; par défaut celui de DiLand. L'export vers les
    /// téléchargements s'en sert pour donner au dossier le nom de la commande, qui parle
    /// à l'opérateur là où « 000123.COM » ne dit rien.
    /// </param>
    public StagedOrder Stage(DiLandOrder order, string workDirectory, string? folderName = null)
    {
        ArgumentNullException.ThrowIfNull(order);

        var destination = Path.Combine(workDirectory, folderName ?? order.DirectoryName);
        Directory.CreateDirectory(destination);

        var lignes = _depot.LinesOf(order);

        // une même photo peut figurer sur deux lignes — commandée en 10x15 et en 13x18 par
        // exemple. On ne la met à disposition qu'une fois, et on la compte une fois
        var deposees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var photo in lignes.SelectMany(l => l.Photos))
        {
            if (!File.Exists(_depot.PhotoPath(order, photo))) continue;

            // la recopie remet la photo en clair : DiLand brouille le début des fichiers
            // des commandes qu'il a traitées
            _depot.CopyPhotoTo(order, photo, Path.Combine(destination, photo.FileName));
            deposees.Add(photo.FileName);
        }

        // le produit majoritaire : sur une commande de soixante 10x15 et d'un 13x18,
        // présélectionner le 10x15 évite soixante corrections à la main
        var majoritaire = lignes
            .Where(l => MatchProduct(l.ProductName) is not null)
            .OrderByDescending(l => l.PrintCount)
            .FirstOrDefault();

        return new StagedOrder(
            destination,
            majoritaire is null ? null : MatchProduct(majoritaire.ProductName)!.Code,
            deposees.Count);
    }

    /// <summary>
    /// Marque une commande comme prise en charge sans rien créer : elle a été ouverte pour
    /// être retouchée, la commande Studio naîtra à l'impression.
    ///
    /// Elle RESTE dans la liste de l'opérateur : tant que le tirage n'est pas sorti, la
    /// commande est à faire. C'est ce qui évite qu'une commande ouverte puis oubliée
    /// disparaisse sans que personne ne s'en aperçoive.
    /// </summary>
    public void MarkInProgress(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        Note(order);
        Journal.MarkInProgress(order.Oid);
    }

    /// <summary>
    /// Le tirage est sorti : la commande quitte la liste pour l'historique.
    /// Appelé après une impression réussie, pas avant.
    /// </summary>
    public void MarkPrinted(long oid, Guid? studioOrderId = null) =>
        Journal.MarkPrinted(oid, studioOrderId);

    /// <summary>Retire une commande sans tirage de notre côté (DiLand l'a faite, ou annulation).</summary>
    public void Dismiss(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        Note(order);
        Journal.Dismiss(order.Oid);
    }

    /// <summary>
    /// Retrouve le produit Studio correspondant au produit DiLand.
    ///
    /// Le catalogue a été repris de DiLand, donc les noms coïncident (« 10x15 »,
    /// « Bord blanc 10x15 », « 21x29,7 »). On compare malgré tout à la casse, aux espaces
    /// et à la virgule décimale près, pour qu'un écart de saisie ne fasse pas perdre une
    /// commande. À nom égal, le minilab l'emporte : c'est lui qui tire les commandes de
    /// bornes, et le catalogue contient aussi un « 10x15 » sur la DNP.
    /// </summary>
    public Product? MatchProduct(string dilandName)
    {
        if (string.IsNullOrWhiteSpace(dilandName)) return null;

        var cible = Normaliser(dilandName);

        var candidats = _catalogue
            .Where(p => p.Enabled && Normaliser(p.Name) == cible)
            .ToList();

        return candidats.FirstOrDefault(p => p.Output == ProductOutput.FujiMinilab)
            ?? candidats.FirstOrDefault();
    }

    private static string Normaliser(string nom) =>
        nom.Replace(" ", "").Replace(",", ".").Trim().ToLowerInvariant();

    /// <summary>
    /// Le recadrage fait à la borne, ou l'image entière. DiLand l'exprime déjà en
    /// fractions de l'image, comme nous ; un rectangle incohérent est ignoré plutôt que
    /// de produire un tirage faux.
    /// </summary>
    private static CropSpec CropOf(DiLandOrderPhoto photo)
    {
        if (!photo.ApplyCrop) return CropSpec.Full;

        var crop = new CropSpec(photo.CropX, photo.CropY, photo.CropWidth, photo.CropHeight);
        return crop.IsValid ? crop : CropSpec.Full;
    }

    /// <summary>DiLand stocke des degrés, nous des quarts de tour horaires.</summary>
    internal static int QuarterTurns(double angle)
    {
        var quarts = (int)Math.Round(angle / 90.0) % 4;
        return quarts < 0 ? quarts + 4 : quarts;
    }

}
