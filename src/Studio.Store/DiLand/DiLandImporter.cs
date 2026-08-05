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

    /// <param name="attente">
    /// Les commandes mises en attente, pour que celles issues d'une borne meurent avec
    /// elle. Facultatif : les essais qui ne s'en servent pas n'ont pas à en fabriquer une.
    /// </param>
    public DiLandImporter(
        DiLandRepository depot,
        OrderService commandes,
        IReadOnlyList<Product> catalogue,
        string registrePath,
        AttenteStore? attente = null)
    {
        _depot = depot;
        _commandes = commandes;
        _catalogue = catalogue;
        Journal = new KioskOrderJournal(registrePath);

        // à côté du journal, dans les données de Studio : les deux vivent et meurent
        // ensemble (voir KioskOrderJournal.Purge)
        ArchiveRoot = Path.Combine(Path.GetDirectoryName(registrePath) ?? ".", "archive");

        // c'est le journal qui sait quand une commande est close ou périmée : c'est donc
        // lui qui efface ce qui attend en son nom, et non chacun des endroits qui closent
        // une commande
        Journal.Attente = attente;
    }

    /// <summary>Le suivi des commandes de bornes : ce qui reste à faire, et ce qui a été fait.</summary>
    public KioskOrderJournal Journal { get; }

    /// <summary>
    /// Où Studio garde SA copie des photos des commandes de bornes.
    ///
    /// <b>L'historique ne lit plus rien chez DiLand.</b> Ses dossiers sont purgés quand il
    /// le décide, sans prévenir : une commande close pouvait survivre à ses propres
    /// photos, et l'opérateur qui redemandait les fichiers le lendemain tombait sur du
    /// vide. La copie se fait une fois, à la prise en charge, et c'est elle qu'on sert
    /// ensuite. Elle disparaît avec l'entrée du journal, au bout d'un mois.
    /// </summary>
    public string ArchiveRoot { get; }

    /// <summary>
    /// Range les photos d'une commande de borne chez nous, et note où.
    ///
    /// Le dossier porte l'identifiant DiLand et non le numéro de commande : le numéro
    /// repart de zéro chaque année, l'identifiant jamais.
    /// </summary>
    public StagedOrder Archiver(DiLandOrder order, bool refaire = false)
    {
        ArgumentNullException.ThrowIfNull(order);

        var prete = Stage(order, ArchiveRoot, folderName: order.Oid.ToString(), ecraser: refaire);
        Journal.SetArchive(order.Oid, prete.PhotosDirectory);
        return prete;
    }

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
        var base_ = _depot.RefreshSnapshot()
            ? _depot.ReadKioskOrdersAfter(0, 4000)
            : [];

        // Le disque est lu DANS TOUS LES CAS, base disponible ou non : c'est la seule
        // source qui voie les commandes qu'une borne a déposées pendant que DiLand était
        // fermé ou tombé — et il tombe presque tous les jours. Elles n'existaient nulle
        // part pour nous jusqu'ici.
        LireLeDisque();

        Reconcile();

        // Une commande vue des deux côtés ne paraît qu'une fois, et c'est la version de la
        // BASE qui l'emporte : elle porte le vrai Oid, celui auquel le journal et les
        // commandes Studio déjà créées se réfèrent. Le dédoublonnage se fait sur le
        // dossier, seul repère commun aux deux lectures.
        var dossiersConnus = base_
            .Select(c => c.DirectoryName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plancher = DateTime.Now - FenetreDuDisque;

        return base_
            .Concat(_surLeDisque.Values
                .Where(c => !dossiersConnus.Contains(c.DirectoryName))
                .Where(c => c.Date >= plancher))
            .Where(c => !Journal.IsClosed(c.Oid))
            .OrderBy(c => c.Date)
            .TakeLast(limit)
            .ToList();
    }

    /// <summary>
    /// Au-delà de quel âge une commande trouvée SUR LE DISQUE n'est plus proposée.
    ///
    /// Le dossier <c>Orders</c> de DiLand garde des mois d'historique. Y verser tout ce
    /// qui n'est pas en base noierait la liste du jour sous des commandes vieilles de
    /// plusieurs semaines, déjà servies d'une façon ou d'une autre — et une liste qu'on ne
    /// croit plus ne se lit plus.
    ///
    /// La fenêtre est celle de l'historique, pour qu'il n'y ait qu'un seul nombre à
    /// retenir. Elle couvre largement le cas qui motive cette lecture : DiLand tombe, on
    /// s'en aperçoit dans l'heure.
    ///
    /// <b>Deux commandes plus anciennes ont été trouvées le 03/08/2026</b> (#12360 du
    /// 18/06 et #6830 du 25/06), absentes de la base — pas même supprimées. Elles ne
    /// remontent donc pas ici ; <c>Studio.DiLandProbe xml</c> les montre.
    /// </summary>
    public static readonly TimeSpan FenetreDuDisque = KioskOrderJournal.Retention;

    /// <summary>
    /// Les commandes lues sur le disque, avec leur contenu, indexées par leur clé de
    /// journal.
    ///
    /// Gardées en mémoire parce que leurs LIGNES ne sont nulle part ailleurs : la base ne
    /// les connaît pas, et <see cref="LinesOf"/> doit pouvoir les rendre.
    /// </summary>
    private readonly Dictionary<long, DiLandOrder> _surLeDisque = [];
    private readonly Dictionary<long, IReadOnlyList<DiLandOrderLine>> _lignesDuDisque = [];

    private void LireLeDisque()
    {
        foreach (var contenu in _depot.ReadKioskOrdersFromDisk(4000))
        {
            _surLeDisque[contenu.Order.Oid] = contenu.Order;
            _lignesDuDisque[contenu.Order.Oid] = contenu.Lines;
        }
    }

    /// <summary>
    /// Le contenu d'une commande, qu'elle vienne de la base ou du disque.
    ///
    /// Le disque est interrogé en premier pour les commandes qu'on en a tirées : la base
    /// ne les connaît pas, et lui poser la question rendrait une commande vide.
    /// </summary>
    private IReadOnlyList<DiLandOrderLine> LignesDe(DiLandOrder order) =>
        _lignesDuDisque.TryGetValue(order.Oid, out var duDisque) ? duDisque : _depot.LinesOf(order);

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

        foreach (var ligne in LignesDe(order))
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
            resume.Total,
            order.DirectoryName);
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
    /// <remarks>
    /// Les photos sont recopiées dans NOTRE archive (<see cref="ArchiveRoot"/>), et c'est
    /// elle que la commande Studio référencera. Deux raisons, et non une :
    ///
    /// - la copie REND LEUR NOM aux fichiers. DiLand marque ceux des commandes qu'il a
    ///   traitées d'un « _p » final — <c>photo.jpg</c> devient <c>photo.jpg_p</c>, dont
    ///   l'extension n'est plus celle d'une image — et en brouille le début ;
    /// - elle affranchit la commande de DiLand pour de bon. Une commande reprise doit
    ///   pouvoir être réimprimée des semaines plus tard, quand DiLand aura purgé la
    ///   sienne.
    /// </remarks>
    public DiLandImportOutcome Import(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var avertissements = new List<string>();

        // une commande n'est reprise qu'une fois : une deuxième reprise créerait une
        // deuxième commande Studio, donc un doublon de tirage
        if (Journal.Find(order.Oid) is { } suivi && (suivi.StudioOrderId is not null || !suivi.IsOpen))
            return new DiLandImportOutcome(order, null, ["déjà reprise"]);

        var photos = new List<DraftItem>();

        // les fichiers sont recopiés une fois pour toutes, sous leur nom de base
        var prete = Archiver(order);

        foreach (var ligne in LignesDe(order))
        {
            var produit = MatchProduct(ligne.ProductName);
            if (produit is null)
            {
                avertissements.Add($"produit inconnu au catalogue : « {ligne.ProductName} »");
                continue;
            }

            foreach (var photo in ligne.Photos)
            {
                var chemin = Path.Combine(prete.PhotosDirectory, photo.FileName);

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
                    // l'EXIF est déjà appliqué au rendu : reprendre l'angle de DiLand tel
                    // quel le compterait deux fois (voir QuartsDeTourResiduels)
                    RotationQuarterTurns: QuartsDeTourResiduels(photo, chemin),
                    // Les bornes redressent : le contraire était écrit ici, et 113 photos
                    // de la base de la boutique portent un redressement qui partait à la
                    // poubelle. Le tirage sortait de travers sans que rien ne le dise.
                    FineRotationDegrees: photo.FineRotationDegrees,
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

    /// <summary>
    /// Ce que le client a réglé sur une photo à la borne, tel qu'il faut le reposer dans
    /// l'écran des photos.
    /// </summary>
    /// <param name="Crop">Recadrage en fractions de l'image ; <c>CropSpec.Full</c> si aucun.</param>
    /// <param name="QuartsDeTour">Rotation par quarts de tour horaires.</param>
    /// <param name="RedressementDegres">Redressement fin, le « Tilt » de DiLand.</param>
    /// <param name="Quantite">Nombre d'exemplaires commandés.</param>
    /// <param name="CodeProduit">
    /// Produit de la LIGNE d'où vient cette photo, et non le produit majoritaire de la
    /// commande : une commande mixte 10×15 + 13×18 doit s'ouvrir juste.
    /// </param>
    public sealed record CadrageBorne(
        CropSpec Crop,
        int QuartsDeTour,
        double RedressementDegres,
        int Quantite,
        string? CodeProduit);

    /// <summary>Ce qu'il faut pour ouvrir une commande de borne dans l'écran des photos.</summary>
    /// <param name="PhotosDirectory">Dossier où les photos ont été recopiées.</param>
    /// <param name="ProductCode">Produit majoritaire de la commande, à présélectionner.</param>
    /// <param name="PhotoCount">Nombre de photos recopiées.</param>
    /// <param name="Cadrages">
    /// Ce que le client a réglé, par nom de fichier.
    ///
    /// <b>C'est ce qui manquait au parcours « Modifier ».</b> Il recopie les FICHIERS puis
    /// rescanne le dossier : l'écran ne voyait donc que des images, et le recadrage, les
    /// rotations et les quantités du client disparaissaient à l'ouverture. Le parcours
    /// « Reprendre » (<see cref="Import"/>), lui, les portait déjà — d'où deux tirages
    /// différents pour la même commande selon le bouton pressé.
    ///
    /// Vide quand la commande a été retrouvée par ses seuls fichiers, DiLand l'ayant
    /// purgée de sa base : il n'y a alors plus rien à reprendre.
    /// </param>
    public sealed record StagedOrder(
        string PhotosDirectory,
        string? ProductCode,
        int PhotoCount,
        IReadOnlyDictionary<string, CadrageBorne> Cadrages);

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
    /// <param name="ecraser">
    /// Refaire la copie même si le fichier est déjà là.
    ///
    /// Par défaut on ne recopie pas ce qui existe : ouvrir deux fois la même commande dans
    /// la journée ne doit pas relire cinquante fichiers. Mais un SECOND téléchargement
    /// demandé par l'opérateur rouvrait alors un dossier périmé sans rien dire — c'est
    /// exactement ce qu'on lui reproche quand il redemande les photos.
    /// </param>
    public StagedOrder Stage(DiLandOrder order, string workDirectory, string? folderName = null,
        bool ecraser = false)
    {
        ArgumentNullException.ThrowIfNull(order);

        var destination = Path.Combine(workDirectory, folderName ?? order.DirectoryName);
        Directory.CreateDirectory(destination);

        var lignes = LignesDe(order);

        // une même photo peut figurer sur deux lignes — commandée en 10x15 et en 13x18 par
        // exemple. On ne la met à disposition qu'une fois, et on la compte une fois
        var deposees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ce que le client a réglé, relevé au MÊME parcours que la recopie : la donnée est
        // là depuis toujours, on ne la lisait simplement pas (voir StagedOrder.Cadrages)
        var cadrages = new Dictionary<string, CadrageBorne>(StringComparer.OrdinalIgnoreCase);

        foreach (var ligne in lignes)
        {
            var produit = MatchProduct(ligne.ProductName);

            foreach (var photo in ligne.Photos)
            {
                if (!File.Exists(_depot.PhotoPath(order, photo))) continue;

                // la recopie remet la photo en clair : DiLand brouille le début des fichiers
                // des commandes qu'il a traitées
                _depot.CopyPhotoTo(order, photo, Path.Combine(destination, photo.FileName), ecraser);
                deposees.Add(photo.FileName);

                // première occurrence gagne, comme la recopie : la photo n'apparaît qu'une
                // fois dans la grille, elle ne peut donc porter qu'un cadrage
                //
                // la rotation se lit sur la COPIE, qui vient d'être écrite en clair : c'est
                // la seule à porter un en-tête EXIF lisible (voir QuartsDeTourResiduels)
                cadrages.TryAdd(photo.FileName, new CadrageBorne(
                    CropOf(photo),
                    QuartsDeTourResiduels(photo, Path.Combine(destination, photo.FileName)),
                    photo.FineRotationDegrees,
                    Math.Max(1, photo.Quantity),
                    produit?.Code));
            }
        }

        // Rien en base, mais des fichiers sur le disque : c'est une commande que DiLand a
        // purgée de sa base alors que son dossier est toujours là. On prend ce qu'on
        // trouve — mieux vaut les photos sans les quantités que l'écran vide qui s'affichait
        // jusqu'ici quand un client revenait trois semaines plus tard.
        if (deposees.Count == 0)
            foreach (var chemin in _depot.PhotosOf(order))
            {
                var nom = DiLandRepository.CleanFileName(Path.GetFileName(chemin));
                _depot.CopyFileTo(chemin, Path.Combine(destination, nom), ecraser);
                deposees.Add(nom);
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
            deposees.Count,
            cadrages);
    }

    /// <summary>
    /// Les photos d'une commande close, telles que Studio les a gardées.
    ///
    /// <b>On ne retourne PAS chez DiLand.</b> C'est notre archive qu'on sert, et elle
    /// suffit : c'est justement pour cela qu'elle existe. DiLand peut avoir purgé la
    /// commande, être fermé, ou avoir été réinstallé — l'historique s'en moque.
    ///
    /// Rend null quand nous n'avons rien gardé : les entrées antérieures à l'archivage,
    /// et celles dont le dossier a été effacé à la main. À l'appelant de le dire, plutôt
    /// que d'ouvrir un dossier vide.
    /// </summary>
    public string? ArchiveDe(KioskOrderEntry entree)
    {
        ArgumentNullException.ThrowIfNull(entree);

        var dossier = string.IsNullOrWhiteSpace(entree.ArchiveDirectory)
            ? Path.Combine(ArchiveRoot, entree.Oid.ToString())
            : entree.ArchiveDirectory;

        if (!Directory.Exists(dossier)) return null;

        // un dossier vide ne vaut pas mieux qu'un dossier absent : la copie a pu échouer
        return Directory.EnumerateFiles(dossier).Any() ? dossier : null;
    }

    /// <summary>
    /// Rattrape une commande close dont nous n'avons pas d'archive : les entrées d'avant,
    /// et celles archivées puis effacées à la main.
    ///
    /// C'est le SEUL cas où l'historique redescend chez DiLand, et il est temporaire par
    /// nature — au bout d'un mois, plus aucune entrée n'aura connu l'avant. Rend null si
    /// DiLand ne connaît plus la commande non plus.
    /// </summary>
    public string? ArchiverDepuisDiLand(KioskOrderEntry entree)
    {
        ArgumentNullException.ThrowIfNull(entree);

        var commande = RetrouverChezDiLand(entree);
        if (commande is null) return null;

        var prete = Archiver(commande, refaire: true);
        return prete.PhotoCount > 0 ? prete.PhotosDirectory : null;
    }

    /// <summary>La commande telle que DiLand la connaît encore, ou telle que le journal l'a notée.</summary>
    private DiLandOrder? RetrouverChezDiLand(KioskOrderEntry entree)
    {
        if (_depot.RefreshSnapshot())
        {
            var connue = _depot.ReadOrdersAfter(entree.Oid - 1, 1)
                .FirstOrDefault(c => c.Oid == entree.Oid);
            if (connue is not null) return connue;
        }

        // les entrées écrites avant que le journal retienne le dossier n'ont rien à offrir
        if (string.IsNullOrWhiteSpace(entree.DirectoryName)) return null;

        var reconstituee = new DiLandOrder(
            Oid: entree.Oid,
            Number: entree.Number,
            DailyNumber: entree.DailyNumber,
            Date: entree.OrderedAt,
            DirectoryName: entree.DirectoryName,
            EndUserName: entree.CustomerName,
            PhotoCount: 0);

        return _depot.PhotosOf(reconstituee).Count > 0 ? reconstituee : null;
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

        // C'est ICI que l'archive se constitue : au moment où l'on prend la commande en
        // charge, donc tant que les fichiers de DiLand sont sûrement là. Attendre la
        // clôture serait trop tard — DiLand purge quand il veut.
        try { Archiver(order); }
        catch (Exception) { /* l'archive est un confort, pas une condition pour servir */ }

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
    /// Le recadrage fait à la borne, ou l'image entière.
    ///
    /// DiLand l'exprime en PIXELS ; il est ramené en fractions dès la lecture, par
    /// <see cref="DiLandOrderPhoto.FromRaw"/>. Un rectangle incohérent est ignoré plutôt
    /// que de produire un tirage faux.
    ///
    /// <b>Lu ici pour les DEUX parcours</b> — « Reprendre » (<see cref="Import"/>) et
    /// « Modifier » (<see cref="Stage"/>). Deux lectures du même recadrage finiraient par
    /// diverger, et le même bouton ne tirerait plus la même chose selon l'écran.
    /// </summary>
    internal static CropSpec CropOf(DiLandOrderPhoto photo)
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

    /// <summary>
    /// La rotation qu'il RESTE à appliquer après <c>AutoOrient</c>, en quarts de tour
    /// horaires.
    ///
    /// <b>L'« Angle » de DiLand n'est pas la rotation du client : c'est la rotation TOTALE
    /// depuis le fichier brut, orientation EXIF comprise.</b> Studio, lui, applique
    /// toujours l'EXIF d'abord (<c>ImagePipeline.RenderInto</c> appelle <c>AutoOrient</c>),
    /// puis les quarts de tour. Reprendre l'angle de DiLand tel quel les additionnait :
    /// une photo de téléphone en portrait — EXIF 8, donc Angle 270 — était redressée par
    /// l'EXIF puis tournée de 270° DE PLUS. Elle partait couchée, et le recadrage du
    /// client, exprimé lui aussi dans le repère redressé, tombait à côté.
    ///
    /// Relevé sur la base de la boutique le 05/08/2026 : sur 185 photos d'angle non nul,
    /// 183 ont un Angle égal à leur orientation EXIF au degré près. Les deux autres sont
    /// de VRAIES rotations faites à la borne — fichiers sans EXIF, tournés d'un quart —
    /// et c'est pourquoi on soustrait au lieu d'ignorer l'angle : les ignorer ferait
    /// sortir ces deux-là de travers.
    ///
    /// L'invariant qui le vérifie : après cette rotation, les côtés de l'image doivent
    /// tomber sur les <c>Width</c> × <c>Height</c> notés par DiLand — c'est le repère dans
    /// lequel son recadrage est exprimé.
    /// </summary>
    /// <param name="cheminEnClair">
    /// La photo DÉJÀ RECOPIÉE, donc débrouillée. Lire l'original ne donnerait rien : DiLand
    /// passe au XOR les 1024 premiers octets des commandes traitées, c'est-à-dire l'en-tête
    /// EXIF lui-même.
    /// </param>
    internal static int QuartsDeTourResiduels(DiLandOrderPhoto photo, string cheminEnClair)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var total = QuarterTurns(photo.Angle);
        var dejaFaits = OrientationExif.QuartsDeTour(cheminEnClair);

        return ((total - dejaFaits) % 4 + 4) % 4;
    }

}
