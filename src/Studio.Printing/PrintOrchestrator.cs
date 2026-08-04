using System.Drawing;
using System.Text.Json;
using ImageMagick;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;
using Studio.Printing.Devices.Fuji;
using Studio.Store;

namespace Studio.Printing;

/// <summary>État d'impression d'une enveloppe, persisté dans spool/envNN.state.</summary>
public sealed record SpoolState(string Status, DateTimeOffset At)
{
    public const string Rendering = "Rendering";
    public const string Spooled = "Spooled";
    public const string Printed = "Printed";

    /// <summary>Fichiers prêts, impression manuelle attendue (voir <see cref="ProductOutput.ManualFile"/>).</summary>
    public const string AwaitingManualPrint = "AwaitingManualPrint";

    /// <summary>
    /// L'opérateur a arrêté l'enveloppe en cours de route. Ce qui était déjà parti a été
    /// rappelé quand la machine le permettait ; le reste n'a jamais été envoyé.
    /// </summary>
    public const string Canceled = "Canceled";

    /// <summary>
    /// L'imprimante n'était pas en état de tirer — capot ouvert, rouleau à changer,
    /// bourrage. L'enveloppe attend son tour ; <see cref="PendingPrintQueue"/> la
    /// reprendra dès que la machine répondra, à la page où elle s'était arrêtée.
    /// </summary>
    public const string Waiting = "Waiting";
}

/// <summary>
/// Où en était une enveloppe quand elle s'est interrompue.
/// </summary>
/// <param name="PagesRemises">
/// Pages physiques déjà confiées à Windows. La reprise saute celles-là : sans ce compte,
/// un bourrage à la vingtième photo d'une planche de trente en refaisait trente.
/// </param>
/// <param name="Raison">Ce qui a arrêté le tirage, pour l'écran de reprise.</param>
public sealed record PrintResumePoint(int PagesRemises, string Raison, DateTimeOffset At);

/// <summary>
/// L'imprimante n'était pas en état de tirer. L'enveloppe est mise en attente, pas perdue.
///
/// Distincte des autres échecs : l'appelant doit dire « en attente » et non « échec »,
/// et surtout ne rien proposer de réimprimer — la reprise est automatique.
/// </summary>
public sealed class PrinterNotReadyException(string message) : Exception(message);

/// <summary>
/// Où en est l'impression d'une enveloppe, à destination du bandeau des machines.
///
/// Le suivi se résumait à « Impression en cours — commande 01-014 », sans dire combien
/// de tirages restaient ni sur quelle machine : sur une commande de trente tirages,
/// impossible de savoir s'il fallait attendre dix secondes ou trois minutes.
/// </summary>
/// <param name="Etape">Ce qui se passe : <see cref="Rendu"/>, <see cref="Envoi"/>…</param>
/// <param name="Faits">Nombre d'unités terminées à cette étape.</param>
/// <param name="Total">Nombre d'unités de l'étape ; 0 si on ne le sait pas encore.</param>
/// <param name="Machine">
/// Machine visée, telle que le bandeau la nomme (« A », « B », « D »), ou null quand la
/// destination n'est pas une machine identifiable — l'avancement se lit alors au centre
/// du bandeau plutôt que dans une tuile.
/// </param>
public sealed record PrintProgress(string Etape, int Faits, int Total, string? Machine = null)
{
    public const string Rendu = "Préparation des photos";
    public const string Envoi = "Envoi au minilab";
    public const string Impression = "Impression";
    public const string Annulation = "Annulation";

    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Faits / (double)Total, 0, 1);
}

/// <summary>
/// L'opérateur a demandé l'arrêt : l'enveloppe s'interrompt, et ce qui est déjà parti est
/// rappelé quand la machine sait le faire.
///
/// Distincte d'<see cref="OperationCanceledException"/> pour que l'appelant sache que
/// l'arrêt est VOULU et non un incident — une commande annulée ne doit pas s'afficher
/// comme une commande en échec.
/// </summary>
public sealed class PrintCanceledException(string message) : Exception(message);

/// <summary>
/// Orchestration rendu → impression, avec la garantie anti-« replay storm » :
/// l'état Spooled est persisté sur disque AVANT l'envoi au spouleur, et une
/// enveloppe retrouvée dans cet état après un crash n'est JAMAIS resoumise
/// automatiquement — c'est l'opérateur qui tranche (confirmée / à réimprimer).
/// Un crash coûte au pire une confirmation manuelle, jamais un tirage en double.
/// </summary>
public sealed class PrintOrchestrator
{
    private readonly ProductCatalog _catalog;
    private readonly OrderFolderStore _store;
    private readonly string _catalogDir;
    private readonly IMinilabPrinter? _minilab;

    /// <summary>
    /// Journal optionnel : trace la durée de rendu de chaque tirage.
    ///
    /// Le rendu écrit un PNG aux dimensions du produit — 48 Mpx pour un 50×70 à 300 ppp — et
    /// c'est lui qui s'exécute quand l'opérateur appuie sur « Imprimer » dans la grille,
    /// AVANT que la boîte d'agrandissement ne s'ouvre. Les deux attentes se confondaient.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <param name="catalogDir">Dossier catalog/ contenant les DEVMODE et profils ICC.</param>
    /// <param name="minilab">
    /// Accès au minilab Fuji. Null = les produits qui en dépendent seront refusés
    /// explicitement plutôt que renvoyés vers un spouleur qui les jetterait.
    /// </param>
    public PrintOrchestrator(ProductCatalog catalog, OrderFolderStore store, string catalogDir,
        IMinilabPrinter? minilab = null)
    {
        _catalog = catalog;
        _store = store;
        _catalogDir = catalogDir;
        _minilab = minilab;
    }

