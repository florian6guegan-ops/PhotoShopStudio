using System.Drawing;
using System.Text.Json;
using ImageMagick;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;
using Studio.Printing.Devices.Dnp;
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
/// Pages physiques déjà SORTIES de la machine. La reprise saute celles-là : sans ce
/// compte, un bourrage à la vingtième photo d'une planche de trente en refaisait trente.
///
/// Le nom dit « remises » pour une raison d'histoire — le champ est sérialisé dans les
/// fichiers <c>envNN.reprise</c> déjà sur les disques —, mais ce qu'il porte est bien ce
/// que la MACHINE a sorti, pas ce que Windows a pris. La différence est tout le sujet de
/// <see cref="CadenceSpouleur"/> : sur six cents photos, une panne d'encre à la troisième
/// laissait le spouleur avec cinq cent quatre-vingt-dix-sept pages et le point de reprise
/// à 600.
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
/// <param name="Verdicts">
/// Nombre de réponses que la machine rendra sur cet envoi, quand il diffère du nombre de
/// FEUILLES.
///
/// <b>Les deux comptes ne coïncident pas sur le minilab</b> : une photo demandée en deux
/// exemplaires part en UN tirage de deux copies (<c>PrintNum</c>), et le DE100 rendra donc
/// un seul verdict pour deux feuilles. <see cref="Total"/> compte ce que l'opérateur
/// attend — les feuilles — et celui-ci ce qu'il faut avoir reçu pour déclarer l'enveloppe
/// finie. Les confondre faisait annoncer « 1 / 1 » sur une commande qui sortait deux
/// photos. Zéro quand la question ne se pose pas.
/// </param>
/// <param name="Handle">
/// Handle de la commande minilab, une fois qu'elle est partie. C'est par lui que le suivi
/// demande à la MACHINE où elle en est, tirage par tirage — le seul compte qui ne mélange
/// pas cette commande avec ce que le minilab sort par ailleurs.
/// </param>
public sealed record PrintProgress(
    string Etape, int Faits, int Total, string? Machine = null, int Verdicts = 0,
    string? Handle = null)
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
/// L'envoi est parti sans que la machine confirme l'avoir pris — <b>et l'on ne sait pas
/// si elle l'a pris</b>.
///
/// C'est le cas le plus désagréable qui soit, et il mérite son propre type parce que les
/// deux réflexes disponibles sont mauvais :
///
/// <list type="bullet">
/// <item>le traiter en échec le renvoie par un autre chemin — c'est ce qui a sorti la
/// planche 003 de la commande 12-012 en double le 12/08/2026 ;</item>
/// <item>le traiter en attente le fait reprendre TOUT SEUL par
/// <see cref="PendingPrintQueue"/>, ce qui revient au même en différé.</item>
/// </list>
///
/// L'enveloppe reste donc à <see cref="SpoolState.Spooled"/> — « partie, sortie non
/// confirmée » — et c'est l'opérateur qui tranche, en regardant ce qui est tombé dans le
/// bac. La règle est celle qu'observe déjà le minilab : rien n'est jamais renvoyé
/// automatiquement quand la sortie est douteuse.
/// </summary>
public sealed class PrintUnconfirmedException(string message) : Exception(message);

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

    /// <summary>
    /// Avertissement destiné à l'OPÉRATEUR, et non au journal : ce qui doit lui sauter aux
    /// yeux pendant qu'il travaille, sans pour autant arrêter le tirage.
    ///
    /// Distinct de <see cref="Log"/>, qui n'écrit que dans un fichier — et un avertissement
    /// qui ne va que dans un fichier n'avertit personne. L'application le pose dans le
    /// bandeau des impressions, où il attend d'être acquitté au lieu de barrer l'écran
    /// d'une boîte modale.
    ///
    /// Il sert aujourd'hui à la finition : le client a demandé du lustré, la machine a du
    /// brillant, le tirage part quand même et l'opérateur doit pouvoir l'arrêter.
    /// </summary>
    public Action<string>? Avertir { get; set; }

    /// <summary>
    /// La marque portée sur la bande basse des planches identité — mention, logo, code QR.
    ///
    /// Elle est posée par l'application au démarrage, et non lue ici : l'atelier
    /// d'impression ne connaît pas le dossier de configuration, et une lecture de fichier
    /// par planche coûterait un accès disque au milieu d'un rendu.
    ///
    /// Null = date seule dans la marge, la planche d'avant.
    /// </summary>
    public MarqueSettings? Marque { get; set; }

    /// <summary>
    /// Ce qu'il faut ajouter au rendu pour que le papier ressemble à l'écran, machine par
    /// machine. Posée par l'application au démarrage, comme <see cref="Marque"/> et pour la
    /// même raison : l'atelier d'impression ne lit pas le dossier de configuration.
    ///
    /// Null = aucune compensation, la machine reçoit ce que l'écran montrait. Voir
    /// <see cref="CorrectionsMachines"/> — et surtout : elle ne touche JAMAIS l'aperçu.
    /// </summary>
    public CorrectionsMachines? Corrections { get; set; }

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
    /// Vrai si cet orchestrateur peut atteindre le minilab.
    ///
    /// <b>Sans lui, aucun tirage Fuji ne part</b> — et la panne est déroutante parce que le
    /// relais, lui, tourne : c'est l'orchestrateur qui ne sait plus où le joindre. Vu le
    /// 10/08/2026, commande 10-024 : reconstruire l'orchestrateur après un changement de
    /// catalogue l'avait laissé sans minilab jusqu'au redémarrage de l'application.
    /// </summary>
    public bool MinilabDisponible => _minilab is not null;

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
            // DÉJÀ CLOSE : on ne la referme pas une seconde fois.
            //
            // L'envoi des photos clôt l'enveloppe ; si l'opérateur clique ensuite sur
            // « Imprimer » — ce qui est naturel, la commande est là, sous ses yeux — on
            // repassait ici et le journal de la commande recevait un SECOND événement
            // « printed », identique au premier. Rien n'était réexpédié, mais la commande
            // 08-002 du 08/08/2026 porte deux clôtures à vingt-trois secondes d'écart, et
            // c'est ce genre de doublon qui rend un historique impossible à relire.
            //
            // L'état sur DISQUE fait foi, et non celui de l'objet en mémoire : la commande
            // peut avoir été rouverte depuis « Commandes du jour » après un redémarrage.
            if (ReadSpoolState(order, envelope)?.Status == SpoolState.Printed)
            {
                Log?.Invoke($"Enveloppe {order.DisplayNumber}/{envelope.Number} : déjà envoyée " +
                            "par courriel, rien de plus à faire.");
                return;
            }

            WriteSpoolState(order, envelope, SpoolState.Printed);
            envelope.Status = EnvelopeStatus.Printed;

            // Gravé sur la commande, et pas seulement dans l'objet en mémoire : sans ces
            // deux lignes, l'enveloppe close ne l'était que le temps de la session. Le
            // 07/08/2026, la commande 07-015 s'arrêtait sur « photos-copied » dans son
            // journal et restait « Submitted » dans order.json, pour une prestation
            // pourtant rendue.
            MettreAJourStatutCommande(order);
            _store.Save(order);
            _store.AppendEvent(order, "printed", $"env={envelope.Number}, courriel");

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

        // On REFAIT la dernière photo sortie. Quand une machine s'arrête faute d'encre ou
        // de ruban, celle qui était en cours sort pâle ou à moitié, et rien ne permet de le
        // savoir depuis le logiciel. Une feuille refaite coûte quelques centimes ; une
        // photo ratée au milieu d'un paquet de six cents coûte le paquet. Demandé par
        // l'exploitant le 04/08/2026.
        var deja = CadenceSpouleur.ReprendreA(reprise?.PagesRemises ?? 0);
        if (deja > 0 || reprise is not null)
            _store.AppendEvent(order, "spool-resume",
                $"env={envelope.Number}, sorties={reprise?.PagesRemises ?? 0}, " +
                $"reprise à la page {deja + 1}");

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
        catch (PrintUnconfirmedException ex)
        {
            // ON NE RANGE PAS EN ATTENTE. L'enveloppe reste « Spooled », c'est-à-dire
            // partie sans confirmation : la file de reprise ne la touchera pas, et la
            // réimprimer réclamera un geste explicite de l'opérateur — les deux garanties
            // qui manquaient le 12/08/2026.
            //
            // Le point de reprise est effacé pour la même raison : il ne décrit plus rien
            // de sûr, et le laisser traîner ferait repartir l'enveloppe au mauvais rang le
            // jour où quelqu'un la remet en attente.
            var faites = ReadResumePoint(order, envelope)?.PagesRemises ?? deja;
            ClearResumePoint(order, envelope);
            _store.AppendEvent(order, "print-unconfirmed",
                $"env={envelope.Number}, pages sorties={faites}, raison={ex.Message}");
            Log?.Invoke($"Commande {order.DisplayNumber}/{envelope.Number} : sortie NON CONFIRMÉE " +
                        $"après {faites} photo(s) — {ex.Message}");

            throw new PrintUnconfirmedException(
                $"Commande {order.DisplayNumber} interrompue : la DNP n'a pas confirmé le tirage " +
                $"n° {faites + 1}.\n\n" +
                "IL EST PEUT-ÊTRE SORTI QUAND MÊME — regardez ce qui est tombé dans le bac. " +
                "Rien n'a été réimprimé automatiquement, et rien ne repartira tout seul.\n\n" +
                $"{faites} photo(s) sont sorties à coup sûr. S'il en manque, reprenez la commande " +
                "depuis « Commandes du jour ».");
        }
        catch (Exception ex)
        {
            // Le spouleur a refusé en cours de route — bourrage, machine éteinte, câble.
            // Le point de reprise dit où on en était : l'enveloppe repartira de là plutôt
            // que de refaire les pages déjà sorties.
            var faites = ReadResumePoint(order, envelope)?.PagesRemises ?? deja;
            MettreEnAttente(order, envelope, faites, ex.Message);

            var reprendAt = CadenceSpouleur.ReprendreA(faites) + 1;
            throw new PrinterNotReadyException(
                $"Commande {order.DisplayNumber} interrompue — {ex.Message}.\n\n" +
                $"{faites} photo(s) sont sorties. Elle reprendra toute seule à la photo " +
                $"n° {reprendAt} dès que la machine sera prête : changez ce qu'il faut, " +
                "il n'y a rien d'autre à faire.\n\n" +
                (faites > 0
                    ? $"La photo n° {faites} sera refaite — c'est celle qui était en cours " +
                      "quand la machine s'est arrêtée, et elle a pu sortir mal imprimée."
                    : "Aucune photo n'est sortie : rien ne sera perdu."));
        }

        ClearResumePoint(order, envelope);
        WriteSpoolState(order, envelope, SpoolState.Printed);
        envelope.Status = EnvelopeStatus.Printed;
        MettreAJourStatutCommande(order);
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
        var (machine, paperWidthMm, repli) = ChoisirMachineEtRouleau(order, envelope, pages);

        // vérifié AVANT le moindre envoi : demander un format que le rouleau chargé ne
        // permet pas ne donne pas un tirage plus petit, mais un tirage faux — la machine
        // avertit que le papier n'est pas adapté et gâche la feuille. Constaté en
        // boutique le 01/08/2026.
        EnsurePaperFits(pages, machine, paperWidthMm, repli);

        // La finition doit être celle du papier réellement chargé : annoncer « brillant »
        // sur du lustré fausse le rendu. Lue ICI, avant le moindre envoi, pour que
        // l'opérateur soit prévenu AVANT que le papier ne défile — c'est le seul moment où
        // il peut encore arrêter le tirage.
        var chargee = _minilab!.LoadedSurface(machine);
        SignalerFinitionDifferente(pages, machine, chargee);

        // Ce qu'on ANNONCE à la machine. Elle attend une valeur, toujours : une machine
        // muette retombe donc sur le brillant, comme avant que l'inconnu se distingue.
        var surface = chargee ?? De100Surface.Glossy;

        WriteSpoolState(order, envelope, SpoolState.Spooled);
        envelope.Status = EnvelopeStatus.Spooled;
        _store.Save(order);
        _store.AppendEvent(order, "minilab-submit-start",
            $"env={envelope.Number}, machine={machine}, tirages={pages.Sum(p => p.Copies)}");

        // Retenue pour pouvoir la clore quand la machine aura rendu ses verdicts : c'est le
        // seul circuit où la sortie est connue APRÈS le retour de l'envoi. Voir
        // ConfirmerSortieMinilab.
        if (!_enveloppesAuMinilab.TryGetValue(order.DisplayNumber, out var envoyees))
            _enveloppesAuMinilab[order.DisplayNumber] = envoyees = [];
        envoyees.Add(envelope.Number);

        var cible = machine.ToString();
        var jobs = new List<De100PrintJob>(pages.Count);

        // Ce que l'opérateur va voir tomber dans le bac : des FEUILLES, pas des photos
        // distinctes. Une photo en double exemplaire en fait deux, et le bandeau annonçait
        // « 1 / 1 » pendant que la machine en sortait deux.
        var feuilles = pages.Sum(p => p.Copies);
        var feuillesEnvoyees = 0;

        // le format de CETTE enveloppe : le bandeau estimera ce qui reste dessus plutôt
        // que sur le premier format du rouleau, et la DURÉE d'après sa longueur
        var formatDeLEnveloppe = pages
            .Select(p => De100Formats.All.FirstOrDefault(f =>
                Math.Abs(f.ShortSideMm - Math.Min(p.WidthMm, p.HeightMm)) <= Tolerance &&
                Math.Abs(f.LengthMm - Math.Max(p.WidthMm, p.HeightMm)) <= Tolerance))
            .FirstOrDefault(f => f is not null);

        if (formatDeLEnveloppe is not null)
        {
            DernierFormatMinilab = formatDeLEnveloppe.Name;
            DerniereLongueurMinilabMm =
                De100Formats.ConsumedLengthMm(formatDeLEnveloppe, paperWidthMm);
        }
        else if (pages.Count > 0)
        {
            // format hors catalogue — une taille personnalisée : on n'a pas de nom, mais la
            // longueur suffit à estimer la durée
            DerniereLongueurMinilabMm = (int)Math.Max(pages[0].WidthMm, pages[0].HeightMm);
        }

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
            var image = FitPageToRoll(page, largeur, longueur, machine);

            jobs.Add(new De100PrintJob(
                JobId: MinilabJobId(order.DisplayNumber, envelope.Number, i + 1),
                ImagePath: image,
                WidthMm: largeur,
                HeightMm: longueur,
                PrintSizeName: MinilabSizeName(page.Product, largeur, longueur),
                Surface: surface,
                Copies: page.Copies));

            feuillesEnvoyees += page.Copies;

            // Faits et Total en FEUILLES ; le nombre de verdicts attendus voyage à part,
            // car le minilab répond une fois par tirage, exemplaires compris.
            progression?.Report(new PrintProgress(
                PrintProgress.Envoi, feuillesEnvoyees, feuilles, cible, pages.Count));
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

        // Le handle remonte au suivi : c'est par lui qu'il ira demander à la MACHINE
        // combien de tirages de CETTE commande sont sortis. Rapporté après l'envoi, une
        // fois qu'il existe, et sans toucher aux comptes déjà annoncés.
        progression?.Report(new PrintProgress(
            PrintProgress.Envoi, feuilles, feuilles, cible, pages.Count, handle));
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
    /// Machine du minilab choisie par l'opérateur pour la session en cours. Elle prime sur
    /// celle éventuellement fixée par le produit ; null = choix automatique.
    ///
    /// <b>Null est l'état normal.</b> Une machine posée ici n'est plus jamais discutée : le
    /// rouleau ne décide plus rien. La barre de la grille la remettait à la première
    /// machine de la liste à chaque ouverture, ce qui imposait la machine A sans que
    /// personne ne l'ait demandé — voir <see cref="ChoisirMachineEtRouleau"/>.
    /// </summary>
    public string? PreferredMinilabMachine { get; set; }

    /// <summary>
    /// Nom du dernier format envoyé au minilab, pour que le bandeau estime ce qui reste
    /// SUR CE FORMAT-LÀ.
    ///
    /// « ~576 × 10x15 » annoncé à quelqu'un qui lance des A4 ne lui apprend rien. Null tant
    /// qu'aucun tirage n'est parti : on retombe alors sur le premier format du rouleau.
    /// </summary>
    public string? DernierFormatMinilab { get; private set; }

    /// <summary>
    /// Longueur de papier qu'un tirage du dernier format consomme. C'est elle qui décide de
    /// la cadence — un A4 défile deux fois plus longtemps qu'un 10×15 — et donc de
    /// l'estimation de durée.
    /// </summary>
    public int DerniereLongueurMinilabMm { get; private set; }

    /// <summary>
    /// Machine demandée : par l'opérateur pour la session, sinon par le produit. Null =
    /// personne n'a tranché, le choix revient au ROULEAU (voir
    /// <see cref="ChoisirMachineEtRouleau"/>).
    /// </summary>
    private string? MachineDemandee(List<RenderedPage> pages) =>
        !string.IsNullOrWhiteSpace(PreferredMinilabMachine)
            ? PreferredMinilabMachine
            : pages.Select(p => p.Product.MinilabMachineId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

    /// <summary>
    /// La machine qui recevra l'enveloppe, et la largeur du rouleau qu'elle porte.
    ///
    /// Le DE100 de la boutique compte DEUX machines, et elles n'ont jamais le même
    /// rouleau : c'est tout l'intérêt d'en avoir deux. Prendre la première prête revenait
    /// à ignorer la seconde — un 21×29,7 était refusé « le rouleau chargé dans la machine
    /// A fait 152 mm » alors que le rouleau de 210 était monté à côté. Constaté le
    /// 04/08/2026 sur les commandes 04-010 et 04-014.
    ///
    /// La machine par défaut passe en tête : à format égal, rien ne change. Les autres ne
    /// sont examinées que si son rouleau ne porte pas le format.
    ///
    /// Un choix IMPOSÉ — barre de la grille, ou produit — ne se discute pas : l'opérateur
    /// seul sait quel rouleau il vient de monter, et un format qui n'y tient pas se dira
    /// dans <see cref="EnsurePaperFits"/>, avec le rouleau à charger — et avec la machine
    /// d'à côté qui le porterait, s'il y en a une.
    /// </summary>
    private (char Machine, int PaperWidthMm, MachineDeRepli? Repli) ChoisirMachineEtRouleau(
        Order order, Envelope envelope, List<RenderedPage> pages)
    {
        var pretes = _minilab!.ReadyMachines();
        var demandee = MachineDemandee(pages);
        var defaut = ChooseMachine(pretes, demandee);

        if (!string.IsNullOrWhiteSpace(demandee) && pretes.Contains(demandee[0]))
        {
            var largeurImposee = LargeurDuRouleau(order, envelope, defaut);

            // le format ne tient pas sur la machine demandée : avant de refuser, on regarde
            // si une AUTRE machine le porterait — c'est l'information qui manquait à
            // l'opérateur, qui lisait « chargez le rouleau de 210 mm » avec ce rouleau déjà
            // monté dans la machine voisine
            var repli = largeurImposee > 0 && !PagesTiennent(pages, largeurImposee)
                ? ChercherUneAutreMachine(pretes, defaut, pages)
                : null;

            return (defaut, largeurImposee, repli);
        }

        // le silence d'une machine n'est retenu que si AUCUNE ne répond : sans cela, une
        // machine endormie à côté d'une machine prête mettrait la commande en attente
        Exception? muette = null;

        // la finition que le client a demandée à la borne ; null au comptoir, et le choix
        // se fait alors sur le seul format, comme avant
        var voulue = SurfaceDemandee(pages);

        var choix = ChoisirSelonLeRouleau(
            pretes, defaut,
            machine =>
            {
                try
                {
                    return _minilab.LoadedPaperWidthMm(machine);
                }
                catch (Exception ex)
                {
                    muette ??= ex;
                    throw;
                }
            },
            largeur => PagesTiennent(pages, largeur),
            voulue is null
                ? null
                : machine =>
                {
                    try
                    {
                        return _minilab.LoadedSurface(machine) == voulue;
                    }
                    catch (Exception ex)
                    {
                        muette ??= ex;
                        throw;
                    }
                });

        if (choix is not null)
        {
            if (choix.Value.Machine != defaut)
                Log?.Invoke($"Minilab : machine {choix.Value.Machine} retenue " +
                            $"({choix.Value.PaperWidthMm} mm{(voulue is null ? "" : $", {voulue}")}) — " +
                            $"le rouleau de {defaut} ne porte pas " +
                            (voulue is null ? "ce format." : "ce format ou cette finition."));
            return (choix.Value.Machine, choix.Value.PaperWidthMm, null);
        }

        // aucune machine n'a répondu
        MettreEnAttente(order, envelope, 0,
            $"le minilab {defaut} ne répond pas ({muette?.Message})");

        throw new PrinterNotReadyException(
            $"Commande {order.DisplayNumber} mise en attente : le minilab {defaut} ne " +
            "répond pas — il est probablement en veille.\n\n" +
            "Réveillez-le : la commande partira toute seule, sans rien réimprimer.");
    }

    /// <summary>
    /// Une machine prête, autre que celle visée, dont le rouleau porterait le format.
    /// </summary>
    /// <param name="Machine">Identifiant machine.</param>
    /// <param name="PaperWidthMm">Largeur de son rouleau.</param>
    private sealed record MachineDeRepli(char Machine, int PaperWidthMm);

    /// <summary>Toutes les pages tiennent-elles sur un rouleau de cette largeur ?</summary>
    private static bool PagesTiennent(List<RenderedPage> pages, int largeurMm) =>
        pages.All(p => FitsPaperWidth(p.WidthMm, p.HeightMm, largeurMm));

    /// <summary>
    /// Une machine prête, autre que <paramref name="visee"/>, dont le rouleau porterait le
    /// format — ou null.
    ///
    /// Elle ne sert PAS à détourner le tirage : une machine imposée reste imposée. Elle ne
    /// sert qu'à le dire dans le refus, parce que « chargez le rouleau de 210 mm » devant
    /// une machine voisine qui le porte déjà envoie l'opérateur démonter un rouleau pour
    /// rien.
    ///
    /// Une machine muette est sautée sans bruit : on est déjà dans un chemin d'échec, et
    /// une seconde panne n'a rien à y ajouter.
    /// </summary>
    private MachineDeRepli? ChercherUneAutreMachine(
        IReadOnlyList<char> pretes, char visee, List<RenderedPage> pages)
    {
        var (machine, largeur) = MachineQuiPorteLeFormat(
            pretes, visee,
            m => _minilab!.LoadedPaperWidthMm(m),
            l => PagesTiennent(pages, l));

        return machine == Aucune ? null : new MachineDeRepli(machine, largeur);
    }

    /// <summary>Réponse de <see cref="MachineQuiPorteLeFormat"/> quand aucune ne convient.</summary>
    internal const char Aucune = '\0';

    /// <summary>
    /// La recherche proprement dite, isolée pour être vérifiable sans minilab.
    ///
    /// Une machine muette est sautée sans bruit : on est déjà dans un chemin d'échec, et
    /// une seconde panne n'a rien à y ajouter. Un rouleau de largeur INCONNUE n'est jamais
    /// proposé — annoncer « la machine B porte du 0 mm » serait pire que se taire.
    /// </summary>
    /// <param name="pretes">Machines prêtes.</param>
    /// <param name="visee">Machine imposée, qu'on ne se propose évidemment pas à elle-même.</param>
    /// <param name="largeurDuRouleau">Lecture du rouleau ; lève si la machine ne répond pas.</param>
    /// <param name="porteLeFormat">Vrai si un rouleau de cette largeur porte toutes les pages.</param>
    /// <returns>La machine et son rouleau ; <see cref="Aucune"/> si aucune ne convient.</returns>
    internal static (char Machine, int PaperWidthMm) MachineQuiPorteLeFormat(
        IReadOnlyList<char> pretes, char visee,
        Func<char, int> largeurDuRouleau, Func<int, bool> porteLeFormat)
    {
        ArgumentNullException.ThrowIfNull(pretes);
        ArgumentNullException.ThrowIfNull(largeurDuRouleau);
        ArgumentNullException.ThrowIfNull(porteLeFormat);

        foreach (var machine in pretes.Where(m => m != visee))
        {
            try
            {
                var largeur = largeurDuRouleau(machine);
                if (largeur > 0 && porteLeFormat(largeur)) return (machine, largeur);
            }
            catch
            {
                // machine endormie ou injoignable : rien à proposer de son côté
            }
        }

        return (Aucune, 0);
    }

    /// <summary>
    /// Le choix proprement dit, isolé pour être vérifiable sans minilab.
    ///
    /// Trois règles, dans cet ordre :
    ///
    /// 1. la machine par défaut est examinée EN PREMIER — à format égal, rien ne change ;
    /// 2. une machine qui ne répond pas est sautée, jamais bloquante ;
    /// 3. si aucun rouleau ne porte le format, on rend le PREMIER qui a répondu — c'est
    ///    lui qu'<see cref="EnsurePaperFits"/> nommera dans son refus, avec le rouleau à
    ///    charger. Rendre « rien » ferait perdre cette explication.
    ///
    /// <b>La finition passe AVANT le format</b> quand le client en a demandé une : elle se
    /// cherche en premier passage, sur les machines qui portent aussi le format. Faute de
    /// quoi une commande lustrée partait sur la première machine dont le rouleau avait la
    /// bonne largeur — la A, en brillant — et le client repartait avec la mauvaise
    /// surface sans que rien ne l'ait signalé.
    /// </summary>
    /// <param name="pretes">Machines prêtes, dans l'ordre où le relais les rend.</param>
    /// <param name="defaut">Machine que <see cref="ChooseMachine"/> aurait retenue seule.</param>
    /// <param name="largeurDuRouleau">Lecture du rouleau ; lève si la machine ne répond pas.</param>
    /// <param name="porteLeFormat">Vrai si un rouleau de cette largeur porte TOUTES les pages.</param>
    /// <param name="porteLaFinition">
    /// Vrai si cette machine a chargé la surface demandée ; lève si elle ne répond pas.
    /// <c>null</c> = aucune finition demandée, et le choix se fait comme avant, sur le seul
    /// format. C'est le cas de tout ce qui ne vient pas d'une borne.
    ///
    /// Quand une finition est demandée mais qu'aucune machine ne la porte, on retombe sur
    /// le choix par le format : la machine rendue n'est pas la bonne, mais elle permet à
    /// <see cref="EnsureSurfaceMatches"/> de refuser en nommant ce qui est chargé.
    /// </param>
    /// <returns>La machine retenue et son rouleau ; null si aucune machine n'a répondu.</returns>
    internal static (char Machine, int PaperWidthMm)? ChoisirSelonLeRouleau(
        IReadOnlyList<char> pretes, char defaut,
        Func<char, int> largeurDuRouleau, Func<int, bool> porteLeFormat,
        Func<char, bool>? porteLaFinition = null)
    {
        ArgumentNullException.ThrowIfNull(pretes);
        ArgumentNullException.ThrowIfNull(largeurDuRouleau);
        ArgumentNullException.ThrowIfNull(porteLeFormat);

        var ordre = pretes.OrderByDescending(m => m == defaut).ToList();

        // Premier passage : la machine doit porter le format ET la finition demandée.
        if (porteLaFinition is not null)
        {
            foreach (var machine in ordre)
            {
                int largeur;
                bool finition;
                try
                {
                    largeur = largeurDuRouleau(machine);
                    finition = porteLaFinition(machine);
                }
                catch
                {
                    continue;
                }

                // largeur inconnue : on ne bloque rien, comme EnsurePaperFits
                if (finition && (largeur <= 0 || porteLeFormat(largeur))) return (machine, largeur);
            }
        }

        (char Machine, int PaperWidthMm)? repli = null;

        foreach (var machine in ordre)
        {
            int largeur;
            try
            {
                largeur = largeurDuRouleau(machine);
            }
            catch
            {
                continue;
            }

            // largeur inconnue : on ne bloque rien, comme EnsurePaperFits
            if (largeur <= 0 || porteLeFormat(largeur)) return (machine, largeur);

            repli ??= (machine, largeur);
        }

        return repli;
    }

    /// <summary>
    /// Largeur du rouleau chargé, ou mise en attente de la commande.
    ///
    /// Interroger le rouleau est la PREMIÈRE chose qui touche la machine, donc la première
    /// qui peut rester suspendue quand elle dort. Une commande de 41 photos est restée
    /// douze minutes ici sans un mot le 03/08/2026 : le rendu était fait, et rien ne disait
    /// à l'opérateur ce qu'on attendait.
    ///
    /// Elle part donc EN ATTENTE, comme pour une imprimante pas prête : la file la
    /// reprendra dès que le minilab répondra, et l'opérateur sait pourquoi.
    /// </summary>
    private int LargeurDuRouleau(Order order, Envelope envelope, char machine)
    {
        try
        {
            return _minilab!.LoadedPaperWidthMm(machine);
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
    }

    /// <summary>
    /// Refuse l'enveloppe entière si l'un de ses formats ne peut pas sortir du rouleau
    /// chargé. Le contrôle porte sur toutes les pages avant le premier envoi : sur une
    /// enveloppe de trois pages dont la deuxième est impossible, il ne faut pas non plus
    /// avoir tiré la première.
    /// </summary>
    /// <param name="repli">
    /// Machine voisine dont le rouleau porterait le format, quand la machine visée a été
    /// IMPOSÉE. Nommée dans le refus : sans elle, le message envoie changer un rouleau qui
    /// tourne déjà dans la machine d'à côté.
    /// </param>
    private void EnsurePaperFits(List<RenderedPage> pages, char machine, int paperWidthMm,
        MachineDeRepli? repli = null)
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

            // une machine voisine porte le format : c'est elle qu'il faut désigner, et non
            // un rouleau à changer
            var conseil = repli is { } autre
                ? $"\n\nLa machine {autre.Machine} porte du {autre.PaperWidthMm} mm : choisissez-la " +
                  "dans la liste « Minilab », ou repassez-la sur « Automatique »."
                : rouleau > 0
                    ? $"\n\nPour ce produit, chargez le rouleau de {rouleau} mm."
                    : "";

            throw new InvalidOperationException(
                $"Le rouleau chargé dans la machine {machine} fait {paperWidthMm} mm de large : " +
                $"le {page.Product.Name} a besoin d'au moins {petitCote:0} mm. " +
                "Rien n'a été envoyé au minilab.\n\n" +
                (possibles.Count > 0
                    ? "Ce papier permet : " + string.Join(", ", possibles) + "."
                    : "Aucun produit du catalogue ne sort de ce rouleau.") +
                conseil);
        }
    }

    /// <summary>
    /// Prévient — <b>sans rien empêcher</b> — quand le client a demandé une finition que la
    /// machine retenue n'a pas chargée.
    ///
    /// <b>Pourquoi prévenir plutôt que refuser.</b> Le tirage sort quand même : bloquer une
    /// commande arrête la boutique, et l'opérateur est le seul à savoir si ce client-là
    /// acceptera du brillant ou s'il faut changer le rouleau. Ce qu'on lui doit, c'est de
    /// ne pas le laisser l'apprendre par le client : une finition fausse ne se voit pas au
    /// sortir de la machine — le tirage est propre et au bon format — et sans ce message
    /// personne ne s'en apercevrait avant l'ouverture de la pochette.
    ///
    /// Le message part par <see cref="Avertir"/>, qui le pose dans le bandeau des
    /// impressions, et non par <see cref="Log"/> : un avertissement qui ne va que dans un
    /// fichier journal n'avertit personne.
    ///
    /// Avec les rouleaux de la boutique — brillant sur une machine, lustré sur l'autre — il
    /// ne devrait tomber qu'en panne réelle : machine endormie, rouleau vide, ou rouleaux
    /// intervertis.
    ///
    /// Rien à dire quand aucune finition n'est demandée : c'est tout le comptoir, et le
    /// rouleau chargé fait foi comme il l'a toujours fait.
    /// </summary>
    private void SignalerFinitionDifferente(List<RenderedPage> pages, char machine, De100Surface? chargee)
    {
        var voulue = SurfaceDemandee(pages);

        // Surface inconnue : rien à signaler plutôt qu'une alerte fondée sur du vide,
        // exactement comme EnsurePaperFits laisse passer une largeur nulle. Une machine
        // dont le pont ne décrit pas le média imprime comme avant, sans bruit.
        if (voulue is null || chargee is null || voulue == chargee) return;

        // La machine d'à côté porte peut-être le rouleau : c'est l'information qui évite
        // d'aller en démonter un pour rien. Cherchée seulement ici, quand ça diverge.
        var ailleurs = AutreMachineAvecLaFinition(machine, voulue.Value);

        var conseil = ailleurs is { } autre
            ? $" La machine {autre} a chargé du {Dire(voulue.Value)} : arrêtez le tirage si le " +
              "client y tient, et choisissez-la dans la liste « Minilab »."
            : $" Arrêtez le tirage si le client y tient, et chargez un rouleau {Dire(voulue.Value)}.";

        var message =
            $"Commande {machine} : le client a demandé du {Dire(voulue.Value)}, le rouleau chargé " +
            $"est en {Dire(chargee.Value)}. Le tirage part quand même." + conseil;

        Log?.Invoke($"⚠ {message}");
        Avertir?.Invoke(message);
    }

    /// <summary>
    /// Une machine prête, autre que celle visée, qui aurait chargé cette surface — ou
    /// null. Une machine muette est sautée sans bruit : on est déjà dans un chemin
    /// d'échec, et une seconde panne n'a rien à y ajouter.
    /// </summary>
    private char? AutreMachineAvecLaFinition(char visee, De100Surface voulue)
    {
        foreach (var machine in _minilab!.ReadyMachines())
        {
            if (machine == visee) continue;

            try
            {
                if (_minilab.LoadedSurface(machine) == voulue) return machine;
            }
            catch
            {
                // machine endormie ou injoignable : rien à proposer de son côté
            }
        }

        return null;
    }

    /// <summary>
    /// Le nom d'une surface tel qu'on le dit à l'opérateur. <c>De100Surface.Lustre</c>
    /// affiché tel quel ne lui apprend rien : c'est un nom d'énumération anglais, lu dans
    /// un message qu'on parcourt un tirage à la main.
    ///
    /// Publique parce que la barre du minilab décrit les mêmes rouleaux : elle affichait
    /// « 152 mm Glossy » là où l'avertissement dit « brillant ».
    /// </summary>
    public static string Dire(De100Surface surface) => surface switch
    {
        De100Surface.Glossy or De100Surface.GlossyThick => FinitionPapier.Brillant.ToLowerInvariant(),
        De100Surface.Lustre => FinitionPapier.Lustre.ToLowerInvariant(),
        De100Surface.Matte or De100Surface.PhotoMatte => FinitionPapier.Mat.ToLowerInvariant(),
        De100Surface.FineArtMatte => "mat fin",
        De100Surface.Silk => "satiné",
        De100Surface.Pearl => "nacré",
        _ => surface.ToString().ToLowerInvariant(),
    };

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
    private string FitPageToRoll(RenderedPage page, double rollWidthMm, double lengthMm, char machine)
    {
        const int dpi = 300; // le DE100 travaille à 300 ppp, quel que soit le produit

        var (cibleW, cibleH) = DefinitionAttendue(machine, rollWidthMm, lengthMm, dpi);

        // Les cotes NUES du tirage — sans le débord de la machine. C'est la taille à
        // laquelle le tirage doit PARAÎTRE, bandes blanches du rouleau comprises.
        var (nueW, nueH) = (MmPx.ToPixels(rollWidthMm, dpi), MmPx.ToPixels(lengthMm, dpi));

        using var image = new MagickImage(page.Path);

        var pivote = image.Width > image.Height != cibleW > cibleH && image.Width != image.Height;
        if (pivote) image.Rotate(90);

        // Une image en NIVEAUX DE GRIS doit être réécrite même si sa taille est déjà juste
        // — voir EnTroisCanaux : le minilab la refuse.
        var enCouleur = image.ColorSpace == ColorSpace.sRGB && image.ChannelCount >= 3;

        if (!pivote && enCouleur
            && image.Width == (uint)cibleW && image.Height == (uint)cibleH)
            return page.Path;

        image.BackgroundColor = MagickColors.White;

        // 1. le tirage aux cotes nues : c'est ICI, et seulement ici, que du blanc a le
        //    droit d'entrer — un 10×15 posé sur un rouleau de 210 laisse une bande de
        //    chaque côté, et elle est voulue (voir MinilabPrintSize).
        image.Extent((uint)nueW, (uint)nueH, Gravity.Center, MagickColors.White);
        image.ResetPage();

        // 2. le DÉBORD de la machine, qu'on lui donne en IMAGE et non en blanc.
        RemplirLeDebord(image, cibleW, cibleH);

        EnTroisCanaux(image);

        var sortie = Path.Combine(
            Path.GetDirectoryName(page.Path)!,
            Path.GetFileNameWithoutExtension(page.Path) + "-rouleau.png");

        // même règle que les rendus : la compression maximale de Magick.NET coûte huit fois
        // le prix de la compression rapide, sur des images que rien ne conserve
        MagickInit.Write(image, sortie);
        return sortie;
    }

    /// <summary>
    /// Agrandit le tirage jusqu'à COUVRIR la définition que la machine réclame, puis rogne
    /// au centre ce qui dépasse.
    ///
    /// <b>C'est la correction du liseré blanc</b> (constaté en boutique le 05/08/2026, sur
    /// des 10×15, des 13×18 et des 15×20 — donc sur tous les tirages). Le DE100 réclame
    /// l'image AVEC les 3 mm de débord qu'il rognera : pour un 210 × 297 il veut
    /// 2515 × 3543 px là où les cotes nues en font 2480 × 3508. On se contentait d'étendre
    /// le canevas à cette définition en comblant de BLANC — la photo se retrouvait donc
    /// entourée d'un liseré d'un millimètre et demi, que le rognage de la machine ne
    /// mangeait pas entièrement puisqu'il part du bord du papier.
    ///
    /// Le débord doit être rempli par la PHOTO : c'est tout le sens du fond perdu. On
    /// agrandit donc l'image de ce qu'il faut, et la machine rogne dans l'image au lieu de
    /// rogner dans du blanc.
    ///
    /// <b>Le facteur est le même sur les deux axes.</b> Le débord vaut le même nombre de
    /// pixels en largeur qu'en hauteur, donc pas la même PROPORTION : cadrer chaque axe
    /// séparément étirerait la photo de quelques millièmes. On prend le plus exigeant des
    /// deux et l'on rogne l'excédent de l'autre — quelques pixels sur un bord, contre une
    /// déformation sur toute l'image.
    ///
    /// Sans débord — machine muette, format inconnu, repli sur notre calcul — la cible
    /// vaut les cotes nues et cette méthode ne touche à rien.
    /// </summary>
    internal static void RemplirLeDebord(MagickImage image, int cibleW, int cibleH)
    {
        if (cibleW <= 0 || cibleH <= 0) return;
        if (image.Width == (uint)cibleW && image.Height == (uint)cibleH) return;

        // Le facteur qui COUVRE la cible : le plus exigeant des deux axes. Appliqué dans
        // les deux sens — agrandir quand la machine demande plus, RÉDUIRE quand elle
        // demande moins. Ne réduire qu'en rognant tronquerait la photo au lieu de la
        // mettre à l'échelle : le cas ne s'est jamais présenté, mais il sortirait sans
        // que rien ne le dise.
        var facteur = Math.Max(cibleW / (double)image.Width, cibleH / (double)image.Height);

        image.Resize(new MagickGeometry(
            (uint)Math.Max(1, Math.Ceiling(image.Width * facteur)),
            (uint)Math.Max(1, Math.Ceiling(image.Height * facteur)))
        { IgnoreAspectRatio = true });
        image.ResetPage();

        image.Crop(new MagickGeometry((uint)cibleW, (uint)cibleH) { IgnoreAspectRatio = true },
            Gravity.Center);
        image.ResetPage();

        // le rognage peut rendre un pixel de moins sur un arrondi : la machine, elle, veut
        // la définition au pixel près
        if (image.Width != (uint)cibleW || image.Height != (uint)cibleH)
            image.Extent((uint)cibleW, (uint)cibleH, Gravity.Center, MagickColors.White);
    }

    /// <summary>
    /// Force l'image en sRGB sur TROIS canaux, alpha retiré.
    ///
    /// <b>Le minilab refuse les images en niveaux de gris</b>, et il le fait comme tout le
    /// reste : sans un mot. Un scan noir et blanc — ou une photo passée en noir et blanc —
    /// traverse tout le rendu en conservant son unique canal, et la commande était rejetée
    /// dix secondes après avoir été acceptée.
    ///
    /// C'est la cause du 21×29,7 des commandes 04-015 à 04-041 du 04/08/2026 : la photo
    /// d'essai était un scan gris. Prouvé en renvoyant le fichier de Studio, converti en
    /// sRGB et rien d'autre : il est sorti du premier coup. Le paramètre <c>ColorSpace</c>
    /// que Studio envoie au SDK vaut d'ailleurs « 1 » — RGB — depuis toujours : l'image
    /// doit lui correspondre.
    ///
    /// <b>Le define PNG est indispensable.</b> Poser <c>ColorSpace</c> et <c>ColorType</c>
    /// ne suffit pas : le format PNG réécrit en niveaux de gris dès que tous les pixels le
    /// sont, c'est son optimisation automatique. <c>color-type 2</c> l'interdit.
    /// </summary>
    private static void EnTroisCanaux(MagickImage image)
    {
        image.ColorSpace = ColorSpace.sRGB;
        image.ColorType = ColorType.TrueColor;
        image.Alpha(AlphaOption.Off);
        image.Settings.SetDefine(MagickFormat.Png, "color-type", "2");
    }

    /// <summary>
    /// La définition que la MACHINE attend pour ce format, ou la nôtre si elle n'en dit rien.
    ///
    /// <b>C'est la correction du 21×29,7</b>, trouvée par essais sur la machine le
    /// 04/08/2026. Le DE100 ajoute son DÉBORD : pour un 210 × 297 à 300 ppp il réclame
    /// 2515 × 3543 px, soit 213 × 300 mm. Studio calculait 2480 × 3508 — les cotes nues —
    /// et la machine refusait, sans motif, six fois de suite.
    ///
    /// Pourquoi le 18×24 sortait, lui, avec le même écart : il passe par un canal VARIABLE
    /// (<c>21xL</c>), qui tolère l'à-peu-près. Le 210 × 297 tombe sur le canal FIXE
    /// <c>A4</c>, qui exige la définition au pixel près. Neuf essais de NOMS de format ont
    /// tous échoué avant qu'on regarde de ce côté ; le nom n'y était pour rien.
    ///
    /// <b>Le repli n'est pas une précaution de style</b> : une machine muette, un relais
    /// coupé, un format qu'elle ne connaît pas, et l'on retombe sur le calcul qui sort
    /// depuis toujours. On ne perd jamais un tirage parce qu'une lecture a échoué.
    /// </summary>
    /// <summary>
    /// Définitions déjà obtenues de la machine, par machine, format et résolution.
    ///
    /// <b>Une seule question par format, et non une par tirage.</b> Cette lecture est sur
    /// le chemin d'impression : elle partait une fois par photo, soit 61 allers-retours sur
    /// la commande 07-008 du 07/08/2026 — 61 traversées du relais 32 bits pendant que la
    /// machine imprimait, et 61 lignes identiques dans le journal du jour, soixante sur
    /// trois cent trente-quatre. C'est le relais qu'il faut ménager avant tout : le
    /// surcharger de lectures pendant qu'il travaille est exactement ce qui l'a fait
    /// tomber (voir De100Protocol).
    ///
    /// Ce que la machine réclame pour un format donné tient à son canal d'impression et à
    /// son débord, pas au rouleau du moment : la réponse ne change pas d'un tirage à
    /// l'autre. Retenue pour la session, elle est donc relue à chaque démarrage.
    /// </summary>
    private readonly Dictionary<(char, double, double, int), (int Width, int Height)>
        _definitionsMinilab = [];

    private (int Width, int Height) DefinitionAttendue(
        char machine, double largeurMm, double longueurMm, int dpi)
    {
        var nôtre = (MmPx.ToPixels(largeurMm, dpi), MmPx.ToPixels(longueurMm, dpi));

        if (_minilab is null) return nôtre;

        var clé = (machine, largeurMm, longueurMm, dpi);
        if (_definitionsMinilab.TryGetValue(clé, out var connue)) return connue;

        try
        {
            var (w, h) = _minilab.ExpectedPixels(machine, largeurMm, longueurMm, (uint)dpi);
            if (w == 0 || h == 0) return nôtre;

            // Écrit ici, donc UNE fois par format : la ligne dit ce qu'on a appris de la
            // machine, elle n'a pas à être répétée à chaque photo qui s'en sert.
            if (w != (uint)nôtre.Item1 || h != (uint)nôtre.Item2)
                Log?.Invoke($"Minilab : {largeurMm:0}×{longueurMm:0} mm — la machine attend " +
                            $"{w}×{h} px (notre calcul : {nôtre.Item1}×{nôtre.Item2}). " +
                            "C'est SA définition qui est retenue.");

            _definitionsMinilab[clé] = ((int)w, (int)h);
            return ((int)w, (int)h);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Minilab : définition attendue illisible ({ex.Message}) — " +
                        "on garde notre calcul.");
            return nôtre;
        }
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
        MettreAJourStatutCommande(order);
        _store.Save(order);
        _store.AppendEvent(order, "confirmed-by-operator", $"env={envelope.Number}");
    }

    /// <summary>
    /// Enveloppes parties au minilab pendant cette session, par numéro de commande.
    ///
    /// Le minilab est le seul circuit qui rende son verdict APRÈS le retour de l'envoi :
    /// l'enveloppe reste « Spooled » le temps que le papier sorte. Sans cette liste, rien
    /// ne dirait quelle enveloppe une sortie confirmée vient clore.
    /// </summary>
    private readonly Dictionary<string, HashSet<int>> _enveloppesAuMinilab = [];

    /// <summary>
    /// La MACHINE a rendu son verdict sur tous les tirages : l'enveloppe est close.
    ///
    /// <b>C'est le défaut du 07/08/2026.</b> Les 61 tirages de la commande 07-008 sont
    /// tous sortis à 15:22, le relais l'a dit tirage par tirage et le journal l'a écrit —
    /// mais rien n'était gravé sur la commande. L'enveloppe restait « Spooled », donc
    /// <see cref="FindEnvelopesNeedingConfirmation"/> la remontait à CHAQUE démarrage
    /// comme une impression douteuse, pour un travail terminé six heures plus tôt. Elle a
    /// fini confirmée à la main à 21:43.
    ///
    /// Distinct de <see cref="ConfirmPrinted"/>, et l'événement le dit (<c>printed</c> et
    /// non <c>confirmed-by-operator</c>) : relire une commande six mois plus tard, c'est
    /// vouloir savoir si c'est la machine qui a répondu ou quelqu'un qui a cliqué.
    ///
    /// N'agit que sur les enveloppes RÉELLEMENT parties au minilab et encore en attente :
    /// une enveloppe déjà close, ou partie par le spouleur, n'est jamais touchée ici.
    /// </summary>
    public void ConfirmerSortieMinilab(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!_enveloppesAuMinilab.TryGetValue(order.DisplayNumber, out var envoyees)) return;

        var closes = 0;
        foreach (var envelope in order.Envelopes)
        {
            if (!envoyees.Contains(envelope.Number)) continue;
            if (ReadSpoolState(order, envelope)?.Status != SpoolState.Spooled) continue;

            WriteSpoolState(order, envelope, SpoolState.Printed);
            envelope.Status = EnvelopeStatus.Printed;
            _store.AppendEvent(order, "printed", $"env={envelope.Number}, minilab");
            closes++;
        }

        if (closes == 0) return;

        MettreAJourStatutCommande(order);
        _store.Save(order);
        _enveloppesAuMinilab.Remove(order.DisplayNumber);
    }

    /// <summary>
    /// Sort une feuille blanche sur une machine du minilab : le séparateur PHYSIQUE entre
    /// deux commandes.
    ///
    /// Quand deux commandes s'enchaînent sur le même rouleau, les tirages tombent dans le
    /// même bac et rien ne dit où finit l'une et où commence l'autre — l'opérateur trie
    /// trente photos à la main en espérant reconnaître les visages. Une feuille vierge
    /// entre les deux règle la question sans rien lire.
    ///
    /// <b>Le format le plus court du rouleau</b>, et non celui de la commande : ce papier
    /// part à la poubelle, il n'a aucune raison de coûter un 15×20. Sur un rouleau de
    /// 152 mm c'est un « 15xS » de 50 mm, soit un tiers d'un 10×15.
    ///
    /// Ne lève jamais : une séparation qui ne sort pas ne doit surtout pas empêcher la
    /// commande suivante de partir. Rend vrai si la feuille est bien partie.
    /// </summary>
    public bool TirerFeuilleDeSeparation(char machine)
    {
        if (_minilab is null) return false;

        try
        {
            var largeurRouleau = _minilab.LoadedPaperWidthMm(machine);
            if (largeurRouleau <= 0)
            {
                Log?.Invoke($"Séparation : la machine {machine} ne dit pas quel rouleau elle porte, " +
                            "aucune feuille blanche n'a été tirée.");
                return false;
            }

            // le moins cher des formats que ce rouleau porte
            var format = De100Formats.ForPaperWidth(largeurRouleau)
                .OrderBy(f => De100Formats.ConsumedLengthMm(f, largeurRouleau))
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            if (format is null)
            {
                Log?.Invoke($"Séparation : aucun format ne tient sur le rouleau de " +
                            $"{largeurRouleau} mm de la machine {machine}.");
                return false;
            }

            // le côté qui n'est pas la largeur du rouleau : c'est la longueur de coupe
            var longueur = format.ShortSideMm == largeurRouleau ? format.LengthMm : format.ShortSideMm;
            var (largeurMm, longueurMm) = MinilabPrintSize(largeurRouleau, longueur, largeurRouleau);

            const int dpi = 300;
            var (cibleW, cibleH) = DefinitionAttendue(machine, largeurMm, longueurMm, dpi);

            var fichier = Path.Combine(Path.GetTempPath(),
                $"studio-separation-{cibleW}x{cibleH}.png");

            if (!File.Exists(fichier))
            {
                using var blanche = new MagickImage(MagickColors.White, (uint)cibleW, (uint)cibleH);
                EnTroisCanaux(blanche);
                MagickInit.Write(blanche, fichier);
            }

            // Identifiant SANS le découpage « numéro-enveloppe-rang » : le suivi retrouve
            // une commande en retirant les deux derniers segments (voir
            // <see cref="OrderNumberOf"/>), et un séparateur qui lui ressemblerait viendrait
            // fausser le compte des tirages d'une vraie commande. Un seul mot, aucun tiret :
            // il ne désigne personne, et le verdict de la machine sera ignoré.
            var job = new De100PrintJob(
                JobId: $"SEPARATION{DateTime.Now:HHmmssfff}",
                ImagePath: fichier,
                WidthMm: largeurMm,
                HeightMm: longueurMm,
                PrintSizeName: format.Name,
                Surface: De100Surface.Glossy,
                Copies: 1);

            var handle = _minilab.Submit([job], machine);

            Log?.Invoke($"Séparation : feuille blanche {format.Name} envoyée à la machine {machine} " +
                        $"(commande {handle}).");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Séparation : feuille blanche impossible sur la machine {machine} — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fait suivre à la COMMANDE l'état de ses enveloppes.
    ///
    /// Le statut de la commande n'était écrit qu'à sa création : toutes restaient
    /// « Submitted » à vie, y compris une fois tirées et remises au client. Ce qui compte
    /// n'est pas l'affichage — les écrans lisent les enveloppes — mais ce qu'on relit dans
    /// <c>order.json</c> des mois plus tard, et ce sur quoi les statistiques s'appuieront.
    ///
    /// Une commande annulée le reste : ce n'est pas à un tirage de la rouvrir.
    /// </summary>
    /// <remarks>
    /// Interne plutôt que privée, comme <see cref="FinitionDnp"/> : la règle se vérifie
    /// autrement qu'en passant une journée au comptoir à regarder des commandes changer
    /// d'état.
    /// </remarks>
    internal static void MettreAJourStatutCommande(Order order)
    {
        if (order.Status == OrderStatus.Cancelled) return;
        if (order.Envelopes.Count == 0) return;

        // Tout a été rappelé : la commande est annulée, et surtout pas « prête ».
        if (order.Envelopes.All(e => e.Status == EnvelopeStatus.Canceled))
        {
            order.Status = OrderStatus.Cancelled;
            return;
        }

        // « Prête » veut dire prête à être retirée : il suffit qu'une enveloppe attende
        // encore la main de l'opérateur (grand format sur l'Epson) pour qu'elle ne le soit
        // pas, même si toutes les autres sont sorties.
        if (order.Envelopes.All(e => e.Status is EnvelopeStatus.Printed or EnvelopeStatus.Canceled))
            order.Status = OrderStatus.Ready;
        else if (order.Envelopes.Any(e => e.Status is EnvelopeStatus.Rendering
                                              or EnvelopeStatus.Spooled
                                              or EnvelopeStatus.AwaitingManualPrint))
            order.Status = OrderStatus.Printing;
    }

    /// <summary>
    /// Une page rendue et le produit qui l'a produite. Le produit est porté par la page
    /// (et non par l'enveloppe) : une enveloppe groupe un CANAL d'impression, qui peut
    /// contenir plusieurs produits — imprimante, DEVMODE et format diffèrent alors d'une
    /// page à l'autre.
    /// </summary>
    internal sealed record RenderedPage(
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

            // montage : plusieurs agrandissements du même format composés sur une feuille,
            // que l'opérateur massicote. Rien ne change au prix, seulement au nombre de
            // fichiers rendus. Sans plan — feuille absente, trop petite, hors circuit — on
            // retombe sur le rendu d'avant, un fichier par tirage.
            if (PlanDeMontage(line, product) is { } montage)
            {
                pages.AddRange(RenderMontage(envelope, line, product, montage,
                    photosDir, rendersDir, pages.Count, progression, total, ct));
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

                    // la correction propre à la machine, par-dessus ce que l'opérateur a
                    // posé — voir ReglagesDuTirage
                    var reglages = ReglagesDuTirage(item.Adjustments, product);

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

                            var caseIdentite = new RenderRequest(
                                sourcePath, cellW, cellH,
                                item.Crop, item.RotationQuarterTurns, item.FineRotationDegrees, FitMode.Fill, 0,
                                reglages, iccPath);

                            // la date de la commande, pas l'heure du rendu : une planche
                            // rejouée après un incident doit porter la même mention
                            //
                            // ⚠ La planche déclarée hors norme porte une bande MÊME SI le
                            // produit ne demande pas la date : l'avertissement n'est pas un
                            // ornement de planche, c'est ce qui protège la boutique quand la
                            // photo revient refusée du guichet.
                            var bande = sheet.DateStamp || item.PhotosNonConformes
                                ? SheetFooter.Pour(order.CreatedAt.DateTime, Marque,
                                    item.PhotosNonConformes)
                                : null;

                            if (sheet.GrandePhoto)
                            {
                                // PLANCHE DE RENTRÉE : les identités, plus le portrait.
                                //
                                // Son cadrage est celui que l'opérateur a posé, et il est
                                // gardé par la commande. À défaut — une commande d'avant ce
                                // format, ou un article dont le cadrage large s'est perdu —
                                // on reprend celui de l'identité : le portrait sortira
                                // serré, mais il sortira, et la planche reste vendable.
                                var portrait = caseIdentite with
                                {
                                    Crop = item.CropGrandePhoto ?? item.Crop,
                                };

                                ImagePipeline.RenderPlancheRentreeToFile(
                                    caseIdentite, portrait,
                                    item.SheetCopiesOverride ?? sheet.Copies,
                                    sheet.GapMm, sheet.CutMarks,
                                    targetW, targetH, output, product.Dpi,
                                    sheet.CutBorder, bande, sheet.FullBleed);
                            }
                            else
                            {
                                ImagePipeline.RenderIdSheetToFile(caseIdentite,
                                    item.SheetCopiesOverride ?? sheet.Copies, sheet.GapMm, sheet.CutMarks,
                                    targetW, targetH, output, product.Dpi,
                                    sheet.CutBorder, bande, sheet.FullBleed);
                            }
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
                                reglages,
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
    /// Les réglages de l'opérateur, plus la correction d'exposition du produit.
    ///
    /// Une COPIE, jamais l'objet de l'article : celui-ci appartient à la commande
    /// enregistrée, et l'y ajouter ferait s'empiler la correction à chaque réimpression —
    /// la troisième sortirait délavée. Ce que la commande garde doit rester ce que
    /// l'opérateur a demandé ; la correction de machine se rejoue au rendu.
    ///
    /// Elle s'ajoute à l'exposition et ne la remplace pas : l'opérateur qui a déjà remonté
    /// une photo sous-exposée ne doit pas voir son geste effacé.
    /// </summary>
    public static ImageAdjustments AvecLaCorrectionDuProduit(
        ImageAdjustments reglages, Product product)
    {
        ArgumentNullException.ThrowIfNull(reglages);
        ArgumentNullException.ThrowIfNull(product);

        if (product.PrintExposure == 0) return reglages;

        var corriges = reglages.Clone();
        corriges.Exposure += product.PrintExposure;
        return corriges;
    }

    /// <summary>
    /// Les réglages avec lesquels ce tirage est RENDU : ceux de l'opérateur, la correction
    /// du produit, puis celle de la machine qui va le sortir.
    ///
    /// <b>Un seul point de passage pour les trois chemins de rendu</b> — planche d'identité,
    /// montage à taille libre, tirage ordinaire. Les trois appelaient
    /// <see cref="AvecLaCorrectionDuProduit"/> chacun de leur côté ; y ajouter la machine
    /// trois fois aurait fini par en oublier un, et un seul produit non compensé suffit à
    /// faire douter du réglage entier.
    ///
    /// <b>⚠ CE CHEMIN N'EST PAS CELUI DE L'APERÇU</b>, et il ne doit jamais le devenir : la
    /// compensation existe pour rattraper l'écart entre l'écran et le papier. Voir
    /// <see cref="CorrectionMachine"/>.
    /// </summary>
    private ImageAdjustments ReglagesDuTirage(ImageAdjustments reglages, Product product)
    {
        var corriges = AvecLaCorrectionDuProduit(reglages, product);
        return Corrections?.Appliquer(corriges, product) ?? corriges;
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
    /// <summary>
    /// Le sens que les cadrages de la ligne dessinent : vrai debout, faux couché, null
    /// quand ils ne s'accordent pas (ou qu'aucun ne se lit).
    ///
    /// Le cadrage est enregistré en FRACTIONS de la photo : son rapport ne se connaît qu'en
    /// lisant les cotes du fichier. On les PING — l'en-tête suffit, l'image n'est pas
    /// décodée.
    ///
    /// <b>⚠ LES COTES SONT CELLES QU'ON VOIT, orientation EXIF appliquée.</b> C'est LA
    /// subtilité de cette méthode, et elle lui a manqué jusqu'au 17/08/2026 : elle lisait
    /// l'en-tête brut (<c>MagickImageInfo</c>), qui rend les pixels tels qu'ils sont STOCKÉS.
    /// Une photo prise à la verticale est stockée couchée avec une étiquette EXIF « tourne-moi »
    /// — 6016 × 4000 dans le fichier, 4000 × 6016 à l'écran. Or le cadrage a été posé sur
    /// l'image REDRESSÉE (le rendu fait <c>AutoOrient</c>, voir <c>ImagePipeline</c>), donc
    /// ses fractions se rapportent à 4000 × 6016. Les multiplier par les cotes brutes
    /// retourne le verdict : un portrait soigneusement cadré debout était déclaré COUCHÉ, et
    /// les cases de la planche basculaient — le client demandait du 7 × 10 et repartait avec
    /// du 10 × 7. Signalé depuis le comptoir, commande 17-021 du 17/08/2026.
    ///
    /// <see cref="ImagePipeline.GetOrientedSize"/> porte la règle, quarts de tour de
    /// l'opérateur compris ; c'est déjà elle qu'appellent les deux autres endroits de ce
    /// fichier qui ont besoin des cotes d'une photo.
    ///
    /// À égalité — moitié debout, moitié couché — on ne touche à rien : mieux vaut le sens
    /// saisi qu'un arbitrage arbitraire qui surprendrait une planche sur deux.
    /// </summary>
    internal static bool? SensDesCadrages(OrderLine line, string photosDir)
    {
        var debout = 0;
        var couche = 0;

        foreach (var item in line.Items)
        {
            try
            {
                // orientation EXIF ET quarts de tour de l'opérateur : les deux changent la
                // photo AVANT que le cadrage ne s'y applique
                var (largeur, hauteur) = ImagePipeline.GetOrientedSize(
                    Path.Combine(photosDir, item.FileName), item.RotationQuarterTurns);

                var l = largeur * item.Crop.Width;
                var h = hauteur * item.Crop.Height;
                if (l <= 0 || h <= 0) continue;

                if (h > l) debout++;
                else if (l > h) couche++;
            }
            catch (Exception)
            {
                // fichier absent ou illisible : il se signalera au rendu, pas ici
            }
        }

        if (debout == couche) return null;
        return debout > couche;
    }

    private IEnumerable<RenderedPage> RenderCustomSheets(Envelope envelope,
        OrderLine line, Product product, string photosDir, string rendersDir,
        int dejaFaites, IProgress<PrintProgress>? progression, int total, CancellationToken ct)
    {
        var celluleWmm = line.CustomCellWidthMm!.Value;
        var celluleHmm = line.CustomCellHeightMm!.Value;

        // LE CADRAGE DÉCIDE DU SENS DE LA CASE.
        //
        // Le rendu tenait pour acquis que « le cadrage a été posé pour ce rapport ». Il ne
        // l'est pas : l'écran de recadrage donne au cadre l'orientation de LA PHOTO, pendant
        // que la planche prend celle du FORMAT SAISI. Les deux ne se parlaient jamais.
        // Résultat à Créteil le 14/08/2026 (commandes 14-018 puis 14-027) : des portraits
        // soigneusement cadrés debout, coulés dans des cases couchées de 80 × 65 mm, donc
        // coupés en haut et en bas — deux fois, sur du papier.
        //
        // Le rectangle de cadrage EST l'expression de ce que l'opérateur veut voir sortir :
        // c'est lui qui tranche, pas l'ordre dans lequel deux nombres ont été tapés.
        if (SensDesCadrages(line, photosDir) is { } cadragesDebout
            && cadragesDebout == celluleWmm > celluleHmm)
        {
            Log?.Invoke(
                $"Planche personnalisée : cases remises {(cadragesDebout ? "debout" : "couchées")} " +
                $"({celluleHmm:0.#} × {celluleWmm:0.#} mm au lieu de {celluleWmm:0.#} × {celluleHmm:0.#}) " +
                "— c'est le sens des cadrages posés à l'écran.");

            (celluleWmm, celluleHmm) = (celluleHmm, celluleWmm);
        }

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
                                // …sauf en « cadre blanc » à taille libre, où la marge est
                                // justement ce qu'on vend. Elle est portée par la LIGNE :
                                // le produit désigne le papier de la planche, et la lui
                                // demander mettrait le blanc autour de la feuille entière.
                                MmPx.ToPixels(line.CustomCellBorderMm ?? 0, product.Dpi),
                                ReglagesDuTirage(item.Adjustments, product),
                                IccPath(product, item.Finish),
                                item.CutBorder),
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
                    cutBorder: ContourDemande(cellules),
                    calerAuCoin: line.CustomSheetCoin);
            }

            yield return new RenderedPage(output, 1, plan.SheetWidthMm, plan.SheetHeightMm,
                product, line.Items[0].Finish);

            progression?.Report(new PrintProgress(
                PrintProgress.Rendu, Math.Min(total, dejaFaites + n + 1), total));
        }
    }

    /// <summary>
    /// Le plan de montage de cette ligne, ou <c>null</c> pour le rendu d'avant : un fichier
    /// par tirage.
    ///
    /// <b>Tout ce qui cloche rend null, et rien n'échoue.</b> Une feuille disparue du
    /// catalogue, désactivée, passée sur une autre machine, ou devenue trop petite pour deux
    /// tirages : la commande sort quand même, comme elle serait sortie sans montage. Le
    /// choix de la feuille a pu être fait des heures avant le tirage — parfois avant une
    /// modification du catalogue — et une commande déjà encaissée ne doit pas se refuser à
    /// sortir pour cette raison. Le journal dit ce qui s'est passé.
    ///
    /// ⚠ <b>Grand format uniquement.</b> Le minilab plafonne à 210 mm de large : deux 24×30
    /// n'y tiendront jamais. La DNP a déjà son propre cas (15×20 → deux 10×15) depuis la
    /// 1.3.15. Périmètre décidé par l'exploitant le 12/08/2026.
    /// </summary>
    private (PlanMontage Plan, Product Feuille)? PlanDeMontage(OrderLine line, Product product)
    {
        if (!line.IsMontage) return null;

        if (product.Output != ProductOutput.ManualFile)
        {
            Log?.Invoke($"Montage ignoré pour {product.Code} : ce n'est pas un tirage grand format.");
            return null;
        }

        var feuille = _catalog.Find(line.MontageSheetCode!);
        if (feuille is null || feuille.Output != ProductOutput.ManualFile)
        {
            Log?.Invoke($"Montage ignoré : la feuille « {line.MontageSheetCode} » n'est plus au " +
                        "catalogue grand format. Un fichier par tirage, comme avant.");
            return null;
        }

        var papier = new PaperOption(feuille.Code, feuille.Name,
            feuille.WidthMm, feuille.HeightMm, feuille.Dpi);

        var plan = MontageFeuille.Pour(papier, product.WidthMm, product.HeightMm);
        if (plan is null)
        {
            Log?.Invoke($"Montage ignoré : un {product.Name} ne tient pas deux fois sur un " +
                        $"{feuille.Name}. Un fichier par tirage, comme avant.");
            return null;
        }

        return (plan, feuille);
    }

    /// <summary>
    /// Les feuilles d'une ligne montée : plusieurs tirages du même format composés côte à
    /// côte, que l'opérateur massicote.
    ///
    /// <b>Chaque photo est rendue à SON orientation</b>, puis tournée d'un quart de tour si
    /// l'empreinte est en travers — voir <see cref="MontageFeuille"/>. C'est ce qui permet
    /// de monter deux 24×30 portrait sur un 40×60 (l'empreinte y est couchée) sans trahir le
    /// cadrage posé à l'écran, et de mêler portraits et paysages sur la même feuille.
    ///
    /// Une feuille = une page à un exemplaire : les copies d'une photo sont des cases, pas
    /// des tirages répétés.
    /// </summary>
    private IEnumerable<RenderedPage> RenderMontage(Envelope envelope, OrderLine line,
        Product product, (PlanMontage Plan, Product Feuille) montage,
        string photosDir, string rendersDir, int dejaFaites,
        IProgress<PrintProgress>? progression, int total, CancellationToken ct)
    {
        var (plan, feuille) = montage;

        // Tout se compte dans les pixels de la FEUILLE : c'est elle qu'on fabrique. Un
        // format et sa feuille peuvent être déclarés à des résolutions différentes, et
        // mélanger les deux poserait les cases à côté de leurs repères de coupe.
        var dpi = feuille.Dpi;
        var (empreinteW, empreinteH) =
            MontageFeuille.EmpreintePixels(plan, product.WidthMm, product.HeightMm, dpi);

        var feuilles = CustomSheetLayout.Distribute(
            line.Items.Select(i => i.Quantity).ToList(), plan.ParFeuille);

        for (var n = 0; n < feuilles.Count; n++)
        {
            ct.ThrowIfCancellationRequested();

            var output = Path.Combine(rendersDir,
                $"env{envelope.Number:00}-{line.ProductCode}-montage-{n + 1:000}.png");

            if (!File.Exists(output)) // rendu déterministe : réutilisable après un crash
            {
                var cellules = feuilles[n]
                    .Select(place => new ImagePipeline.SheetCell(
                        RequeteDeLaCase(line.Items[place.PhotoIndex], product, photosDir, dpi),
                        place.Copies))
                    .ToList();

                ImagePipeline.RenderCustomSheetToFile(
                    cellules, SheetSpec.DefaultGapMm, cutMarks: true,
                    MmPx.ToPixels(plan.LargeurMm, dpi),
                    MmPx.ToPixels(plan.HauteurMm, dpi),
                    output, dpi,
                    cutBorder: ContourDemande(cellules),
                    footprint: (empreinteW, empreinteH));

                Log?.Invoke($"Montage {Path.GetFileName(output)} : {feuilles[n].Sum(c => c.Copies)} " +
                            $"× {product.Name} sur {feuille.Name} ({plan.ParFeuille} par feuille" +
                            (plan.CelluleTournee ? ", posés en travers)" : ")"));
            }

            // La feuille porte les dimensions de la FEUILLE : c'est ce que l'écran grand
            // format doit sortir à 100 %, et non le format du tirage qu'elle contient.
            yield return new RenderedPage(output, 1, plan.LargeurMm, plan.HauteurMm,
                feuille, line.Items[0].Finish);

            progression?.Report(new PrintProgress(
                PrintProgress.Rendu, Math.Min(total, dejaFaites + n + 1), total));
        }
    }

    /// <summary>
    /// La case d'une photo montée, <b>à l'orientation de la photo</b> et non à celle de
    /// l'empreinte.
    ///
    /// C'est la même règle que le rendu ordinaire d'un agrandissement (voir
    /// <see cref="RenderEnvelope"/>) : la toile suit la photo, un portrait sort en 24×30 et
    /// un paysage en 30×24. Sans cela, un portrait posé dans une empreinte couchée serait
    /// recadré en paysage — et sur un grand format, c'est du papier cher perdu.
    /// </summary>
    private RenderRequest RequeteDeLaCase(
        OrderItem item, Product product, string photosDir, int dpi)
    {
        var sourcePath = Path.Combine(photosDir, item.FileName);

        var (imgW, imgH) = ImagePipeline.GetOrientedSize(sourcePath, item.RotationQuarterTurns);
        var (toileW, toileH) = CropMath.TiltedCanvas(imgW, imgH, item.FineRotationDegrees);

        var (caseW, caseH) = CropMath.OrientCanvas(
            MmPx.ToPixels(product.WidthMm, dpi), MmPx.ToPixels(product.HeightMm, dpi),
            (int)Math.Round(toileW), (int)Math.Round(toileH), item.Crop);

        return new RenderRequest(
            sourcePath, caseW, caseH,
            item.Crop, item.RotationQuarterTurns, item.FineRotationDegrees,
            item.FitOverride ?? product.DefaultFit,
            MmPx.ToPixels(product.BorderMm, dpi),
            ReglagesDuTirage(item.Adjustments, product),
            IccPath(product, item.Finish),
            item.CutBorder);
    }

    /// <summary>
    /// Le contour de découpe est-il demandé sur cette planche ?
    ///
    /// ⚠ <b>Il était posé D'OFFICE, et la case de l'opérateur ne servait à rien.</b> Les
    /// deux rendus de planche personnalisée passaient <c>cutBorder: true</c> en dur, au
    /// motif que « le contour est le seul repère utile : on coupe une planche aux ciseaux ».
    /// L'argument se tient — mais il ne se décide pas ici : l'écran des tailles libres offre
    /// la case, <c>CustomSize.ContourNoir</c> la porte, <c>order.json</c> l'enregistre
    /// (<c>"CutBorder": false</c>) et le rendu l'ignorait. Signalé le 20/08/2026 : « il y
    /// était d'office sans avoir coché l'option ».
    ///
    /// <b>Il suffit qu'UNE case le demande.</b> On ne coupe pas une planche à moitié : le
    /// trait sert à massicoter la feuille entière, et l'écran pose de toute façon le même
    /// choix sur toutes les photos lors de la bascule.
    ///
    /// ⚠ Les REPÈRES DANS LES MARGES (<c>cutMarks</c>) ne suivent pas cette case : ils sont
    /// hors des photos, ils aident le massicot sans marquer le tirage, et aucun réglage ne
    /// les vise. À changer le jour où l'opérateur le demande, pas avant.
    /// </summary>
    private static bool ContourDemande(IEnumerable<ImagePipeline.SheetCell> cellules) =>
        cellules.Any(c => c.Request.CutBorder);

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

                // CE QUE LA MACHINE VA RECEVOIR, écrit une fois par produit et par
                // commande. Sans cette ligne, un tirage raté ne laisse aucune trace des
                // réglages sur lesquels il est sorti — et c'est la première chose qu'on
                // cherche quand du papier part à la poubelle (06/08/2026).
                if (devMode is not null)
                {
                    var resume = LectureDevMode.Resume(devMode);
                    if (resume.Count > 0)
                        Log?.Invoke($"Réglages pilote de « {product.Name} » : {string.Join(" · ", resume)}");

                    foreach (var alerte in LectureDevMode.Avertissements(devMode))
                        Log?.Invoke($"⚠ {product.Name} : {alerte}");
                }

                // Le réglage du POSTE que le DEVMODE ne porte pas, et qui décide pourtant
                // de la façon dont l'image arrive au pilote. Voir DnpSpouleur.
                //
                // ⚠ IL S'ANNONCE À CÔTÉ, PAS ICI. La lecture passe par WMI et coûte une
                // demi-seconde ; elle se payait sur le chemin de l'impression, à chaque
                // commande, pour une ligne de journal qui ne change rien au tirage. Voir
                // AnnoncerLesFonctionnalitesAvancees.
                Devices.Dnp.DnpSpouleur.AnnoncerLesFonctionnalitesAvancees(
                    product.PrinterName, message => Log?.Invoke(message));
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

                    // AU RYTHME DE LA MACHINE : on ne remet une page que si la file a de la
                    // place, et on s'arrête net si elle tombe en panne. Voir CadenceSpouleur.
                    var cadence = CadencePour(product.PrinterName);
                    if (cadence is not null)
                    {
                        var place = cadence.Attendre(cadence.PlafondEnFile, ct);
                        if (place.EnPanne)
                            throw new PrinterNotReadyException(place.Panne);
                    }

                    // LE CHEMIN DIRECT D'ABORD, quand la machine s'y prête : c'est le seul
                    // qui ne fabrique pas le fantôme coloré. Voir EnvoyerDirectementALaDnp.
                    if (!EnvoyerDirectementALaDnp(product, page))
                    {
                        // aplati en 24 bits sur du blanc, une fois pour toutes les copies :
                        // voir BitmapPrinter.ChargerPourImpression
                        bitmap ??= BitmapPrinter.ChargerPourImpression(page.Path);

                        BitmapPrinter.Print(
                            product.PrinterName, bitmap, page.WidthMm, page.HeightMm,
                            devMode, pdfPath, documentName);
                    }

                    faites++;

                    // ce qui est SORTI, pas ce qui est remis : c'est ce nombre qui décide
                    // où l'on reprendrait
                    noterAvancement?.Invoke(cadence?.PagesSorties(faites) ?? faites);
                    progression?.Report(new PrintProgress(
                        PrintProgress.Impression, faites, total, tuileDnp));
                }
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        // Fin de commande : on attend que la machine ait VRAIMENT tout sorti avant de
        // déclarer l'enveloppe imprimée. Sans cette attente, une commande de six cents
        // photos passait « imprimée » cinq secondes après le premier tirage, et une panne
        // survenue ensuite ne trouvait plus rien à reprendre.
        var derniere = CadencePour(pages.LastOrDefault()?.Product.PrinterName);
        if (derniere is not null)
        {
            var fin = derniere.Attendre(plafond: 0, ct);
            if (fin.EnPanne) throw new PrinterNotReadyException(fin.Panne);
        }
    }

    /// <summary>
    /// Tente d'envoyer une page à une DNP <b>sans passer par le pilote Windows</b>, et rend
    /// vrai si la machine l'a prise. Faux = l'appelant imprime comme avant.
    ///
    /// <b>Pourquoi ce chemin passe en premier.</b> Le fantôme coloré n'apparaît QUE par le
    /// pilote — jamais depuis DiLand, qui ne l'emprunte pas. Mesuré le 06/08/2026 : DiLand
    /// imprime sans que le spouleur en sache rien, et le premier tirage envoyé par ce
    /// chemin depuis Studio est sorti sans le défaut. Le pilote de DNP date de 2017 et n'a
    /// pas de successeur : il n'y avait rien à corriger de ce côté-là.
    ///
    /// <b>Trois conditions, et l'on renonce dès que l'une manque</b> — un tirage qui sort
    /// par le pilote vaut mille fois mieux qu'un tirage qui ne sort pas :
    ///
    /// 1. le relais 32 bits répond (le SDK des DNP y vit, comme celui du minilab) ;
    /// 2. le SDK découvre EXACTEMENT UNE imprimante. Avec plusieurs DNP, rien ne dit
    ///    laquelle porte la file Windows visée : le SDK donne un numéro de série, la file un
    ///    nom, et personne ne fait le lien. Tant que l'appariement n'est pas écrit, on ne
    ///    devine pas sur quelle machine part le papier ;
    /// 3. le rendu tombe sur la TRAME NATIVE de la machine. Le pilote ré-échantillonne ce
    ///    qui ne tombe pas juste ; l'envoi direct, lui, ne corrige rien — une image d'un
    ///    pixel de trop sortirait décalée. La planche identité était dans ce cas jusqu'au
    ///    06/08/2026 (1845 × 1239 au lieu de 1844 × 1240).
    ///
    /// <b>Et une condition qui n'en est pas une : le DÉLAI DÉPASSÉ.</b> Voir le bloc qui
    /// l'attrape — c'est le seul incident dont on ne se relève pas par le pilote.
    /// </summary>
    /// <remarks>
    /// Interne plutôt que privée, comme <see cref="FinitionDnp"/> : la règle « on ne se
    /// replie pas sur un délai dépassé » ne se vérifie autrement qu'en attendant qu'une
    /// DNP soit lente devant un vrai client, et elle a déjà coûté une feuille.
    /// </remarks>
    internal bool EnvoyerDirectementALaDnp(Product product, RenderedPage page)
    {
        if (_minilab is null) return false;
        if (!ImprimanteDnp.EstUneDnp(product.PrinterName)) return false;

        try
        {
            var vues = _minilab.DnpSnapshotAsync().GetAwaiter().GetResult();
            if (vues.Count != 1)
            {
                Log?.Invoke(vues.Count == 0
                    ? "Envoi direct DNP impossible : le SDK ne voit aucune machine. On passe par le pilote."
                    : $"Envoi direct DNP impossible : {vues.Count} machines vues, et rien ne dit " +
                      "laquelle porte cette file. On passe par le pilote.");
                return false;
            }

            // LE POINT DE NON-RETOUR, et il tient à cette accolade : à partir d'ici l'image
            // est peut-être PARTIE, et le repli sur le pilote n'est plus une sécurité mais
            // un doublon en puissance. L'interrogation qui précède, elle, n'engage rien —
            // elle peut échouer, expirer, mentir : on se replie sans y penser.
            int acceptes;
            try
            {
                acceptes = _minilab
                    .DnpPrintAsync(page.Path, vues[0].PortNumber, (int)FinitionDnp(product, page.Finish), 1)
                    .GetAwaiter().GetResult();
            }
            catch (TimeoutException ex)
            {
                // Un délai dépassé ne dit pas « la machine n'a pas pris le tirage » : il
                // dit « je ne sais pas ». L'appel natif, lui, continue sa vie dans le
                // relais et aboutit souvent — commande 12-012 du 12/08/2026 : renoncement
                // à 10 s, acceptation par la machine 1 s plus tard, et la planche est
                // sortie deux fois parce qu'on l'avait entre-temps confiée au pilote.
                //
                // Se replier ici, c'est jouer une feuille à pile ou face. On arrête plutôt
                // l'enveloppe et l'on laisse l'opérateur regarder le bac : lui seul peut
                // dire ce qui est sorti, et ce qui manque se refait depuis « Commandes du
                // jour ».
                Log?.Invoke($"Envoi direct DNP sans réponse ({ex.Message}) : on NE se replie " +
                            "PAS sur le pilote — le tirage est peut-être déjà parti.");
                throw new PrintUnconfirmedException(ex.Message);
            }

            if (acceptes >= 1)
            {
                Log?.Invoke($"Tirage envoyé DIRECTEMENT à la DNP {vues[0].SerialNumber} " +
                            $"({Path.GetFileName(page.Path)}) — le pilote Windows n'a rien vu.");
                return true;
            }

            Log?.Invoke("La DNP a refusé l'envoi direct : on repasse par le pilote.");
            return false;
        }
        catch (PrintUnconfirmedException)
        {
            // traversée telle quelle : le repli générique ci-dessous est précisément ce
            // qu'elle interdit
            throw;
        }
        catch (Exception ex)
        {
            // Le repli n'est pas une politesse : sans lui, une panne du relais empêcherait
            // d'imprimer du tout, alors que le pilote, lui, répond toujours. Il reste bon
            // pour tout ce qui est un ÉCHEC FRANC — relais absent, image introuvable,
            // machine qui refuse : là, on sait que rien n'est parti.
            Log?.Invoke($"Envoi direct DNP indisponible ({ex.Message}) : on passe par le pilote.");
            return false;
        }
    }

    /// <summary>
    /// La finition à annoncer à la machine pour ce tirage.
    ///
    /// Le pilote la porte dans son DEVMODE ; l'envoi direct, lui, doit la déclarer
    /// lui-même. <b>Le défaut de la boutique est le BRILLANT</b>, et non le lustré : le
    /// DEVMODE de la planche identité porte bien <c>OPTYPE_LUSTER</c>, mais ce nom interne
    /// s'affiche « Brillant » dans le dialogue du pilote — le lustré, lui, s'y appelle
    /// <c>OPTYPE_LUSTER_MATTE</c>. C'est le piège déjà noté dans <c>LectureDevMode</c>, et
    /// il avait été pris ici : tout tirage sans finition nommée — c'est-à-dire TOUS ceux du
    /// catalogue, dont les planches identité — partait en lustré. DiLand, sur la même
    /// machine, envoie <c>SetOvercoatFinish(GLOSSY)</c>.
    /// </summary>
    /// <remarks>
    /// Interne plutôt que privée, comme la règle de choix de machine du minilab : une
    /// finition ne se vérifie autrement qu'en gâchant du papier.
    /// </remarks>
    internal static Devices.Dnp.DnpOvercoat FinitionDnp(Product product, string? finish)
    {
        var nom = finish ?? product.Finishes?.FirstOrDefault()?.Name ?? "";

        // « Mat fin » avant « mat » : le second est contenu dans le premier. Deux mots,
        // donc cherché sur la chaîne entière.
        if (EstMatFin(nom)) return Devices.Dnp.DnpOvercoat.FineMatte;

        // « lust » couvre lustré, lustre et luster.
        if (FinitionDit(nom, "lust")) return Devices.Dnp.DnpOvercoat.Luster;

        if (FinitionDit(nom, "mat")) return Devices.Dnp.DnpOvercoat.Matte;

        // Brillant, et tout ce qu'on ne sait pas nommer : c'est ce que la file demande.
        return Devices.Dnp.DnpOvercoat.Glossy;
    }

    /// <summary>
    /// Reconnaissance d'une finition sur les MOTS de son nom, et non sur la chaîne
    /// entière : « format » contient « mat », et une finition annoncée de travers coûte
    /// la feuille.
    /// </summary>
    private static bool FinitionDit(string nom, params string[] mots) =>
        nom.Split(' ', '-', '_', ',', '(', ')')
           .Any(mot => mots.Any(m => mot.StartsWith(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>« Mat fin » se cherche sur la chaîne entière : c'est deux mots.</summary>
    private static bool EstMatFin(string nom) =>
        nom.Contains("mat fin", StringComparison.OrdinalIgnoreCase)
        || nom.Contains("fine matte", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// La surface de papier qu'une finition nommée réclame au minilab — donc, en pratique,
    /// LE ROULEAU, et par lui la machine qui recevra l'enveloppe.
    ///
    /// <b>Null veut dire « aucune exigence »</b>, et c'est le cas le plus courant : rien
    /// au comptoir ne nomme de finition, et le rouleau chargé fait foi comme il l'a
    /// toujours fait. C'est aussi ce qui distingue cette règle de
    /// <see cref="FinitionDnp"/>, qui doit bien annoncer QUELQUE CHOSE à la machine et
    /// retombe donc sur le brillant : ici, un nom qu'on ne sait pas traduire ne doit
    /// surtout pas se transformer en exigence, sous peine de refuser des commandes que
    /// l'on tirait sans rien dire hier.
    ///
    /// Les noms viennent du client par la borne (<see cref="FinitionPapier"/>) ou de la
    /// main de l'opérateur dans le catalogue : la reconnaissance porte donc sur les mots,
    /// pas sur une égalité de chaîne.
    /// </summary>
    /// <remarks>
    /// <b>Publique</b>, et non seulement vérifiable comme <see cref="FinitionDnp"/> :
    /// l'écran des photos s'en sert pour annoncer à l'opérateur la machine sur laquelle sa
    /// commande partira. Il doit appliquer LA MÊME règle que le tirage — une seconde règle
    /// écrite dans l'interface finirait par diverger, et la barre annoncerait une machine
    /// pendant que le papier sortirait de l'autre.
    /// </remarks>
    public static De100Surface? FinitionMinilab(string? finish)
    {
        var nom = finish?.Trim();
        if (string.IsNullOrEmpty(nom)) return null;

        if (EstMatFin(nom)) return De100Surface.FineArtMatte;
        if (FinitionDit(nom, "lust")) return De100Surface.Lustre;
        if (FinitionDit(nom, "mat")) return De100Surface.Matte;
        if (FinitionDit(nom, "brill", "gloss")) return De100Surface.Glossy;

        return null;
    }

    /// <summary>
    /// La surface que cette enveloppe réclame, ou null si elle n'en réclame aucune.
    ///
    /// Une enveloppe ne porte qu'une finition — <c>OrderService</c> les sépare justement
    /// pour cela, puisqu'elle part d'un bloc sur une seule machine. La première page qui
    /// en nomme une décide donc pour toutes.
    /// </summary>
    private static De100Surface? SurfaceDemandee(List<RenderedPage> pages) =>
        pages.Select(p => FinitionMinilab(p.Finish)).FirstOrDefault(s => s is not null);

    /// <summary>
    /// La cadence d'une file d'impression, ou null quand on ne sait pas la lire.
    ///
    /// Null n'est pas un cas d'erreur : une file que le spouleur ne décrit pas — pilote
    /// avare, machine virtuelle, « Print to PDF » — s'imprime comme avant, d'un trait. On
    /// ne bloque jamais une impression parce qu'une lecture WMI n'a rien donné.
    /// </summary>
    private CadenceSpouleur? CadencePour(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return null;

        if (_cadences.TryGetValue(printerName, out var connue)) return connue;

        var cadence = new CadenceSpouleur(
            () => LirePlace(printerName),
            Thread.Sleep)
        {
            Log = Log,
        };

        // une file inconnue du spouleur ne se cadence pas : on le décide UNE fois, à la
        // première lecture, plutôt qu'à chaque page
        var premiere = LirePlace(printerName);
        var retenue = premiere.PagesEnFile < 0 ? null : cadence;

        _cadences[printerName] = retenue;
        return retenue;
    }

    private readonly Dictionary<string, CadenceSpouleur?> _cadences =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// L'état de la file, traduit pour la cadence. <c>PagesEnFile = -1</c> = le spouleur
    /// n'a rien à en dire.
    /// </summary>
    private static PlaceEnFile LirePlace(string printerName)
    {
        var etat = DnpSpouleur.Lire(printerName);

        if (etat.Etat == EtatFileDnp.Inconnu)
            return new PlaceEnFile(PeutEnvoyer: true, Panne: "", PagesEnFile: -1);

        // Une file EN PAUSE est une panne de notre point de vue : rien n'en sortira tant
        // que personne ne la relance, et continuer à la remplir ne ferait qu'empiler ce
        // qu'on ne pourra pas reprendre.
        var panne = etat.Etat switch
        {
            EtatFileDnp.Erreur => etat.Message.Length > 0 ? etat.Message : "intervention nécessaire",
            EtatFileDnp.HorsLigne => $"« {printerName} » est hors ligne",
            EtatFileDnp.EnPause => $"la file de « {printerName} » est en pause",
            _ => "",
        };

        return new PlaceEnFile(panne.Length == 0, panne, etat.PhotosRestantes);
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

