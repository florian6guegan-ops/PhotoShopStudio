using System.IO;
using System.Text.Json;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Core.Imaging;
using Studio.Core.Mail;
using Studio.Imaging;
using Studio.Imaging.Faces;
using Studio.App.Infrastructure;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;
using Studio.Store;
using Studio.Store.DiLand;
using Studio.Web;

namespace Studio.App;

/// <summary>Mode de l'exécutable (config/mode.json) : poste opérateur ou borne client.</summary>
public sealed class ModeConfig
{
    public string Mode { get; set; } = "operateur";
    /// <summary>Borne : URL du poste opérateur (API commandes).</summary>
    public string OperatorUrl { get; set; } = "http://127.0.0.1:8123";
    public string BorneName { get; set; } = "Borne1";
    /// <summary>Code de sortie staff du mode borne.</summary>
    public string StaffPin { get; set; } = "2468";

    public bool IsKiosk => Mode.Equals("borne", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Composition de l'application : chemins de données et services partagés.</summary>
public sealed class AppServices
{
    public required string DataRoot { get; init; }
    public string CatalogDir => Path.Combine(DataRoot, "catalog");
    public string ProductsJson => Path.Combine(CatalogDir, "products.json");

    /// <summary>
    /// Le cache du poste : vignettes, et pages de PDF rendues.
    ///
    /// Rien de ce qui s'y trouve n'est une donnée de la boutique — tout s'y refabrique.
    /// Il est notamment le SEUL endroit où l'on écrit les pages d'un PDF : le dossier
    /// ouvert est souvent la clé du client, sur laquelle on ne pose rien.
    /// </summary>
    public string CacheDir => Path.Combine(DataRoot, "cache");

    public required ProductCatalog Catalog { get; set; }
    public required OrderFolderStore Store { get; init; }
    public required OrderService Orders { get; init; }
    public required PrintOrchestrator Printer { get; set; }

    private PendingPrintQueue? _attente;

    /// <summary>
    /// Commandes en attente d'imprimante, reprises toutes seules dès que la machine répond.
    ///
    /// Reconstruite quand l'orchestrateur l'est (rechargement du catalogue) : elle tient
    /// une référence dessus, et une file branchée sur l'ancien n'imprimerait plus rien.
    /// </summary>
    public PendingPrintQueue Attente => _attente ??=
        new PendingPrintQueue(Printer, () => Orders.Recent(30)) { Log = m => FileLog.Write(m) };

    /// <summary>Accès au minilab Fuji, partagé avec l'orchestrateur d'impression.</summary>
    public required De100BridgePrinter Minilab { get; init; }
    public required ThumbnailService Thumbnails { get; init; }

    private DiLandImporter? _diland;

    /// <summary>
    /// Reprise des commandes déposées par les bornes dans DiLand.
    ///
    /// Tant que DiLand tourne en boutique, les bornes lui envoient les commandes ; on les
    /// récupère sans les lui prendre. Construit au premier usage : le catalogue peut être
    /// rechargé, et le dépôt DiLand peut être absent sur un poste de développement.
    /// </summary>
    public DiLandImporter DiLandImport => _diland ??= new DiLandImporter(
        new DiLandRepository(DiLandRepository.DefaultRoot, Path.Combine(DataRoot, "diland")),
        Orders,
        Catalog.All.ToList(),
        Path.Combine(DataRoot, "diland", "reprises.json"),
        CommandesEnAttente);

    private AttenteStore? _attenteStore;

    /// <summary>
    /// Les commandes que l'opérateur a mises de côté pour servir quelqu'un d'autre.
    ///
    /// <b>À ne pas confondre avec <see cref="Attente"/></b>, qui est la file des tirages
    /// que l'IMPRIMANTE fait attendre. Ici, c'est l'opérateur qui met de côté, et rien
    /// n'est encore commandé.
    ///
    /// Hors du dossier de DiLand, et non sous lui : une commande mise de côté peut venir
    /// d'une clé USB ou d'un téléphone tout autant que d'une borne. C'est en préparant une
    /// commande AU COMPTOIR qu'on a le plus besoin de faire autre chose.
    /// </summary>
    public AttenteStore CommandesEnAttente =>
        _attenteStore ??= new AttenteStore(Path.Combine(DataRoot, "attente"));

    private readonly Lazy<FaceDetector> _faces = new(() => new FaceDetector(
        Path.Combine(AppContext.BaseDirectory, "models", "face_detection_yunet_2023mar.onnx")));

    /// <summary>Détecteur de visage (YuNet), chargé au premier usage.</summary>
    public FaceDetector Faces => _faces.Value;

    /// <summary>Serveur d'upload téléphone + API bornes.</summary>
    public required UploadServer Upload { get; init; }

    public required ModeConfig Mode { get; init; }
    public required TicketConfig Ticket { get; init; }
    public required BackupConfig Backup { get; init; }

    /// <summary>Dossier des fichiers de configuration, propre à ce poste.</summary>
    public string ConfigDir => Path.Combine(DataRoot, "config");

    private MailSettings? _mail;

    /// <summary>
    /// Réglages d'envoi des photos par courriel.
    ///
    /// Ils vivent dans les DONNÉES du poste (<c>config\mail.json</c>) et non dans le
    /// dépôt, qui est public : ils portent un mot de passe d'application. C'est aussi ce
    /// qui permet à chaque poste opérateur d'avoir les siens — un nouveau poste se
    /// configure depuis l'écran Paramètres, sans toucher au code ni au catalogue.
    /// </summary>
    public MailSettings Mail => _mail ??= MailSettings.Load(ConfigDir);

    /// <summary>Enregistre les réglages de courriel et les reprend aussitôt.</summary>
    public void SaveMail(MailSettings reglages)
    {
        MailSettings.Save(ConfigDir, reglages);
        _mail = reglages;
    }

    private DetourageSettings? _detourage;

    /// <summary>
    /// Comment le fond blanc des photos d'identité est détouré sur CE poste.
    ///
    /// Le réglage vit dans les données du poste parce que la réponse dépend de la machine :
    /// le réseau de neurones donne un bien meilleur contour, mais il lui faut une carte
    /// graphique et il se compte en secondes. Voir <see cref="DetourageSettings"/>.
    /// </summary>
    public DetourageSettings Detourage => _detourage ??= DetourageSettings.Load(ConfigDir);

    /// <summary>Enregistre les réglages de détourage et les applique sans redémarrer.</summary>
    public void SaveDetourage(DetourageSettings reglages)
    {
        DetourageSettings.Save(ConfigDir, reglages);
        _detourage = reglages;
        AppliquerLeDetourage(reglages);
    }

    /// <summary>
    /// Pose les réglages sur le moteur de détourage.
    ///
    /// <c>Reinitialiser</c> est indispensable : la session ONNX est gardée pour la vie du
    /// processus, et sans elle changer de modèle dans Paramètres n'aurait d'effet qu'au
    /// redémarrage suivant — le réglage passerait pour inopérant.
    /// </summary>
    private static void AppliquerLeDetourage(DetourageSettings reglages)
    {
        BiRefNetMatting.Actif = reglages.Actif;
        BiRefNetMatting.ModelePrefere = reglages.ModeleDemande;
        BiRefNetMatting.Reinitialiser();
    }

    /// <summary>
    /// Le produit « envoi par courriel », créé au catalogue à la première utilisation.
    ///
    /// Passe par le catalogue comme n'importe quel tarif : c'est une ligne de caisse, elle
    /// doit figurer au ticket, au total et aux statistiques (voir <see cref="MailProduct"/>).
    /// </summary>
    public Product ProduitEnvoiCourriel() => MailProduct.Obtenir(Catalog, produit =>
    {
        ProductCatalog.Save(ProductsJson, Catalog.All.Append(produit));
        ReloadCatalog();
    });

    /// <summary>
    /// Le WiFi du magasin, pour le code QR de connexion de l'écran « téléphone ».
    ///
    /// Il est saisi à la main parce que ce poste n'a PAS de carte sans fil : Windows n'a
    /// donc aucun profil à lire. Laissé vide, on retombe sur la lecture automatique, qui
    /// vaudra pour un poste portable.
    /// </summary>
    public required WifiConfig Wifi { get; set; }

    /// <summary>
    /// Enregistre le réseau du magasin et l'applique sans redémarrer.
    ///
    /// L'objet est REMPLACÉ et pas seulement écrit sur le disque : l'écran « téléphone »
    /// lit <see cref="Wifi"/> à chaque affichage, et sans cette ligne le code QR aurait
    /// gardé l'ancien réseau jusqu'au prochain lancement — c'est-à-dire tout l'après-midi.
    /// </summary>
    public void SaveWifi(WifiConfig reglages)
    {
        ArgumentNullException.ThrowIfNull(reglages);

        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(
            Path.Combine(ConfigDir, "wifi.json"),
            JsonSerializer.Serialize(reglages, ProductCatalog.JsonOptions));

        Wifi = reglages;
    }

    /// <summary>
    /// Les impressions en cours. Partagé par toute l'application : c'est lui qui permet
    /// de rendre la main à l'opérateur pendant qu'une commande s'imprime.
    /// </summary>
    public SuiviImpressions Impressions { get; } = new();

    /// <summary>
    /// Ce qu'on a observé de la consommation de chaque machine, pour estimer ce qui reste.
    ///
    /// Convertir un pourcentage d'encre en tirages dépend de la machine et de ce qu'on
    /// imprime : aucune valeur écrite dans le code ne serait juste. On observe donc, et
    /// l'estimation s'affine — voir <see cref="EstimationConsommables"/>.
    /// </summary>
    public Dictionary<string, ObservationMachine> Consommables { get; private set; } = [];

    private string CheminDesConsommables => Path.Combine(ConfigDir, "consommables.json");

    /// <summary>
    /// Le débit mesuré de chaque format : combien de secondes une photo prend, maintenances
    /// comprises. C'est lui qui permet d'annoncer une durée d'attente.
    /// </summary>
    public Dictionary<string, DebitMesure> Debits { get; private set; } = [];

    private string CheminDesDebits => Path.Combine(ConfigDir, "debits.json");

    /// <summary>
    /// Range ce qu'une commande vient d'apprendre sur la cadence d'un format.
    ///
    /// Appelé une fois la commande SORTIE en entier : une commande interrompue a passé du
    /// temps à attendre l'opérateur, pas à imprimer.
    /// </summary>
    public void NoterLeDebit(string format, int tirages, TimeSpan duree)
    {
        if (string.IsNullOrWhiteSpace(format)) return;

        Debits.TryGetValue(format, out var precedent);
        var appris = EstimationDuree.Apprendre(precedent, tirages, duree);

        if (appris is null || appris == precedent) return;

        Debits[format] = appris;
        EstimationDuree.Enregistrer(CheminDesDebits, Debits);

        FileLog.Write($"Cadence mesurée : {format} — {appris.SecondesParTirage:0.0} s par photo " +
                      $"(sur {appris.TiragesMesures} tirages).");
    }

    /// <summary>
    /// Range ce qu'on vient de lire d'une machine, et en tire sa consommation réelle.
    ///
    /// Appelé à chaque rafraîchissement du bandeau : c'est là que passent les relevés, et
    /// ils ne coûtent rien de plus puisqu'on a déjà l'instantané sous la main.
    /// </summary>
    public void NoterLesConsommables(IEnumerable<De100PrinterInfo> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);

        var change = false;

        foreach (var machine in machines)
        {
            if (machine.Supplies is null) continue;

            var cle = machine.MachineId.ToString();
            Consommables.TryGetValue(cle, out var precedent);

            var appris = EstimationConsommables.Apprendre(
                precedent, machine.TotalPrintCount, machine.Supplies, DateTimeOffset.Now);

            if (precedent is not null && appris == precedent) continue;

            Consommables[cle] = appris;
            change = true;
        }

        if (change) EstimationConsommables.Enregistrer(CheminDesConsommables, Consommables);
    }

    private bool _uploadStarted;

    /// <summary>Démarre Kestrel, ouvre le pare-feu et branche l'API bornes, une seule fois.</summary>
    public async Task EnsureUploadServerAsync()
    {
        if (_uploadStarted) return;

        Upload.KioskOrders = new KioskOrderReceiver(Catalog, Orders,
            await Task.Run(() => Store.ScanRecent(days: 3)));
        Upload.KioskOrders.OrderReceived += order =>
        {
            if (!Ticket.Enabled) return;
            try
            {
                EscPosTicket.Send(EscPosTicket.Build(order, Catalog, Ticket), Ticket);
            }
            catch
            {
                // ticket indisponible : la commande est déjà créée, l'opérateur peut réimprimer le ticket
            }
        };

        await Upload.StartAsync();
        _uploadStarted = true;
        // la règle pare-feu peut être lente (netsh) : après coup, hors du fil d'interface —
        // le serveur écoute déjà, seul l'accès depuis le réseau en dépend
        _ = Task.Run(() => Firewall.EnsureRule(Upload.Port));
    }

    /// <summary>Entretien au démarrage (poste opérateur) : archivage des vieilles commandes + sauvegarde si échue.</summary>
    public void RunMaintenanceInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                Archiver.ArchiveOldOrders(
                    Path.Combine(DataRoot, "orders"),
                    Path.Combine(DataRoot, "archive"));
            }
            catch
            {
                // l'archivage réessaiera au prochain démarrage
            }
            try
            {
                BackupRunner.RunIfDue(DataRoot, Backup);
            }
            catch
            {
                // sauvegarde indisponible (NAS éteint…) : réessai au prochain démarrage
            }
        });
    }

    /// <summary>Charge (ou crée avec ses valeurs par défaut) un fichier de config JSON.</summary>
    private static T LoadConfig<T>(string path) where T : new()
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ProductCatalog.JsonOptions) ?? new T();
            }
            catch (JsonException)
            {
                return new T(); // config corrompue : valeurs par défaut, on n'écrase pas le fichier
            }
        }
        var fresh = new T();
        File.WriteAllText(path, JsonSerializer.Serialize(fresh, ProductCatalog.JsonOptions));
        return fresh;
    }

    public static AppServices Load(string dataRoot = @"D:\PhotoStudioData")
    {
        // « models » en fait partie : l'écran Paramètres y renvoie pour poser le modèle de
        // détourage, et un dossier qui n'existe pas se cherche longtemps
        foreach (var sub in new[] { "orders", "catalog", Path.Combine("catalog", "icc"), "counters", "config", "logs", "cache", "incoming", "models", "attente" })
            Directory.CreateDirectory(Path.Combine(dataRoot, sub));

        var productsJson = Path.Combine(dataRoot, "catalog", "products.json");
        if (!File.Exists(productsJson))
            ProductCatalog.Save(productsJson, ProductCatalog.CreateDefaultProducts());

        var catalog = ProductCatalog.Load(productsJson);
        var store = new OrderFolderStore(Path.Combine(dataRoot, "orders"));
        var counter = new DailyCounter(Path.Combine(dataRoot, "counters", "daily.json"));

        MagickInit.Configure();

        var minilab = new De100BridgePrinter { Log = message => FileLog.Write(message) };

        // Les agrandissements journalisaient déjà média, placement et durées — dans le vide,
        // faute d'abonné. C'est la seule trace du temps passé à réduire, convertir et spouler.
        Studio.Printing.LargeFormat.LargeFormatPrinter.Log = message => FileLog.Write(message);

        // Un envoi refusé par le serveur ne laissait aucune trace : le client repartait en
        // croyant avoir ses photos, et on n'avait rien à relire le lendemain.
        PhotoMailer.Log = message => FileLog.Write(message);

        // Le détourage disait dans le vide quel modèle il chargeait, et pourquoi il
        // retombait sur la méthode par couleur — même défaut que LargeFormatPrinter.Log.
        // C'est la seule trace qui permette de comprendre un réglage sans effet.
        BiRefNetMatting.Log = message => FileLog.Write(message);
        BackgroundRemoval.Log = message => FileLog.Write(message);
        PdfPages.Log = message => FileLog.Write(message);

        // L'état de la DNP passe par le spouleur dès que DiLand tient son port USB : c'est
        // la seule trace quand cette lecture-là échoue à son tour.
        Studio.Printing.Devices.Dnp.DnpSpouleur.Log = message => FileLog.Write(message);

        var services = new AppServices
        {
            DataRoot = dataRoot,
            Catalog = catalog,
            Store = store,
            Orders = new OrderService(store, counter),
            // le minilab se connecte à la demande : construire l'objet ne démarre pas le relais
            Minilab = minilab,
            Printer = new PrintOrchestrator(catalog, store, Path.Combine(dataRoot, "catalog"), minilab)
            {
                Log = message => FileLog.Write(message),
            },
            Thumbnails = new ThumbnailService(Path.Combine(dataRoot, "cache")),
            Upload = new UploadServer(Path.Combine(dataRoot, "incoming")),
            Mode = LoadConfig<ModeConfig>(Path.Combine(dataRoot, "config", "mode.json")),
            Ticket = LoadConfig<TicketConfig>(Path.Combine(dataRoot, "config", "ticket.json")),
            Backup = LoadConfig<BackupConfig>(Path.Combine(dataRoot, "config", "backup.json")),
            Wifi = LoadConfig<WifiConfig>(Path.Combine(dataRoot, "config", "wifi.json")),
        };

        // Le modèle de détourage se cherche dans les données du poste, et non à un chemin
        // écrit en dur : un second poste opérateur n'a aucune raison d'avoir le même.
        BiRefNetMatting.DossiersCherches =
        [
            Path.Combine(dataRoot, "models"),
            Path.Combine(AppContext.BaseDirectory, "models"),
        ];

        AppliquerLeDetourage(services.Detourage);

        services.Consommables = EstimationConsommables.Charger(
            Path.Combine(dataRoot, "config", "consommables.json"));
        services.Debits = EstimationDuree.Charger(
            Path.Combine(dataRoot, "config", "debits.json"));

        // Ce que la MACHINE a réellement sorti, par opposition à ce qu'on lui a envoyé.
        // L'événement arrive du relais, donc d'un fil de fond : le suivi, lui, est lu par
        // l'interface — on repasse par le répartiteur avant d'y toucher.
        minilab.JobFinished += (_, resultat) =>
        {
            var reussi = resultat.Outcome == De100JobOutcome.Printed;

            // Le verdict du minilab, ÉCRIT AU JOURNAL — il ne l'était nulle part, et ne
            // servait qu'à rafraîchir le bandeau. Le fichier de la commande 04-007 du
            // 04/08/2026 s'arrête donc à l'envoi : deux tirages sur quatre ne sont jamais
            // sortis, et la machine n'a laissé aucune trace de ce qu'elle en a fait.
            // Sans cette ligne, le prochain incident ne se diagnostiquera pas mieux.
            FileLog.Write(
                $"Minilab : tirage {resultat.JobId} — {Verdict(resultat.Outcome)} " +
                $"(commande {resultat.OrderHandle}) · {resultat.Reason}");

            var repartiteur = System.Windows.Application.Current?.Dispatcher;

            // Le motif voyage jusqu'à l'écran, et pas seulement jusqu'au journal :
            // l'opérateur doit lire pourquoi la machine a refusé sans aller ouvrir un
            // fichier — c'est ce qui a manqué trois fois sur le 21×29,7 du 04/08/2026.
            var motif = reussi ? "" : resultat.Reason;

            if (repartiteur is null)
                services.Impressions.TirageTermine(resultat.JobId, reussi, motif);
            else
                repartiteur.BeginInvoke(() =>
                    services.Impressions.TirageTermine(resultat.JobId, reussi, motif));
        };

        // Ce que la machine DIT d'elle-même : bourrage, fin de rouleau, encre épuisée.
        //
        // Le relais transmettait ces événements depuis toujours, sans un seul abonné. Un
        // tirage refusé se lisait donc « erreur signalée par le minilab », point final,
        // alors que la machine venait d'en donner le motif — c'est ce qui a rendu l'échec
        // des commandes 04-015 et 04-020 du 04/08/2026 inexplicable après coup.
        //
        // Tout va au journal ; seul ce qui ARRIVE et qui est grave monte au bandeau. Un
        // événement qui se termine (IsActive faux) est une panne qui vient d'être réparée :
        // l'annoncer ferait clignoter une alerte pour une bonne nouvelle.
        minilab.MachineEvent += (_, evt) =>
        {
            var etat = evt.IsActive ? "" : " (terminé)";
            FileLog.Write(
                $"Minilab {evt.MachineId} : {evt.Level} {evt.ErrorNumber} — {evt.Message}{etat}");

            if (!evt.IsActive) return;
            if (evt.Level is not (De100ErrorLevel.SystemError or De100ErrorLevel.Error)) return;

            var texte = string.IsNullOrWhiteSpace(evt.Message)
                ? $"Minilab {evt.MachineId} : erreur {evt.ErrorNumber}."
                : $"Minilab {evt.MachineId} : {evt.Message}";

            var repartiteur = System.Windows.Application.Current?.Dispatcher;
            if (repartiteur is null)
                services.Impressions.Informer(texte);
            else
                repartiteur.BeginInvoke(() => services.Impressions.Informer(texte));
        };

        return services;
    }

    /// <summary>Le verdict du minilab, en français, pour le journal.</summary>
    private static string Verdict(De100JobOutcome issue) => issue switch
    {
        De100JobOutcome.Printed => "SORTI",
        De100JobOutcome.Failed => "ÉCHEC",
        De100JobOutcome.Canceled => "ANNULÉ",
        De100JobOutcome.TimedOut => "SANS RÉPONSE",
        _ => issue.ToString(),
    };

    /// <summary>Après modification du catalogue : recharge et recâble l'impression.</summary>
    public void ReloadCatalog()
    {
        Catalog = ProductCatalog.Load(ProductsJson);
        Printer = new PrintOrchestrator(Catalog, Store, CatalogDir) { Log = message => FileLog.Write(message) };

        // la file tient l'ancien orchestrateur : la laisser en place ferait imprimer sur
        // un catalogue périmé
        _attente = null;
    }
}