    /// <summary>
    /// Imprime une enveloppe complète. <paramref name="operatorConfirmed"/> doit être
    /// vrai pour resoumettre une enveloppe déjà passée à l'état Spooled.
    /// </summary>
    /// <param name="progression">Averti à chaque tirage préparé puis envoyé ; peut être null.</param>
    /// <param name="ct">
    /// Arrêt demandé par l'opérateur. Il est examiné entre deux tirages, jamais au milieu
    /// d'un envoi : une commande à moitié transmise au minilab serait pire que pas
    /// d'annulation du tout.
    /// </param>
    public void PrintEnvelope(Order order, Envelope envelope, bool operatorConfirmed = false,
        string? pdfOverridePath = null, IProgress<PrintProgress>? progression = null,
        CancellationToken ct = default)
    {
        var state = ReadSpoolState(order, envelope);
        if (state?.Status is SpoolState.Spooled or SpoolState.Printed && !operatorConfirmed)
            throw new InvalidOperationException(
                $"L'enveloppe {order.DisplayNumber}/{envelope.Number} a déjà été envoyée à l'impression " +
                "— confirmation opérateur requise pour réimprimer.");

        // Une enveloppe EN ATTENTE se reprend sans confirmation : elle n'a rien imprimé de
        // trop, elle a été interrompue. C'est tout l'intérêt de la file d'attente.
        var reprise = state?.Status == SpoolState.Waiting
            ? ReadResumePoint(order, envelope)
            : null;

        var products = envelope.Lines.Select(l => _catalog.Require(l.ProductCode)).ToList();

        // une enveloppe emprunte un seul circuit : spouleur, fichiers, ou minilab
        var circuits = products.Select(p => p.Output).Distinct().ToList();
        if (circuits.Count > 1)
            throw new InvalidOperationException(
                $"L'enveloppe {order.DisplayNumber}/{envelope.Number} mélange plusieurs circuits " +
                $"d'impression ({string.Join(", ", circuits)}). Ils doivent être séparés : donnez à ces " +
                "produits des canaux d'impression distincts dans le catalogue.");

        // L'envoi par courriel ne passe par aucune machine. L'enveloppe existe pour porter
        // la ligne et son prix — ticket, total, statistiques — et rien d'autre : elle est
        // close ici même. La lui faire traverser le rendu la mettrait « en attente
        // d'imprimante » pour une prestation qui n'en demande aucune, et la commande ne
        // passerait jamais « Prête ».
        if (circuits[0] == ProductOutput.Email)
        {
            WriteSpoolState(order, envelope, SpoolState.Printed);
            envelope.Status = EnvelopeStatus.Printed;
            Log?.Invoke($"Enveloppe {order.DisplayNumber}/{envelope.Number} : envoi par courriel, " +
                        "rien à imprimer.");
            return;
        }

        var manualPrinting = circuits[0] == ProductOutput.ManualFile;
        var minilabPrinting = circuits[0] == ProductOutput.FujiMinilab;

        if (minilabPrinting && _minilab is null)
            throw new InvalidOperationException(
                "Le minilab Fuji n'est pas accessible depuis cette application : le relais 32 bits " +
                "n'a pas été fourni. Aucun tirage n'a été envoyé.");

        // une file sur le port `nul` avale les travaux sans rien imprimer : mieux vaut
        // refuser franchement que rendre, spouler et annoncer un succès imaginaire
        if (!manualPrinting && !minilabPrinting)
        {
            foreach (var printerName in products.Select(p => p.PrinterName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (PrinterPorts.IsNullPort(printerName))
                    throw new InvalidOperationException(
                        $"L'imprimante « {printerName} » est branchée sur le port « nul » : Windows accepte " +
                        "les travaux et les jette, aucun tirage ne sortira.\n\n" +
                        "C'est normal et voulu : le minilab DE100 ne se pilote pas par le spouleur Windows " +
                        "mais par le SDK Fuji (PModuleIF.dll). NE CHANGEZ PAS le port de cette file — DiLand " +
                        "en dépend, et le modifier l'empêcherait d'imprimer.\n\n" +
                        "Le pilote qui parle au minilab existe déjà dans Studio, mais il n'est pas encore " +
                        "raccordé : le SDK Fuji est en 32 bits alors que l'application tourne en 64 bits, " +
                        "il faut un processus relais.\n\n" +
                        "En attendant, choisissez un produit imprimé sur la DS620.");
            }

            // Le format doit être vérifié ICI, avant le rendu et avant que l'enveloppe ne
            // passe à « Spooled ». Une imprimante à sublimation ne connaît que ses propres
            // formes de papier : lui en demander une autre ne donne pas un tirage
            // approximatif mais AUCUN tirage, et sans erreur. Voir BitmapPrinter.
            foreach (var produit in products.DistinctBy(p => p.Code))
                BitmapPrinter.EnsurePageSizeAvailable(produit.PrinterName, produit.WidthMm, produit.HeightMm);

            // Machine pas en état de tirer : on met l'enveloppe en attente au lieu
            // d'échouer. Le rouleau qu'on change ou le capot resté ouvert durent deux
            // minutes ; le travail doit patienter, pas se perdre.
            foreach (var nom in products.Select(p => p.PrinterName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var etat = PrinterReadiness.Check(nom);
                if (etat.CanPrint) continue;

                MettreEnAttente(order, envelope, reprise?.PagesRemises ?? 0, etat.Reason);
                throw new PrinterNotReadyException(
                    $"Commande {order.DisplayNumber} mise en attente — {etat.Reason}.\n\n" +
                    "Elle partira toute seule dès que l'imprimante sera prête. " +
                    "Rien n'est perdu, et rien ne sera imprimé en double.");
            }
        }

        WriteSpoolState(order, envelope, SpoolState.Rendering);
        envelope.Status = EnvelopeStatus.Rendering;
        _store.Save(order);
        _store.AppendEvent(order, "render-start", $"env={envelope.Number}");

        List<RenderedPage> pages;
        try
        {
            pages = RenderEnvelope(order, envelope, progression, ct);
        }
        catch (OperationCanceledException)
        {
            // arrêt pendant la préparation : RIEN n'est parti, c'est le cas le plus propre
            MarquerAnnulee(order, envelope, "pendant la préparation, rien n'avait été envoyé");
            throw new PrintCanceledException(
                $"Commande {order.DisplayNumber} arrêtée avant le moindre envoi : aucun tirage ne sortira.");
        }

        // circuit manuel : les fichiers sont prêts, rien ne part au spouleur. L'opérateur
        // les ouvre dans Photoshop et confirme ensuite via ConfirmPrinted.
        if (manualPrinting)
        {
            WriteSpoolState(order, envelope, SpoolState.AwaitingManualPrint);
            envelope.Status = EnvelopeStatus.AwaitingManualPrint;
            _store.Save(order);
            _store.AppendEvent(order, "awaiting-manual-print",
                $"env={envelope.Number}, fichiers={pages.Sum(p => p.Copies)}, dossier={_store.GetRendersFolder(order)}");
            return;
        }

        // circuit minilab : on passe par le SDK Fuji, jamais par le spouleur
        if (minilabPrinting)
        {
            SubmitToMinilab(order, envelope, pages, progression, ct);
            return;
        }

        // moment décisif : on grave « Spooled » sur disque AVANT de soumettre au spouleur
        WriteSpoolState(order, envelope, SpoolState.Spooled);
        envelope.Status = EnvelopeStatus.Spooled;
        _store.Save(order);
        var destinations = string.Join(", ", pages
            .GroupBy(p => p.Product.PrinterName)
            .Select(g => $"{g.Key} × {g.Sum(p => p.Copies)}"));
        _store.AppendEvent(order, "spool-start",
            $"env={envelope.Number}, pages={pages.Sum(p => p.Copies)}, destinations=[{destinations}]");

        var deja = reprise?.PagesRemises ?? 0;
        if (deja > 0)
            _store.AppendEvent(order, "spool-resume",
                $"env={envelope.Number}, reprise à la page {deja + 1}");

        try
        {
            PrintPages(pages, pdfOverridePath, $"Studio {order.DisplayNumber}-{envelope.Number}",
                progression, ct,
                depart: deja,
                noterAvancement: faites => WriteResumePoint(order, envelope, faites, "impression en cours"));
        }
        catch (OperationCanceledException)
        {
            // Windows a déjà pris ce qui lui a été donné : on ne prétend pas le reprendre.
            // On arrête d'en donner, et on le dit tel quel.
            MarquerAnnulee(order, envelope,
                "en cours d'impression : les pages déjà remises à Windows peuvent encore sortir");
            throw new PrintCanceledException(
                $"Commande {order.DisplayNumber} arrêtée. Les pages déjà remises au spouleur " +
                "Windows peuvent encore sortir : videz la file de l'imprimante pour les retenir.");
        }
        catch (Exception ex)
        {
            // Le spouleur a refusé en cours de route — bourrage, machine éteinte, câble.
            // Le point de reprise dit où on en était : l'enveloppe repartira de là plutôt
            // que de refaire les pages déjà sorties.
            var faites = ReadResumePoint(order, envelope)?.PagesRemises ?? deja;
            MettreEnAttente(order, envelope, faites, ex.Message);

            throw new PrinterNotReadyException(
                $"Commande {order.DisplayNumber} interrompue après {faites} page(s) — {ex.Message}.\n\n" +
                "Elle reprendra à la page suivante dès que l'imprimante sera prête : " +
                "les pages déjà sorties ne seront pas refaites.");
        }

        ClearResumePoint(order, envelope);
        WriteSpoolState(order, envelope, SpoolState.Printed);
        envelope.Status = EnvelopeStatus.Printed;
        _store.Save(order);
        _store.AppendEvent(order, "printed", $"env={envelope.Number}");
    }

    /// <summary>
    /// Range l'enveloppe en attente, avec le rang de la dernière page sortie.
    ///
    /// L'enveloppe reste <see cref="EnvelopeStatus.Pending"/> et NON « Spooled » : c'est
    /// ce qui la rend reprenable sans confirmation de l'opérateur, et ce qui empêche
    /// l'écran de démarrage de la proposer à la réimpression comme un travail douteux.
    /// </summary>
    private void MettreEnAttente(Order order, Envelope envelope, int pagesRemises, string raison)
    {
        WriteResumePoint(order, envelope, pagesRemises, raison);
        WriteSpoolState(order, envelope, SpoolState.Waiting);
        envelope.Status = EnvelopeStatus.Pending;
        _store.Save(order);
        _store.AppendEvent(order, "print-waiting",
            $"env={envelope.Number}, pages sorties={pagesRemises}, raison={raison}");
        Log?.Invoke($"Commande {order.DisplayNumber}/{envelope.Number} en attente d'imprimante : {raison}");
    }

    /// <summary>
    /// Enveloppes qui attendent une imprimante. <see cref="PendingPrintQueue"/> les reprend
    /// dès que la machine répond.
    /// </summary>
    public List<(Order Order, Envelope Envelope)> FindWaitingEnvelopes(IEnumerable<Order> orders)
    {
        var attente = new List<(Order, Envelope)>();
        foreach (var order in orders)
            foreach (var envelope in order.Envelopes)
                if (ReadSpoolState(order, envelope)?.Status == SpoolState.Waiting)
                    attente.Add((order, envelope));
        return attente;
    }

    /// <summary>
    /// À appeler au démarrage : enveloppes retrouvées à l'état Spooled sans confirmation
    /// d'impression — un crash a eu lieu entre la soumission et la fin. L'opérateur
    /// doit dire si le tirage est sorti ou s'il faut réimprimer.
    /// </summary>
    public List<(Order Order, Envelope Envelope)> FindEnvelopesNeedingConfirmation(IEnumerable<Order> orders)
    {
        var result = new List<(Order, Envelope)>();
        foreach (var order in orders)
            foreach (var envelope in order.Envelopes)
                if (ReadSpoolState(order, envelope)?.Status == SpoolState.Spooled)
                    result.Add((order, envelope));
        return result;
    }

    /// <summary>
    /// Envoie les pages rendues au minilab Fuji.
    ///
    /// Comme pour le spouleur, l'état « Spooled » est gravé sur disque AVANT le premier
    /// envoi : un plantage en cours de route ne doit jamais provoquer un renvoi
    /// automatique. L'enveloppe reste dans cet état jusqu'à ce que le minilab confirme
    /// ses tirages ou que l'opérateur tranche.
    /// </summary>
    private void SubmitToMinilab(Order order, Envelope envelope, List<RenderedPage> pages,
        IProgress<PrintProgress>? progression, CancellationToken ct)
    {
        var machine = ChooseMinilabMachine(pages);

        // Interroger le rouleau chargé est la PREMIÈRE chose qui touche la machine, donc
        // la première qui peut rester suspendue quand elle dort. Une commande de 41 photos
        // est restée douze minutes ici sans un mot le 03/08/2026 : le rendu était fait, et
        // rien ne disait à l'opérateur ce qu'on attendait.
        //
        // Elle part maintenant EN ATTENTE, comme pour une imprimante pas prête : la file
        // la reprendra dès que le minilab répondra, et l'opérateur sait pourquoi.
        int paperWidthMm;
        try
        {
            paperWidthMm = _minilab!.LoadedPaperWidthMm(machine);
        }
        catch (Exception ex)
        {
            MettreEnAttente(order, envelope, 0,
                $"le minilab {machine} ne répond pas ({ex.Message})");

            throw new PrinterNotReadyException(
                $"Commande {order.DisplayNumber} mise en attente : le minilab {machine} ne " +
                "répond pas — il est probablement en veille.\n\n" +
                "Réveillez-le : la commande partira toute seule, sans rien réimprimer.");
        }

        // vérifié AVANT le moindre envoi : demander un format que le rouleau chargé ne
        // permet pas ne donne pas un tirage plus petit, mais un tirage faux — la machine
        // avertit que le papier n'est pas adapté et gâche la feuille. Constaté en
        // boutique le 01/08/2026.
        EnsurePaperFits(pages, machine, paperWidthMm);

        WriteSpoolState(order, envelope, SpoolState.Spooled);
        envelope.Status = EnvelopeStatus.Spooled;
        _store.Save(order);
        _store.AppendEvent(order, "minilab-submit-start",
            $"env={envelope.Number}, machine={machine}, tirages={pages.Sum(p => p.Copies)}");

        // la finition doit être celle du papier réellement chargé : annoncer « brillant »
        // sur du lustré fausse le rendu
        var surface = _minilab.LoadedSurface(machine);

        var cible = machine.ToString();
        var jobs = new List<De100PrintJob>(pages.Count);

        // PRÉPARATION — c'est ici que l'arrêt s'examine, et nulle part ailleurs : rien
        // n'est encore parti, tout est rattrapable. Une fois la commande transmise elle est
        // entière, et c'est ce qu'on veut : une demi-commande ouverte côté minilab est
        // exactement le genre d'ordre fantôme qui bloque la file du DE100.
        for (var i = 0; i < pages.Count; i++)
        {
            if (ct.IsCancellationRequested)
                throw new PrintCanceledException(DecrireArret(order, 0, pages.Count));

            var page = pages[i];
            var (largeur, longueur) = MinilabPrintSize(page.WidthMm, page.HeightMm, paperWidthMm);
            var image = FitPageToRoll(page, largeur, longueur);

            jobs.Add(new De100PrintJob(
                JobId: MinilabJobId(order.DisplayNumber, envelope.Number, i + 1),
                ImagePath: image,
                WidthMm: largeur,
                HeightMm: longueur,
                PrintSizeName: MinilabSizeName(page.Product, largeur, longueur),
                Surface: surface,
                Copies: page.Copies));

            progression?.Report(new PrintProgress(
                PrintProgress.Envoi, i + 1, pages.Count, cible));
        }

        // ENVOI — UNE commande minilab pour toute l'enveloppe. Studio en ouvrait une par
        // photo, ce que rien dans le SDK ne prévoit : sur la commande 04-007 du
        // 04/08/2026, quatre commandes ouvertes et refermées en 1,2 s ont toutes été
        // acceptées, et deux tirages sur quatre ne sont jamais sortis. Voir
        // De100Driver.Submit.
        var handle = _minilab.Submit(jobs, machine);

        // L'arrêt demandé PENDANT l'envoi reste rattrapable : le SDK sait annuler une
        // commande que la machine n'a pas encore tirée (PIF_CancelOrder), et c'est le geste
        // que DiLand ne sait pas faire. Il n'y a plus qu'un handle à rappeler au lieu d'un
        // par photo, mais le bouton d'arrêt garde exactement le même pouvoir.
        if (ct.IsCancellationRequested)
        {
            RappelerDuMinilab(order, envelope, [handle], machine, progression);
            throw new PrintCanceledException(DecrireArret(order, jobs.Count, pages.Count));
        }

        _store.AppendEvent(order, "minilab-submitted",
            $"env={envelope.Number}, machine={machine}, commande={handle}, tirages={jobs.Count}");
    }

    /// <summary>
    /// Rappelle au minilab les commandes déjà transmises.
    ///
    /// C'est ce que DiLand ne fait pas : chez lui, une commande partie est partie, et
    /// l'unique recours est d'aller vider la file SUR la machine. Le SDK sait pourtant
    /// annuler — <c>PIF_CancelOrder</c> — et c'est ce qu'on appelle ici, commande par
    /// commande, en continuant même si l'une refuse : une seule qui résiste ne doit pas
    /// laisser les autres partir à l'impression.
    ///
    /// Ce qui est déjà SORTI ne revient évidemment pas ; l'annulation ne vaut que pour ce
    /// que la machine n'a pas encore tiré.
    /// </summary>
    private void RappelerDuMinilab(Order order, Envelope envelope, List<string> handles,
        char machine, IProgress<PrintProgress>? progression)
    {
        var rappelees = 0;
        var recalcitrantes = new List<string>();

        for (var i = 0; i < handles.Count; i++)
        {
            progression?.Report(new PrintProgress(
                PrintProgress.Annulation, i, handles.Count, machine.ToString()));
            try
            {
                _minilab!.Cancel(handles[i]);
                rappelees++;
            }
            catch (Exception ex)
            {
                // déjà tirée, déjà terminée, ou minilab qui refuse : on note et on continue
                recalcitrantes.Add($"{handles[i]} ({ex.Message})");
            }
        }

        _store.AppendEvent(order, "minilab-canceled",
            $"env={envelope.Number}, machine={machine}, rappelées={rappelees}/{handles.Count}" +
            (recalcitrantes.Count == 0 ? "" : $", refusées=[{string.Join("; ", recalcitrantes)}]"));

        MarquerAnnulee(order, envelope,
            $"{rappelees} commande(s) rappelée(s) sur {handles.Count} envoyée(s) à la machine {machine}");
    }

    /// <summary>
    /// Identifiant d'un tirage envoyé au minilab.
    ///
    /// C'est la SEULE chose que la machine nous rappellera quand le tirage sortira : il
    /// doit donc contenir de quoi retrouver la commande. Fabrication et relecture sont
    /// posées côte à côte pour qu'elles ne puissent pas diverger.
    /// </summary>
    public static string MinilabJobId(string displayNumber, int envelope, int rang) =>
        $"{displayNumber}-{envelope}-{rang:000}";

    /// <summary>
    /// La commande qu'un identifiant de tirage désigne, ou null s'il ne vient pas de nous.
    ///
    /// Attention au découpage : le numéro affiché contient lui-même un tiret (« 01-016 »),
    /// donc on retire les DEUX derniers segments, jamais le premier.
    /// </summary>
    public static string? OrderNumberOf(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;

        var parties = jobId.Split('-');
        return parties.Length < 3 ? null : string.Join('-', parties[..^2]);
    }

    private static string DecrireArret(Order order, int envoyees, int total) =>
        envoyees == 0
            ? $"Commande {order.DisplayNumber} arrêtée avant le moindre envoi : aucun tirage ne sortira."
            : $"Commande {order.DisplayNumber} arrêtée : {envoyees} tirage(s) sur {total} étaient " +
              "partis au minilab, ils lui ont été rappelés. Ceux qu'il avait déjà sortis restent sortis.";

    /// <summary>
    /// Grave l'arrêt : sur disque d'abord, dans la commande ensuite, dans le journal enfin.
    ///
    /// L'état compte autant que le geste — une enveloppe laissée à « Spooled » après une
    /// annulation serait proposée à la réimpression au prochain démarrage, ce qui est
    /// précisément la tempête de renvois qu'on cherche à ne jamais reproduire.
    /// </summary>
    private void MarquerAnnulee(Order order, Envelope envelope, string detail)
    {
        WriteSpoolState(order, envelope, SpoolState.Canceled);
        envelope.Status = EnvelopeStatus.Canceled;
        _store.Save(order);
        _store.AppendEvent(order, "canceled-by-operator", $"env={envelope.Number}, {detail}");
    }

    /// <summary>
    /// Machine visée : celle demandée par le produit si elle est prête, sinon la première
    /// machine prête. Le DE100 de la boutique en compte deux, dont une souvent hors ligne.
    /// </summary>
    /// <summary>
    /// Machine du minilab choisie par l'opérateur pour la session en cours. Elle prime sur
    /// celle éventuellement fixée par le produit ; null = choix automatique.
    /// </summary>
    public string? PreferredMinilabMachine { get; set; }

    private char ChooseMinilabMachine(List<RenderedPage> pages) =>
        ChooseMachine(
            _minilab!.ReadyMachines(),
            !string.IsNullOrWhiteSpace(PreferredMinilabMachine)
                ? PreferredMinilabMachine
                : pages.Select(p => p.Product.MinilabMachineId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)));

    /// <summary>
    /// Refuse l'enveloppe entière si l'un de ses formats ne peut pas sortir du rouleau
    /// chargé. Le contrôle porte sur toutes les pages avant le premier envoi : sur une
    /// enveloppe de trois pages dont la deuxième est impossible, il ne faut pas non plus
    /// avoir tiré la première.
    /// </summary>
    private void EnsurePaperFits(List<RenderedPage> pages, char machine, int paperWidthMm)
    {
        // largeur inconnue (machine avare en informations) : on laisse passer plutôt que
        // de bloquer la boutique sur un défaut de remontée
        if (paperWidthMm <= 0) return;

        foreach (var page in pages)
        {
            if (FitsPaperWidth(page.WidthMm, page.HeightMm, paperWidthMm)) continue;

            var petitCote = Math.Min(page.WidthMm, page.HeightMm);

            // on annonce les produits du catalogue qui sortiraient VRAIMENT de ce rouleau,
            // et non les noms de format du minilab : c'est en produits que l'opérateur
            // parle au client, et la liste des formats sous-estime ce qui est possible
            // (elle ignore les tirages à bandes blanches)
            var possibles = _catalog.Enabled
                .Where(p => p.Output == ProductOutput.FujiMinilab)
                .Where(p => FitsPaperWidth(p.WidthMm, p.HeightMm, paperWidthMm))
                .Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // le plus étroit des rouleaux qui conviendrait, pour dire lequel charger
            var rouleau = De100Formats.All
                .Select(f => f.ShortSideMm)
                .Concat(De100Formats.All.Select(f => f.LengthMm))
                .Where(largeur => largeur >= petitCote)
                .DefaultIfEmpty(0)
                .Min();

            throw new InvalidOperationException(
                $"Le rouleau chargé dans la machine {machine} fait {paperWidthMm} mm de large : " +
                $"le {page.Product.Name} a besoin d'au moins {petitCote:0} mm. " +
                "Rien n'a été envoyé au minilab.\n\n" +
                (possibles.Count > 0
                    ? "Ce papier permet : " + string.Join(", ", possibles) + "."
                    : "Aucun produit du catalogue ne sort de ce rouleau.") +
                (rouleau > 0 ? $"\n\nPour ce produit, chargez le rouleau de {rouleau} mm." : ""));
        }
    }

    /// <summary>
    /// Règle du minilab, isolée pour être vérifiable : le tirage sort si le côté qui se
    /// pose en travers du rouleau y tient.
    ///
    /// Ce n'est PAS une égalité. DiLand tire un 13×18 sur du 152 : le côté de 127 se pose
    /// en travers, et les 25 mm qui restent sortent en bandes blanches (mode « produits
    /// flexibles », voir <see cref="MinilabPrintSize"/>). Vérifié dans son journal, qui
    /// montre 26 envois « 152x180 ». Seul un rouleau plus étroit que le petit côté rend
    /// le tirage réellement impossible.
    /// </summary>
    internal static bool FitsPaperWidth(double pageWidthMm, double pageHeightMm, int paperWidthMm) =>
        Math.Min(pageWidthMm, pageHeightMm) <= paperWidthMm + Tolerance;

    /// <summary>
    /// Cale la page sur ce que le minilab va réellement tirer : la photo se pose le long
    /// du rouleau, et les bandes blanches comblent la différence de largeur.
    ///
    /// Sans ce calage, la machine reçoit une image de 127 mm de large en annonçant un
    /// tirage de 152 : elle l'étire, et la photo sort déformée. C'est la contrepartie du
    /// mode « produits flexibles » de DiLand, qui compose lui aussi l'image à la taille
    /// du rouleau avant d'envoyer (<c>ResizeImage</c> dans son pilote).
    ///
    /// La rotation suit la même règle que lui : une photo paysage tirée sur un rouleau
    /// plus étroit que son grand côté part dans l'autre sens — elle ne rentrerait pas
    /// autrement.
    /// </summary>
    /// <returns>Le fichier à envoyer : l'original si rien n'était à corriger.</returns>
    private static string FitPageToRoll(RenderedPage page, double rollWidthMm, double lengthMm)
    {
        const int dpi = 300; // le DE100 travaille à 300 ppp, quel que soit le produit

        var cibleW = MmPx.ToPixels(rollWidthMm, dpi);
        var cibleH = MmPx.ToPixels(lengthMm, dpi);

        using var image = new MagickImage(page.Path);

        var pivote = image.Width > image.Height != cibleW > cibleH && image.Width != image.Height;
        if (pivote) image.Rotate(90);

        if (!pivote && image.Width == (uint)cibleW && image.Height == (uint)cibleH)
            return page.Path;

        image.BackgroundColor = MagickColors.White;
        image.Extent((uint)cibleW, (uint)cibleH, Gravity.Center, MagickColors.White);

        var sortie = Path.Combine(
            Path.GetDirectoryName(page.Path)!,
            Path.GetFileNameWithoutExtension(page.Path) + "-rouleau.png");

        // même règle que les rendus : la compression maximale de Magick.NET coûte huit fois
        // le prix de la compression rapide, sur des images que rien ne conserve
        MagickInit.Write(image, sortie);
        return sortie;
    }

    /// <summary>
    /// Les deux cotes que le minilab attend, en millimètres : la largeur du rouleau
    /// d'abord, la longueur de coupe ensuite.
    ///
    /// C'est la règle du pilote de DiLand, relevée par décompilation le 01/08/2026, et
    /// elle ne se devine pas : la première cote n'est PAS le grand côté du tirage, c'est
    /// TOUJOURS la largeur du rouleau chargé. La seconde est la longueur de coupe, une
    /// fois la page posée en travers du rouleau dans le sens qui gâche le moins.
    ///
    /// Vérifiée sur les 9 336 tirages du journal de DiLand : les douze cotes distinctes
    /// qu'il a envoyées depuis juillet sont toutes reproduites à l'identique, sur les
    /// trois rouleaux qui se sont succédé (152, 203, 210).
    ///
    /// L'erreur coûte cher et se voit mal : sur un rouleau de 152, un 10×15 donne
    /// « 152x102 » dans les deux raisonnements (son grand côté EST la largeur du
    /// rouleau), donc tout semble marcher. Un 15×20 donne « 152x203 » ici et « 203x152 »
    /// avec la règle du grand côté — la machine comprend alors qu'on lui demande un
    /// rouleau de 203, avertit que le papier n'est pas adapté, et gâche le tirage.
    /// C'est exactement ce qui est arrivé en boutique le 01/08/2026.
    /// </summary>
    /// <param name="paperWidthMm">Largeur du rouleau chargé ; 0 = inconnue.</param>
    internal static (double PaperWidthMm, double LengthMm) MinilabPrintSize(
        double pageWidthMm, double pageHeightMm, int paperWidthMm)
    {
        // largeur inconnue : on retombe sur le grand côté, faute de mieux
        if (paperWidthMm <= 0)
            return (Math.Max(pageWidthMm, pageHeightMm), Math.Min(pageWidthMm, pageHeightMm));

        // la page se pose dans un sens ou dans l'autre : chacun de ses deux côtés peut
        // aller en travers du rouleau. On ne garde que ceux qui y tiennent, et on prend
        // le plus large — c'est lui qui laisse le moins de blanc de chaque côté.
        var poses = new[]
        {
            (EnTravers: pageWidthMm, Longueur: pageHeightMm),
            (EnTravers: pageHeightMm, Longueur: pageWidthMm),
        };

        var possible = poses
            .Where(p => p.EnTravers <= paperWidthMm + Tolerance)
            .OrderByDescending(p => p.EnTravers)
            .ToList();

        // rien ne tient : le rouleau est plus étroit que le petit côté. On rend quand
        // même une cote cohérente, EnsurePaperFits refusera avant le moindre envoi.
        return possible.Count == 0
            ? (paperWidthMm, Math.Max(pageWidthMm, pageHeightMm))
            : (paperWidthMm, possible[0].Longueur);
    }

    /// <summary>Jeu admis sur les cotes en millimètres, arrondis de rendu compris.</summary>
    private const double Tolerance = 1.5;

    /// <summary>
    /// Nom de format attendu par le minilab.
    ///
    /// Ce n'est PAS le nom commercial : le DE100 attend « 152x203 », les millimètres,
    /// largeur de rouleau en premier (voir <see cref="MinilabPrintSize"/>). Envoyer
    /// « 15x20 » ferait rejeter la commande. Un produit peut malgré tout imposer son
    /// propre libellé.
    /// </summary>
    internal static string MinilabSizeName(Product product, double paperWidthMm, double lengthMm) =>
        string.IsNullOrWhiteSpace(product.MinilabPrintSizeName)
            ? $"{paperWidthMm:0}x{lengthMm:0}"
            : product.MinilabPrintSizeName!;

    /// <summary>
    /// Règle de choix, isolée pour être vérifiable : la machine demandée si elle est
    /// prête, sinon la première prête. Une machine demandée mais hors ligne ne doit pas
    /// bloquer le tirage — le DE100 de la boutique en compte deux, dont une souvent
    /// éteinte.
    /// </summary>
    internal static char ChooseMachine(IReadOnlyList<char> ready, string? requested)
    {
        if (ready.Count == 0)
            throw new InvalidOperationException(
                "Aucune machine du minilab n'est prête : rien n'a été envoyé. Vérifiez que le DE100 " +
                "est allumé et sorti de veille.");

        if (!string.IsNullOrWhiteSpace(requested) && ready.Contains(requested[0]))
            return requested[0];

        return ready[0];
    }

    /// <summary>
    /// Enveloppes rendues en fichiers et qui attendent d'être imprimées à la main depuis
    /// Photoshop. Rien n'a été soumis à Windows : sans action de l'opérateur, elles
    /// resteront dans cet état, ce qui est le comportement voulu.
    /// </summary>
    public List<(Order Order, Envelope Envelope, string Folder)> FindEnvelopesAwaitingManualPrint(
        IEnumerable<Order> orders)
    {
        var result = new List<(Order, Envelope, string)>();
        foreach (var order in orders)
            foreach (var envelope in order.Envelopes)
                if (ReadSpoolState(order, envelope)?.Status == SpoolState.AwaitingManualPrint)
                    result.Add((order, envelope, _store.GetRendersFolder(order)));
        return result;
    }

    /// <summary>
    /// Les fichiers rendus d'une enveloppe à tirer à la main, dans l'ordre où ils doivent
    /// passer sous les yeux de l'opérateur.
    ///
    /// Le nom porte le numéro d'enveloppe (<c>envNN-code-001.png</c>) : c'est ce qui
    /// permet de retrouver les tirages d'une enveloppe donnée dans un dossier de rendus
    /// qui les contient toutes. La règle vit ici, avec celle qui écrit ces noms — les deux
    /// écrans qui les lisent n'ont pas à la connaître, ni à la garder en phase.
    /// </summary>
    public IReadOnlyList<string> ManualPrintFiles(Order order, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(envelope);

        var dossier = _store.GetRendersFolder(order);
        if (!Directory.Exists(dossier)) return [];

        // l'extension n'est plus fixe : les agrandissements sortent en JPEG, les planches
        // en PNG (voir Extension). C'est le PRÉFIXE qui identifie l'enveloppe.
        return Directory
            .GetFiles(dossier, $"env{envelope.Number:00}-*")
            .Where(f => RendusConnus.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Extensions qu'un rendu peut porter. Le dossier des rendus contient aussi les images
    /// recalées pour le rouleau du minilab (<c>…-rouleau.png</c>), que le préfixe ne suffit
    /// pas à écarter — mais elles ne concernent pas le circuit manuel.
    /// </summary>
    private static readonly HashSet<string> RendusConnus =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg" };

    /// <summary>L'opérateur confirme que le tirage est bien sorti (rien à réimprimer).</summary>
    public void ConfirmPrinted(Order order, Envelope envelope)
    {
        WriteSpoolState(order, envelope, SpoolState.Printed);
        envelope.Status = EnvelopeStatus.Printed;
        _store.Save(order);
        _store.AppendEvent(order, "confirmed-by-operator", $"env={envelope.Number}");
    }

    /// <summary>
    /// Une page rendue et le produit qui l'a produite. Le produit est porté par la page
    /// (et non par l'enveloppe) : une enveloppe groupe un CANAL d'impression, qui peut
    /// contenir plusieurs produits — imprimante, DEVMODE et format diffèrent alors d'une
    /// page à l'autre.
    /// </summary>
    private sealed record RenderedPage(
        string Path, int Copies, double WidthMm, double HeightMm, Product Product, string? Finish);

    /// <summary>
    /// Extension du fichier rendu, donc son format.
    ///
    /// <b>JPEG pour les agrandissements, PNG pour tout le reste.</b> Ce n'est pas une
    /// préférence esthétique, c'est une mesure : l'encodeur PNG de Magick.NET met
    /// <b>12 secondes</b> à écrire un 40×50 à 300 ppp (27,9 Mpx) là où le JPEG en qualité 95
    /// met <b>0,7 seconde</b>, quel que soit le niveau de compression demandé. Sur deux
    /// tirages, c'est vingt-deux secondes rendues à l'opérateur, devant le client.
    ///
    /// Deux raisons de ne pas l'étendre au reste :
    ///
    /// 1. <b>Le minilab</b> reçoit le fichier tel quel, par le SDK Fuji. Ses formats ne se
    ///    vérifient pas depuis un poste de développement, et un tirage raté coûte du papier.
    ///    Ses rendus sont d'ailleurs petits (un 10×15 fait 2 Mpx) : il n'y a rien à y gagner.
    /// 2. <b>Les planches</b> portent des contours de découpe de deux dixièmes de millimètre
    ///    et de la date en petits caractères, autour desquels le JPEG laisse des franges.
    ///
    /// Les agrandissements, eux, ne sont relus que par nous (GDI+, boîte grand format), et
    /// leur source est déjà un JPEG d'appareil ou de scanner.
    /// </summary>
    private static string Extension(Product product) =>
        product.Output == ProductOutput.ManualFile && product.Sheet is null ? ".jpg" : ".png";

    private List<RenderedPage> RenderEnvelope(Order order, Envelope envelope,
        IProgress<PrintProgress>? progression = null, CancellationToken ct = default)
    {
        var photosDir = _store.GetPhotosFolder(order);
        var rendersDir = _store.GetRendersFolder(order);
        Directory.CreateDirectory(rendersDir);

        // c'est l'étape longue — un rendu ImageMagick par photo — donc celle dont
        // l'avancement intéresse vraiment l'opérateur
        var total = envelope.Lines.Sum(l => l.Items.Count);
        progression?.Report(new PrintProgress(PrintProgress.Rendu, 0, total));

        var pages = new List<RenderedPage>();
        foreach (var line in envelope.Lines)
        {
            var product = _catalog.Require(line.ProductCode);
            var targetW = MmPx.ToPixels(product.WidthMm, product.Dpi);
            var targetH = MmPx.ToPixels(product.HeightMm, product.Dpi);
            var borderPx = MmPx.ToPixels(product.BorderMm, product.Dpi);

            // format « personnalisé » : la ligne ne donne pas un tirage par photo mais des
            // planches où les photos sont casées côte à côte
            if (line.IsCustomSheet)
            {
                pages.AddRange(RenderCustomSheets(envelope, line, product, photosDir,
                    rendersDir, pages.Count, progression, total, ct));
                continue;
            }

            // Les photos d'une ligne sont rendues EN PARALLÈLE.
            //
            // Magick.NET est livré sans OpenMP sur ce poste (ResourceLimits.Thread vaut 1 et
            // refuse d'être changé) : un rendu occupe donc UN cœur sur les huit. Les faire à
            // la queue leu leu laissait la machine à 12 % pendant que le client attendait.
            //
            // Le parallélisme est bridé à quatre : chaque rendu d'agrandissement tient une
            // cinquantaine de mégapixels en mémoire, et ImageMagick bascule sur le disque
            // au-delà de son budget de 2 Go (voir MagickInit) — ce qui coûterait plus cher
            // que le gain.
            var rendus = new RenderedPage?[line.Items.Count];
            var dejaFaites = pages.Count;
            var faits = 0;

            var options = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4),
            };

            try
            {
                Parallel.For(0, line.Items.Count, options, i =>
                {
                    var item = line.Items[i];
                    var output = Path.Combine(rendersDir,
                        $"env{envelope.Number:00}-{line.ProductCode}-{i + 1:000}{Extension(product)}");

                    // canevas orienté comme la photo (une paysage part en 15×10, pas rognée en 10×15) ;
                    // les planches (identité) gardent leur orientation fixe
                    var sourcePath = Path.Combine(photosDir, item.FileName);
                    var (itemW, itemH) = (targetW, targetH);
                    var (widthMm, heightMm) = (product.WidthMm, product.HeightMm);
                    if (product.Sheet is null)
                    {
                        var (imgW, imgH) = ImagePipeline.GetOrientedSize(sourcePath, item.RotationQuarterTurns);

                        // le recadrage se compte sur l'image REDRESSÉE : c'est dans ce repère
                        // qu'il faut juger de son orientation, sinon un cadrage presque carré
                        // peut partir en paysage alors qu'il a été posé en portrait
                        var (toileW, toileH) = CropMath.TiltedCanvas(imgW, imgH, item.FineRotationDegrees);

                        (itemW, itemH) = CropMath.OrientCanvas(targetW, targetH,
                            (int)Math.Round(toileW), (int)Math.Round(toileH), item.Crop);
                        if (itemW != targetW)
                            (widthMm, heightMm) = (product.HeightMm, product.WidthMm);
                    }

                    var iccPath = IccPath(product, item.Finish);

                    var chrono = System.Diagnostics.Stopwatch.StartNew();
                    if (!File.Exists(output)) // rendu déterministe : réutilisable après un crash
                    {
                        if (product.Sheet is { } sheet)
                        {
                            // planche identité : la cellule est rendue en Fill au format cellule
                            //
                            // La taille de la case vient de l'ARTICLE quand il en porte une :
                            // c'est le document visé qui la fixe (26 × 32 mm pour un passeport
                            // espagnol), et le produit n'en connaît qu'une. Sans cela, toutes
                            // les planches sortaient au format français.
                            var cellW = MmPx.ToPixels(item.SheetCellWidthMm ?? sheet.CellWidthMm, product.Dpi);
                            var cellH = MmPx.ToPixels(item.SheetCellHeightMm ?? sheet.CellHeightMm, product.Dpi);
                            ImagePipeline.RenderIdSheetToFile(new RenderRequest(
                                    sourcePath, cellW, cellH,
                                    item.Crop, item.RotationQuarterTurns, item.FineRotationDegrees, FitMode.Fill, 0,
                                    item.Adjustments, iccPath),
                                item.SheetCopiesOverride ?? sheet.Copies, sheet.GapMm, sheet.CutMarks,
                                targetW, targetH, output, product.Dpi,
                                sheet.CutBorder,
                                // la date de la commande, pas l'heure du rendu : une planche
                                // rejouée après un incident doit porter la même mention
                                sheet.DateStamp ? order.CreatedAt.DateTime : null);
                        }
                        else
                        {
                            ImagePipeline.RenderToFile(new RenderRequest(
                                sourcePath,
                                itemW, itemH,
                                item.Crop,
                                item.RotationQuarterTurns,
                                item.FineRotationDegrees,
                                item.FitOverride ?? product.DefaultFit,
                                borderPx,
                                item.Adjustments,
                                iccPath,
                                item.CutBorder),
                                output, product.Dpi);
                        }
                    }

                    Log?.Invoke($"Rendu {Path.GetFileName(output)} ({itemW}×{itemH} px, {product.Dpi} ppp) " +
                                $"en {chrono.ElapsedMilliseconds} ms");

                    // la place dans la liste est celle de la photo, pas celle de la fin du
                    // rendu : l'ordre des tirages ne doit rien devoir au hasard des fils
                    rendus[i] = new RenderedPage(output, item.Quantity, widthMm, heightMm, product, item.Finish);

                    progression?.Report(new PrintProgress(
                        PrintProgress.Rendu, dejaFaites + Interlocked.Increment(ref faits), total));
                });
            }
            catch (AggregateException groupe) when (groupe.InnerExceptions.Count > 0)
            {
                // Parallel.For emballe ce que le corps a levé. L'opérateur doit lire
                // « fichier illisible : 003.jpg », et non « une ou plusieurs erreurs se
                // sont produites ».
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(groupe.InnerExceptions[0]).Throw();
            }

            pages.AddRange(rendus.Select(p => p!));
        }
        return pages;
    }

    /// <summary>
    /// Les planches d'une ligne « personnalisée ».
    ///
    /// La ligne porte le PAPIER dans <c>ProductCode</c> et la taille des cases dans
    /// <c>CustomCell*Mm</c> : on recalcule ici la capacité et l'orientation de la case, au
    /// lieu de les enregistrer dans la commande. C'est volontaire — le calcul est déterministe
    /// et vit à un seul endroit ; le mémoriser dans la commande créerait un second endroit
    /// susceptible de diverger du premier après un changement de catalogue.
    ///
    /// Chaque planche est une page à un exemplaire : les copies d'une même photo sont des
    /// cases de la planche, pas des tirages répétés.
    /// </summary>
    private IEnumerable<RenderedPage> RenderCustomSheets(Envelope envelope,
        OrderLine line, Product product, string photosDir, string rendersDir,
        int dejaFaites, IProgress<PrintProgress>? progression, int total, CancellationToken ct)
    {
        var celluleWmm = line.CustomCellWidthMm!.Value;
        var celluleHmm = line.CustomCellHeightMm!.Value;

        var papier = new PaperOption(product.Code, product.Name, product.WidthMm, product.HeightMm, product.Dpi);
        var (parPlanche, pivotee, plancheTournee) =
            CustomSheetLayout.CapacityDetaillee(papier, celluleWmm, celluleHmm);

        if (parPlanche < 1)
            throw new InvalidOperationException(
                $"Une photo de {celluleWmm:0.#} × {celluleHmm:0.#} mm ne tient pas sur un " +
                $"{product.Name}. Rien n'a été imprimé.");

        var plan = new CustomSheetPlan(papier, 0, parPlanche, pivotee, plancheTournee);
        var (celluleW, celluleH) = CustomSheetLayout.CellPixels(plan, celluleWmm, celluleHmm);

        var planches = CustomSheetLayout.Distribute(
            line.Items.Select(i => i.Quantity).ToList(), parPlanche);

        for (var n = 0; n < planches.Count; n++)
        {
            ct.ThrowIfCancellationRequested();

            var output = Path.Combine(rendersDir,
                $"env{envelope.Number:00}-{line.ProductCode}-perso-{n + 1:000}.png");

            if (!File.Exists(output)) // rendu déterministe : réutilisable après un crash
            {
                var cellules = planches[n]
                    .Select(place =>
                    {
                        var item = line.Items[place.PhotoIndex];
                        return new ImagePipeline.SheetCell(
                            new RenderRequest(
                                Path.Combine(photosDir, item.FileName),
                                celluleW, celluleH,
                                item.Crop, item.RotationQuarterTurns, item.FineRotationDegrees,
                                // la taille demandée est exacte : la photo remplit sa case,
                                // le cadrage a été posé pour ce rapport dans l'écran d'édition
                                item.FitOverride ?? FitMode.Fill,
                                0,
                                item.Adjustments,
                                IccPath(product, item.Finish)),
                            place.Copies);
                    })
                    .ToList();

                // La planche est rendue DANS LE SENS RETENU par le plan : c'est souvent
                // lui qui évite de coucher les cellules, donc de trahir le cadrage posé à
                // l'écran. Le pilote oriente ensuite la page, il sait le faire.
                ImagePipeline.RenderCustomSheetToFile(
                    cellules, SheetSpec.DefaultGapMm, cutMarks: true,
                    MmPx.ToPixels(plan.SheetWidthMm, product.Dpi),
                    MmPx.ToPixels(plan.SheetHeightMm, product.Dpi),
                    output, product.Dpi,
                    // le contour est le seul repère utile : on coupe une planche aux ciseaux
                    cutBorder: true);
            }

            yield return new RenderedPage(output, 1, plan.SheetWidthMm, plan.SheetHeightMm,
                product, line.Items[0].Finish);

            progression?.Report(new PrintProgress(
                PrintProgress.Rendu, Math.Min(total, dejaFaites + n + 1), total));
        }
    }

    /// <summary>Profil ICC applicable : celui de la finition (média) l'emporte sur celui du produit.</summary>
    private string? IccPath(Product product, string? finish)
    {
        var fichier = product.Finishes
                          .FirstOrDefault(f => string.Equals(f.Name, finish, StringComparison.OrdinalIgnoreCase))
                          ?.IccProfile
                      ?? product.IccProfile;

        return fichier is not null ? Path.Combine(_catalogDir, "icc", fichier) : null;
    }

    /// <param name="depart">
    /// Pages physiques déjà sorties lors d'une tentative précédente : on les saute. Zéro
    /// pour un premier envoi. Voir <see cref="PrintResumePoint"/>.
    /// </param>
    /// <param name="noterAvancement">
    /// Appelé après chaque page remise à Windows, avec le nombre total de pages remises
    /// depuis le début de l'enveloppe. C'est ce que la reprise relira après un bourrage :
    /// il doit être écrit AU FUR ET À MESURE, pas à la fin.
    /// </param>
    private void PrintPages(List<RenderedPage> pages, string? pdfPath, string documentName,
        IProgress<PrintProgress>? progression = null, CancellationToken ct = default,
        int depart = 0, Action<int>? noterAvancement = null)
    {
        var devModes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

        // La DS620 est la seule imprimante de la boutique branchée sur le spouleur : le
        // bandeau la montre sous la lettre D. Si un jour il y en a deux, c'est ici qu'il
        // faudra distinguer les destinations.
        const string tuileDnp = "D";

        var total = pages.Sum(p => p.Copies);
        var faites = depart;
        progression?.Report(new PrintProgress(PrintProgress.Impression, faites, total, tuileDnp));

        // rang de la page physique dans l'enveloppe, celui que compte le point de reprise
        var rang = 0;

        // aplatit (page, copies) en séquence de pages physiques
        foreach (var page in pages)
        {
            var product = page.Product;
            var key = $"{product.Code}|{page.Finish}";
            if (!devModes.TryGetValue(key, out var devMode))
            {
                // la finition choisie l'emporte sur le DEVMODE par défaut du produit
                var file = product.Finishes
                               .FirstOrDefault(f => string.Equals(f.Name, page.Finish, StringComparison.OrdinalIgnoreCase))
                               ?.DevmodeFile
                           ?? product.DevmodeFile;
                devMode = file is not null
                    ? File.ReadAllBytes(Path.Combine(_catalogDir, file))
                    : null;
                devModes[key] = devMode;
            }

            // le bitmap n'est ouvert que si au moins une de ses copies reste à tirer
            Bitmap? bitmap = null;
            try
            {
                for (var copy = 0; copy < page.Copies; copy++)
                {
                    if (rang++ < depart) continue;   // déjà sortie avant l'interruption

                    // entre deux pages : celles déjà remises à Windows lui appartiennent,
                    // mais on cesse d'en donner
                    ct.ThrowIfCancellationRequested();

                    bitmap ??= new Bitmap(page.Path);

                    BitmapPrinter.Print(
                        product.PrinterName, bitmap, page.WidthMm, page.HeightMm,
                        devMode, pdfPath, documentName);

                    faites++;
                    noterAvancement?.Invoke(faites);
                    progression?.Report(new PrintProgress(
                        PrintProgress.Impression, faites, total, tuileDnp));
                }
            }
            finally
            {
                bitmap?.Dispose();
            }
        }
    }

    private string SpoolStatePath(Order order, Envelope envelope) =>
        Path.Combine(_store.GetSpoolFolder(order), $"env{envelope.Number:00}.state");

    private string ResumePointPath(Order order, Envelope envelope) =>
        Path.Combine(_store.GetSpoolFolder(order), $"env{envelope.Number:00}.reprise");

    /// <summary>Où reprendre cette enveloppe, ou null si elle n'a jamais été interrompue.</summary>
    public PrintResumePoint? ReadResumePoint(Order order, Envelope envelope)
    {
        var json = AtomicFile.ReadAllTextOrNull(ResumePointPath(order, envelope));
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<PrintResumePoint>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteResumePoint(Order order, Envelope envelope, int pagesRemises, string raison)
    {
        Directory.CreateDirectory(_store.GetSpoolFolder(order));
        AtomicFile.WriteAllText(ResumePointPath(order, envelope),
            JsonSerializer.Serialize(new PrintResumePoint(pagesRemises, raison, DateTimeOffset.Now)));
    }

    /// <summary>
    /// Efface le point de reprise. À appeler dès que l'enveloppe est sortie en entier :
    /// un point resté sur le disque ferait sauter des pages à la réimpression suivante.
    /// </summary>
    private void ClearResumePoint(Order order, Envelope envelope)
    {
        try { File.Delete(ResumePointPath(order, envelope)); }
        catch (IOException) { /* rien à effacer, ou fichier tenu : sans conséquence */ }
    }

    private SpoolState? ReadSpoolState(Order order, Envelope envelope)
    {
        var json = AtomicFile.ReadAllTextOrNull(SpoolStatePath(order, envelope));
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<SpoolState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteSpoolState(Order order, Envelope envelope, string status)
    {
        Directory.CreateDirectory(_store.GetSpoolFolder(order));
        AtomicFile.WriteAllText(SpoolStatePath(order, envelope), JsonSerializer.Serialize(new SpoolState(status, DateTimeOffset.Now)));
    }
}

