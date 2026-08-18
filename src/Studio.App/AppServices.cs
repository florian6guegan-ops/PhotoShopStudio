using System.IO;
using System.Text.Json;
using Studio.Core.Catalog;
using Studio.Core.Cloud;
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

    /// <summary>
    /// Poste identité : plein écran verrouillé sur le parcours des photos d'identité, comme
    /// une borne, mais tourné vers l'opérateur du comptoir plutôt que le client. Sortie vers
    /// le Studio complet par le <see cref="StaffPin"/>.
    /// </summary>
    public bool IsIdentite => Mode.Equals("identite", StringComparison.OrdinalIgnoreCase);

    /// <summary>Les modes plein écran verrouillés : borne client ou poste identité.</summary>
    public bool EstVerrouille => IsKiosk || IsIdentite;
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
        // Le dépôt est CHERCHÉ, il n'est plus écrit en dur : le chemin de la boutique est
        // faux sur tout autre poste (autre disque, autre version, Windows 64 bits), et
        // Studio n'y ouvrait plus une seule commande de borne. Le réglage des paramètres
        // l'emporte quand il est renseigné (voir DiLandLocator).
        new DiLandRepository(
            DiLandLocator.TrouverOuDefaut(Poste.DiLandRacine),
            Path.Combine(DataRoot, "diland")),
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

    private ReglagesIdentite? _identite;

    /// <summary>Les réglages du poste identité — d'où viennent les photos, notamment.</summary>
    public ReglagesIdentite Identite => _identite ??= ReglagesIdentite.Load(ConfigDir);

    /// <summary>Enregistre les réglages du poste identité et les reprend aussitôt.</summary>
    public void SaveIdentite(ReglagesIdentite reglages)
    {
        ReglagesIdentite.Save(ConfigDir, reglages);
        _identite = reglages;
    }

    private DropboxSettings? _dropbox;

    /// <summary>
    /// Réglages de l'envoi des photos au client par Dropbox.
    ///
    /// Ils vivent dans les DONNÉES du poste (<c>config\dropbox.json</c>) et non dans le
    /// dépôt, qui est public : ils portent le jeton du compte, lequel vaut mot de passe.
    /// Voir <see cref="DropboxSettings"/>.
    /// </summary>
    public DropboxSettings Dropbox => _dropbox ??= DropboxSettings.Load(ConfigDir);

    /// <summary>Enregistre les réglages Dropbox et les reprend aussitôt.</summary>
    public void SaveDropbox(DropboxSettings reglages)
    {
        DropboxSettings.Save(ConfigDir, reglages);
        _dropbox = reglages;
    }

    private MarqueSettings? _marque;

    /// <summary>
    /// La marque de la boutique sur la bande basse des planches identité.
    ///
    /// Elle vit dans les données du poste (<c>config\marque.json</c>) et non dans le dépôt :
    /// le logo est un fichier propre à la boutique, et la mention se réécrit sans
    /// recompiler. Voir <see cref="MarqueSettings"/>.
    /// </summary>
    public MarqueSettings Marque => _marque ??= MarqueSettings.Load(ConfigDir);

    /// <summary>Enregistre la marque et l'applique sans redémarrer.</summary>
    public void SaveMarque(MarqueSettings reglages)
    {
        MarqueSettings.Save(ConfigDir, reglages);
        _marque = reglages;
        Printer.Marque = reglages;
    }

    private CorrectionsMachines? _corrections;

    /// <summary>
    /// La compensation d'impression, machine par machine : ce qu'on ajoute au rendu pour
    /// que le papier ressemble à l'écran.
    ///
    /// Dans les données du poste (<c>config\corrections-machines.json</c>) et non dans le
    /// catalogue : elle se mesure sur UNE machine et son rouleau, et n'a aucun sens à
    /// voyager avec les formats et les prix. Voir <see cref="CorrectionMachine"/>.
    /// </summary>
    public CorrectionsMachines Corrections => _corrections ??= CorrectionsMachines.Load(ConfigDir);

    /// <summary>
    /// Enregistre la compensation et l'applique sans redémarrer — l'écran de réglage doit
    /// pouvoir être jugé sur le tirage SUIVANT, pas au prochain lancement.
    /// </summary>
    public void SaveCorrections(CorrectionsMachines corrections)
    {
        CorrectionsMachines.Save(ConfigDir, corrections);
        _corrections = corrections;
        Printer.Corrections = corrections;
    }

    /// <summary>
    /// Les dossiers épinglés dans les boîtes de fichiers et dans le choix du support.
    ///
    /// Ils vivent dans les données du poste : le Bureau et les Téléchargements n'ont pas le
    /// même chemin d'une session à l'autre, et le dossier WeTransfer est celui que
    /// l'exploitant a créé chez lui. Voir <see cref="DossiersFavoris"/>.
    /// </summary>
    public FavorisSettings Favoris { get; private set; } = new();

    /// <summary>Enregistre les favoris et les applique sans redémarrer.</summary>
    public void SaveFavoris(FavorisSettings reglages)
    {
        ArgumentNullException.ThrowIfNull(reglages);

        File.WriteAllText(
            Path.Combine(ConfigDir, "favoris.json"),
            JsonSerializer.Serialize(reglages, ProductCatalog.JsonOptions));

        Favoris = reglages;
        DossiersFavoris.Reglage = reglages;
    }

    /// <summary>
    /// Le prix d'une planche d'identité, qui dépend du DOCUMENT et non du papier :
    /// 10 € pour un document français, 15 € pour un étranger. Voir <see cref="TarifsIdentite"/>.
    /// </summary>
    public TarifsIdentite TarifsIdentite { get; private set; } = new();

    /// <summary>Enregistre les tarifs d'identité et les applique sans redémarrer.</summary>
    public void SaveTarifsIdentite(TarifsIdentite tarifs)
    {
        ArgumentNullException.ThrowIfNull(tarifs);

        File.WriteAllText(
            Path.Combine(ConfigDir, "tarifs-identite.json"),
            JsonSerializer.Serialize(tarifs, ProductCatalog.JsonOptions));

        TarifsIdentite = tarifs;
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

    private PosteSettings? _poste;

    /// <summary>
    /// Ce qui dépend du POSTE : où est DiLand, quelle imprimante joue quel rôle.
    ///
    /// Vide sur une installation neuve, et c'est voulu : tout se détecte seul. Le réglage
    /// n'existe que pour rattraper un poste que la détection ne saurait pas lire — voir
    /// <see cref="PosteSettings"/>.
    /// </summary>
    public PosteSettings Poste => _poste ??= PosteSettings.Load(ConfigDir);

    /// <summary>
    /// Enregistre les réglages du poste.
    ///
    /// <b>Le dépôt DiLand est relâché</b> : il est construit au premier usage à partir du
    /// chemin réglé, et le garder ferait travailler l'application sur l'ancien jusqu'au
    /// prochain démarrage — c'est-à-dire exactement au moment où l'opérateur vient de
    /// corriger un chemin qui ne marchait pas.
    /// </summary>
    public void SavePoste(PosteSettings reglages)
    {
        PosteSettings.Save(ConfigDir, reglages);
        _poste = reglages;
        _diland = null;
    }

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
        BiRefNetMatting.ModelePrefere = ModelePourCetteCarte(reglages);
        BiRefNetMatting.Reinitialiser();
    }

    /// <summary>
    /// Le modèle qu'on demandera VRAIMENT au moteur, une fois la carte de ce poste
    /// consultée.
    ///
    /// <b>Un réglage enregistré survit au seuil qui l'a autorisé.</b> Le choix du modèle
    /// puissant est grisé dans Paramètres quand la carte est trop juste, mais griser
    /// n'efface pas ce qui est déjà dans <c>detourage.json</c> — et le seuil, lui, a été
    /// relevé le 12/08/2026 après l'échec de la GTX 1660 SUPER de Créteil, qui annonce
    /// exactement les 6 Go que l'on exigeait.
    ///
    /// Sans cette conversion, ce poste redemanderait le modèle puissant à chaque séance,
    /// échouerait sur la deuxième planche, et se ferait démoter par
    /// <c>BiRefNetMatting</c> — une planche gâchée et une attente, tous les matins, pour un
    /// verdict connu d'avance.
    /// </summary>
    private static string ModelePourCetteCarte(DetourageSettings reglages)
    {
        if (!reglages.ModelePuissant) return reglages.ModeleDemande;

        // Une carte qui n'annonce pas sa mémoire garde le bénéfice du doute : on ne retire
        // pas un choix sur une absence d'information, et le repli rattrape de toute façon.
        if (CarteGraphique.Principale() is not { MemoireGo: { } go }) return reglages.ModeleDemande;
        if (go >= DetourageSettings.MemoireVideoMinimaleGo) return reglages.ModeleDemande;

        FileLog.Write(
            $"Détourage : « {DetourageSettings.ModelePuissantFichier} » demandé, mais la carte " +
            $"de ce poste n'a que {go:0.#} Go de mémoire vidéo pour " +
            $"{DetourageSettings.MemoireVideoMinimaleGo:0} exigés — " +
            $"« {DetourageSettings.ModeleLeger} » utilisé à sa place.");

        return DetourageSettings.ModeleLeger;
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

    /// <summary>
    /// Où vivent les données de la boutique : catalogue, commandes, journaux, réglages.
    ///
    /// <b>Ce chemin était écrit en dur sur <c>D:</c>.</b> Sur le poste de la boutique il
    /// tombe juste ; sur celui d'un collègue qui n'a qu'un disque C:, la création des
    /// sous-dossiers lève, et l'application s'arrête sur « Impossible de démarrer » —
    /// avant d'avoir montré quoi que ce soit. Vu en préparant la version 1.1.0, le
    /// 06/08/2026, alors qu'on s'apprêtait à la distribuer.
    ///
    /// L'ordre compte : la variable d'environnement l'emporte (elle permet de déplacer les
    /// données sans recompiler), puis l'emplacement historique de la boutique — pour que ce
    /// poste-là ne change pas d'un pouce — et à défaut le dossier de l'utilisateur, qui
    /// existe partout et où l'on a toujours le droit d'écrire.
    /// </summary>
    public static string RacineDonneesParDefaut()
    {
        var declare = Environment.GetEnvironmentVariable("STUDIO_DATA");
        if (!string.IsNullOrWhiteSpace(declare)) return declare;

        foreach (var candidat in new[]
                 {
                     @"D:\PhotoStudioData",
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "StudioPhoto", "data"),
                 })
        {
            // On ne se contente pas de regarder si le disque existe : un D: qui serait un
            // lecteur optique ou une clef protégée passerait le test et échouerait ensuite.
            // Le seul essai qui prouve quelque chose est d'écrire.
            try
            {
                Directory.CreateDirectory(candidat);
                return candidat;
            }
            catch (Exception)
            {
                // candidat suivant
            }
        }

        // Les deux ont échoué : on rend le dossier utilisateur quand même, pour que le
        // message d'erreur nomme un chemin plausible plutôt qu'un disque inexistant.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StudioPhoto", "data");
    }

    public static AppServices Load(string? dataRoot = null)
    {
        dataRoot ??= RacineDonneesParDefaut();

        // « models » en fait partie : l'écran Paramètres y renvoie pour poser le modèle de
        // détourage, et un dossier qui n'existe pas se cherche longtemps
        foreach (var sub in new[] { "orders", "catalog", Path.Combine("catalog", "icc"), "counters", "config", "logs", "cache", "incoming", "models", "attente" })
            Directory.CreateDirectory(Path.Combine(dataRoot, sub));

        // Un poste NEUF prend le catalogue livré avec l'application — celui de la boutique,
        // avec ses formats, ses prix, ses canaux de machine et ses réglages pilote. Les
        // produits d'amorçage ne sont qu'un dernier recours : quatre des cinq pointent sur
        // « Microsoft Print to PDF », ce qui donne un logiciel qui démarre et dont rien ne
        // sort. C'est exactement ce qu'a eu le poste de Créteil le 07/08/2026.
        // Toute la décision est DANS AssurerUnCatalogue, et rien ici : l'enchaîner sur
        // place — « si le fichier manque ET que la pose échoue, alors amorçage » — court-
        // circuitait la reprise dès qu'un catalogue existait, donc précisément quand elle
        // sert. C'est ce qui a laissé Créteil sur ses cinq produits en 1.3.2.
        var productsJson = Path.Combine(dataRoot, "catalog", "products.json");
        CatalogueLivre.AssurerUnCatalogue(Path.Combine(dataRoot, "catalog"));

        // Les profils ICC que le catalogue nomme sont déjà sur le poste, posés par les
        // pilotes : on les recopie là où il les cherche. Sans cela, la planche d'identité
        // de Créteil est partie sans gestion couleur pendant toute son installation, et
        // rien ne le disait — un profil manquant ne fait pas échouer l'impression.
        var profils = CatalogueLivre.ImporterLesProfilsManquants(
            Path.Combine(dataRoot, "catalog"), IccProfiles.WindowsColorDir);

        if (profils.Count > 0)
            FileLog.Write($"Profils ICC repris de Windows : {string.Join(", ", profils)}");

        var catalog = ProductCatalog.Load(productsJson);

        // LE CATALOGUE EST RELU À CHAQUE DÉMARRAGE, et ses cotes avec lui.
        //
        // `CotesProduit` ne parlait qu'à la SAISIE. Un produit enregistré de travers avant
        // qu'elle existe — ou saisi sur un poste qui n'avait pas encore la version — ne
        // rencontrait plus jamais son garde-fou. Le poste DESKTOP-KT88VDM en portait deux
        // depuis des semaines : un « 40×50 » de 40 × 50 mm et une « E-PHOTO » de 10 × 15,
        // qui sortaient des timbres (12/08/2026).
        //
        // On ne corrige rien : le catalogue appartient à l'exploitant. Mais c'est écrit au
        // journal, donc emporté par le rapport de diagnostic — c'est ce qui manquait pour
        // le voir à distance, et c'est là que ça se lit en dix secondes.
        foreach (var anomalie in catalog.All
                     .Select(p => CotesProduit.Anomalie(p.Name, p.Code, p.WidthMm, p.HeightMm))
                     .Where(a => a is not null))
            FileLog.Write($"Catalogue, cotes douteuses : {anomalie}");

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

        // Même raison pour Dropbox : un lien créé sans expiration parce que le compte est
        // gratuit, ou un envoi coupé en route, ne se relisent nulle part ailleurs.
        Studio.Web.Dropbox.DropboxAuth.Log = message => FileLog.Write(message);
        Studio.Web.Dropbox.DropboxClient.Log = message => FileLog.Write(message);
        Studio.Web.Dropbox.DropboxTransfer.Log = message => FileLog.Write(message);
        Studio.Web.Dropbox.DropboxMenage.Log = message => FileLog.Write(message);

        // Le détourage disait dans le vide quel modèle il chargeait, et pourquoi il
        // retombait sur la méthode par couleur — même défaut que LargeFormatPrinter.Log.
        // C'est la seule trace qui permette de comprendre un réglage sans effet.
        BiRefNetMatting.Log = message => FileLog.Write(message);
        BackgroundRemoval.Log = message => FileLog.Write(message);
        ImagePipeline.Log = message => FileLog.Write(message);
        DevMode.Log = message => FileLog.Write(message);
        PdfPages.Log = message => FileLog.Write(message);

        // La correction des yeux rouges est demandée depuis le PIPELINE, qui reçoit une
        // image et des réglages : il n'a aucun moyen de connaître le chemin du modèle ONNX.
        // Le détecteur lui est donc posé ici, comme le reste des points d'entrée statiques.
        YeuxRouges.Log = message => FileLog.Write(message);

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
                // Avertir est branché plus bas : il lui faut « services », qu'on est en
                // train de construire.
            },
            Thumbnails = new ThumbnailService(Path.Combine(dataRoot, "cache")),
            Upload = new UploadServer(Path.Combine(dataRoot, "incoming")),
            Mode = LoadConfig<ModeConfig>(Path.Combine(dataRoot, "config", "mode.json")),
            Ticket = LoadConfig<TicketConfig>(Path.Combine(dataRoot, "config", "ticket.json")),
            Backup = LoadConfig<BackupConfig>(Path.Combine(dataRoot, "config", "backup.json")),
            Wifi = LoadConfig<WifiConfig>(Path.Combine(dataRoot, "config", "wifi.json")),
            Favoris = LoadConfig<FavorisSettings>(Path.Combine(dataRoot, "config", "favoris.json")),
            TarifsIdentite = LoadConfig<TarifsIdentite>(
                Path.Combine(dataRoot, "config", "tarifs-identite.json")),
        };

        // Ce que l'orchestrateur veut faire savoir à l'opérateur PENDANT qu'il travaille —
        // aujourd'hui la finition qui ne correspond pas au rouleau chargé.
        services.Printer.Avertir = message => AvertirLOperateur(services, message);

        // Les boîtes de fichiers de Windows n'ont pas de service à qui demander : elles
        // s'ouvrent depuis n'importe quel écran. Le réglage leur est donc posé ici, une fois.
        DossiersFavoris.Reglage = services.Favoris;

        // Le modèle de détourage se cherche dans les données du poste, et non à un chemin
        // écrit en dur : un second poste opérateur n'a aucune raison d'avoir le même.
        BiRefNetMatting.DossiersCherches =
        [
            Path.Combine(dataRoot, "models"),
            Path.Combine(AppContext.BaseDirectory, "models"),
        ];

        // Le détecteur de visages sert aussi aux YEUX ROUGES, demandés depuis le pipeline.
        // Construire l'objet ne charge pas le réseau — il vérifie seulement que le fichier
        // ONNX est là. Sans modèle sur le poste, la case reste simplement sans effet : un
        // tirage ne doit pas échouer parce qu'elle a été cochée.
        try
        {
            YeuxRouges.Detecteur = services.Faces;
        }
        catch (FileNotFoundException ex)
        {
            FileLog.Write($"Yeux rouges : modèle de détection absent ({ex.Message}) — " +
                          "la correction restera sans effet.");
        }

        AppliquerLeDetourage(services.Detourage);

        // la marque suit l'orchestrateur, qui compose les planches sans connaître le dossier
        // de configuration
        services.Printer.Marque = services.Marque;

        // et la compensation d'impression avec elle, pour la même raison : l'atelier ne lit
        // pas config/, et une lecture de fichier par tirage coûterait un accès disque au
        // milieu d'un rendu
        services.Printer.Corrections = services.Corrections;

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

    /// <summary>
    /// Pose un avertissement dans le bandeau des impressions, depuis n'importe quel fil.
    ///
    /// Le saut vers le fil de l'interface n'est pas une précaution de principe : un tirage
    /// s'exécute en tâche de fond, et c'est de là que l'avertissement part.
    ///
    /// Le bandeau plutôt qu'une boîte modale, pour la même raison qu'ailleurs : une boîte
    /// barre l'écran et force une réponse immédiate, quand l'opérateur a justement besoin
    /// de regarder ce qui sort de la machine avant de décider.
    /// </summary>
    private static void AvertirLOperateur(AppServices services, string message)
    {
        var repartiteur = System.Windows.Application.Current?.Dispatcher;
        if (repartiteur is null)
            services.Impressions.Informer(message);
        else
            repartiteur.BeginInvoke(() => services.Impressions.Informer(message));
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

        // <b>Le minilab se repasse, sinon le DE100 devient inaccessible.</b> Il était
        // oublié ici : toucher au catalogue reconstruisait un orchestrateur SANS lui, et
        // toute impression minilab échouait ensuite sur « le relais 32 bits n'a pas été
        // fourni » — jusqu'au redémarrage de l'application, ce qui rendait la panne
        // incompréhensible. Créteil, commande 10-024, 10/08/2026.
        //
        // Le relais, lui, tournait : c'est l'ORCHESTRATEUR qui ne savait plus où le
        // joindre. Ne pas se fier à la présence du processus pour écarter cette cause.
        Printer = new PrintOrchestrator(Catalog, Store, CatalogDir, Minilab)
        {
            Log = message => FileLog.Write(message),
            // le nouvel orchestrateur repart nu : sans ce report, les planches perdraient
            // leur bande dès qu'on touche au catalogue
            Marque = Marque,
            // et pour la compensation d'impression : sans ce report, changer un prix au
            // Catalogue rendrait les tirages plus sombres qu'à la commande précédente,
            // sans qu'aucun réglage n'ait bougé
            Corrections = Corrections,
            // idem pour l'avertissement : un orchestrateur muet laisserait partir un
            // tirage sur le mauvais rouleau sans que personne n'en sache rien
            Avertir = message => AvertirLOperateur(this, message),
        };

        // la file tient l'ancien orchestrateur : la laisser en place ferait imprimer sur
        // un catalogue périmé
        _attente = null;
    }
}
