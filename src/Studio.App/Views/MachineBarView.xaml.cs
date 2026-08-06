using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Studio.App.Infrastructure;
using Studio.Printing;
using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;

namespace Studio.App.Views;

/// <summary>
/// Bandeau permanent des machines, repris de DiLand : chaque imprimante avec son papier,
/// ses jauges d'encre et son état, visible depuis n'importe quel écran.
///
/// C'est le seul endroit où l'opérateur voit venir une panne de consommable sans avoir
/// à aller la chercher — et une encre vide au mauvais moment coûte une commande. C'est
/// aussi là que se lit l'avancement d'une commande, sur la tuile de la machine qui la
/// tire, avec de quoi l'arrêter.
/// </summary>
public partial class MachineBarView : UserControl
{
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Cadence de surveillance de DiLand. Bien plus courte que <see cref="Periode"/> parce
    /// qu'elle ne coûte qu'une énumération de processus — aucun accès machine — et que
    /// l'opérateur qui ferme DiLand pour récupérer la DNP ne doit pas attendre deux minutes
    /// devant un bandeau qui ne la montre pas encore.
    /// </summary>
    private static readonly TimeSpan PeriodeDiLand = TimeSpan.FromSeconds(5);

    private const int SeuilAlerte = 25;

    private readonly DispatcherTimer _minuteur;
    private readonly DispatcherTimer _guetteurDiLand;

    /// <summary>DiLand tournait-il au dernier coup d'œil ? Sert à détecter la bascule.</summary>
    private bool _dilandTournait;

    /// <summary>
    /// Cadence des reprises de commandes en attente.
    ///
    /// Assez court pour que l'opérateur qui referme le capot voie repartir son tirage sans
    /// se demander s'il doit recliquer, assez long pour ne pas harceler une machine en
    /// panne — la file ne relance de toute façon que si l'imprimante se déclare prête.
    /// </summary>
    private static readonly TimeSpan PeriodeAttente = TimeSpan.FromSeconds(20);

    private readonly DispatcherTimer _minuteurAttente;

    /// <summary>Dernier état lu des machines : on le rejoue quand seul l'avancement change.</summary>
    private List<MachineTile> _tuiles = [];

    public MachineBarView()
    {
        InitializeComponent();

        _minuteur = new DispatcherTimer { Interval = Periode };
        _minuteur.Tick += async (_, _) => await RefreshAsync();

        _guetteurDiLand = new DispatcherTimer { Interval = PeriodeDiLand };
        _guetteurDiLand.Tick += async (_, _) => await SurveillerDiLand();

        _minuteurAttente = new DispatcherTimer { Interval = PeriodeAttente };
        _minuteurAttente.Tick += async (_, _) => await ReprendreLesAttentes();

        Loaded += async (_, _) =>
        {
            _dilandTournait = DiLandPresence.IsRunning();
            _minuteur.Start();
            _guetteurDiLand.Start();
            _minuteurAttente.Start();
            BrancherSuivi();
            await RefreshAsync();
            await ReprendreLesAttentes();   // une commande peut attendre depuis hier soir
        };
        Unloaded += (_, _) =>
        {
            _minuteur.Stop();
            _guetteurDiLand.Stop();
            _minuteurAttente.Stop();
            DebrancherSuivi();
        };
    }

    /// <summary>
    /// Relance les commandes que l'imprimante avait fait attendre.
    ///
    /// Le message reste affiché : une commande repartie toute seule pendant que
    /// l'opérateur avait le dos tourné doit se voir, sinon il la croit perdue et la
    /// relance à la main — donc en double.
    /// </summary>
    private async Task ReprendreLesAttentes()
    {
        try
        {
            var reprises = await App.Services.Attente.TryResumeAsync();
            if (reprises.Count > 0)
            {
                MessageText.Text = string.Join("  ·  ", reprises.Select(r => r.Message));
                await RefreshAsync();
                return;
            }

            // rien n'est reparti : on annonce ce qui patiente encore
            var enAttente = App.Services.Attente.Count;
            if (enAttente > 0 && !App.Services.Impressions.Actif)
                MessageText.Text = enAttente == 1
                    ? "1 commande attend que l'imprimante soit prête — elle partira toute seule."
                    : $"{enAttente} commandes attendent que l'imprimante soit prête — elles partiront toutes seules.";
        }
        catch (Exception ex)
        {
            FileLog.Write("Reprise des commandes en attente impossible", ex);
        }
    }

    /// <summary>
    /// Ouvre ou ferme la tuile DNP en suivant DiLand.
    ///
    /// DiLand tient le port USB de la DS620 en exclusif : tant qu'il tourne, la machine
    /// est injoignable et n'a rien à faire dans le bandeau ; dès qu'il se ferme, elle
    /// redevient interrogeable. On ne relit les machines qu'au moment où l'état CHANGE —
    /// relire à chaque tic coûterait un aller-retour par le relais toutes les cinq secondes.
    /// </summary>
    private async Task SurveillerDiLand()
    {
        var tourne = DiLandPresence.IsRunning();
        if (tourne == _dilandTournait) return;

        _dilandTournait = tourne;
        FileLog.Write(tourne
            ? "DiLand ouvert : la DNP passe hors de portée, retrait du bandeau."
            : "DiLand fermé : la DNP redevient interrogeable.");

        await RefreshAsync();
    }

    // ----- avancement des impressions -----

    /// <summary>
    /// Le bandeau se redessine à chaque pas d'une impression.
    ///
    /// On ne relit PAS les machines pour autant : interroger le minilab prend des
    /// centaines de millisecondes et passe par le relais 32 bits — le faire à chaque
    /// tirage ferait ramer le poste au pire moment. On rejoue les tuiles déjà connues en
    /// n'y remettant que l'avancement.
    /// </summary>
    private void BrancherSuivi()
    {
        var suivi = App.Services.Impressions;
        suivi.PropertyChanged += OnSuiviChanged;
        ((System.Collections.Specialized.INotifyCollectionChanged)suivi.Travaux)
            .CollectionChanged += OnTravauxChanged;

        foreach (var travail in suivi.Travaux) travail.PropertyChanged += OnTravailChanged;
    }

    private void DebrancherSuivi()
    {
        var suivi = App.Services.Impressions;
        suivi.PropertyChanged -= OnSuiviChanged;
        ((System.Collections.Specialized.INotifyCollectionChanged)suivi.Travaux)
            .CollectionChanged -= OnTravauxChanged;

        foreach (var travail in suivi.Travaux) travail.PropertyChanged -= OnTravailChanged;
    }

    private void OnSuiviChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(Reafficher);

    private void OnTravailChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(Reafficher);

    private void OnTravauxChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (TravailImpression travail in e.OldItems ?? Array.Empty<object>())
            travail.PropertyChanged -= OnTravailChanged;
        foreach (TravailImpression travail in e.NewItems ?? Array.Empty<object>())
            travail.PropertyChanged += OnTravailChanged;

        Dispatcher.Invoke(Reafficher);
    }

    /// <summary>
    /// Relit l'état des machines. Appelable après une impression.
    ///
    /// <b>Une lecture qui échoue ne fait JAMAIS disparaître une machine.</b> Chaque famille
    /// est relue de son côté ; si l'une ne répond pas, on garde ce qu'on savait d'elle et
    /// l'on recompose la barre entière. Les deux DE100 s'évanouissaient du bandeau dès que
    /// le relais toussait : le minilab échouait, sa liste restait vide, et la DNP écrasait
    /// ensuite l'ensemble des tuiles avec la sienne. Constaté le 04/08/2026 en réimprimant
    /// la commande 04-040.
    /// </summary>
    public async Task RefreshAsync()
    {
        var fujis = _dernieresFuji;
        var dnps = _dernieresDnp;

        // Le minilab d'abord : il répond vite et de façon fiable. La DNP est interrogée
        // ensuite et séparément — son SDK peut rester bloqué quand DiLand tient le port
        // USB, et ce blocage ne doit pas effacer les machines déjà connues.
        try
        {
            var lues = await App.Services.Minilab.SnapshotAsync();

            // le relevé sert aussi à APPRENDRE la consommation de chaque machine : il ne
            // coûte rien de plus, l'instantané est déjà là
            App.Services.NoterLesConsommables(lues);

            fujis = [.. lues.Select(f => new MachineTile(f))];
            _dernieresFuji = fujis;
            MessageText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write("Bandeau : minilab indisponible", ex);
            MessageText.Text = fujis.Count > 0
                ? "Minilab injoignable pour l'instant — le bandeau montre son dernier état connu."
                : "Minilab injoignable — les tirages restent possibles si la machine répond.";
        }

        try
        {
            // <b>On ne DEMANDE PLUS RIEN au relais quand DiLand tourne.</b>
            //
            // DiLand tient le port USB des DNP en exclusif : le SDK ne peut pas répondre, et
            // le relais 32 bits meurt d'essayer — la sérialisation de l'instantané DNP fait
            // configurer par réflexion, en pleine course, des types que son moteur
            // d'exécution ne supporte pas en 32 bits (« Fatal error. Internal CLR error. »,
            // 06/08/2026 à 12:29:44, moins d'une seconde après son démarrage).
            //
            // Le relais emportait alors le MINILAB avec lui — « Pipe is broken », et plus
            // aucun tirage DE100 ne partait. Une tuile d'état ne vaut pas une machine à
            // l'arrêt : quand DiLand est là, on lit le spouleur, qui répond toujours.
            var lues = DiLandPresence.IsRunning()
                ? []
                : await App.Services.Minilab.DnpSnapshotAsync();

            // Le SDK n'en découvre aucune : la machine dort peut-être, ou DiLand la tient. On
            // complète d'après le spouleur Windows pour l'afficher plutôt que de la faire
            // disparaître. Cette lecture-là se fait ICI et jamais dans le relais — voir
            // DiLandPresence.VuesParWindows.
            if (lues.Count == 0)
                lues = [.. DiLandPresence.VuesParWindows()];

            dnps = [.. lues.Select(d => new MachineTile(d))];
            _dernieresDnp = dnps;
        }
        catch (Exception ex)
        {
            // silencieux à l'écran : les machines Fuji restent affichées
            FileLog.Write("Bandeau : imprimante DNP indisponible", ex);
        }

        _tuiles = [.. fujis, .. dnps];
        Reafficher();
    }

    /// <summary>
    /// Le dernier état CONNU de chaque famille de machines.
    ///
    /// Gardé pour qu'une lecture ratée n'efface pas la tuile : une machine qu'on n'arrive
    /// pas à joindre pendant dix secondes n'a pas disparu de la boutique, et l'opérateur a
    /// besoin de la voir — ne serait-ce que pour savoir qu'elle existe.
    /// </summary>
    private List<MachineTile> _dernieresFuji = [];

    private List<MachineTile> _dernieresDnp = [];

    /// <summary>
    /// Repose les tuiles avec l'avancement du moment. Les travaux dont on ne connaît pas
    /// encore la machine — la préparation des photos précède le choix — s'affichent au
    /// centre du bandeau, faute de tuile où les mettre.
    /// </summary>
    private void Reafficher()
    {
        var suivi = App.Services.Impressions;

        foreach (var tuile in _tuiles) tuile.Travail = suivi.PourMachine(tuile.Lettre);

        MachinesList.ItemsSource = null;
        MachinesList.ItemsSource = _tuiles;

        var orphelins = suivi.SansMachine;
        if (orphelins.Count > 0)
        {
            var premier = orphelins[0];
            MessageText.Text = $"Commande {premier.Numero} — {premier.Etape} {premier.Detail}";
        }
        else if (!suivi.Actif)
        {
            MessageText.Text = "";
        }
    }

    /// <summary>Arrête la commande qui sort sur cette machine.</summary>
    private void OnCancelJob(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string lettre) return;
        if (App.Services.Impressions.PourMachine(lettre) is not { } travail) return;

        // Une confirmation, une seule, parce que le geste est irréversible dans un sens :
        // ce que la machine a déjà tiré ne se reprend pas.
        var reponse = MessageBox.Show(
            $"Arrêter la commande {travail.Numero} ?\n\n" +
            "Les tirages pas encore envoyés ne partiront pas, et ceux déjà transmis au " +
            "minilab lui seront rappelés. Ceux qu'il a déjà sortis resteront sortis.",
            "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (reponse == MessageBoxResult.Yes) travail.Annuler();
    }

    /// <summary>
    /// Vide la file Windows d'une imprimante bloquée.
    ///
    /// <b>Le geste de dernier recours.</b> Le 04/08/2026, trois travaux sont restés deux
    /// heures dans la file de la DS620 sans jamais imprimer une page — dont deux venus de
    /// DiLand. La machine se déclarait prête, aucune erreur n'était signalée, et rien ne
    /// sortait : il fallait passer par les fenêtres d'impression de Windows.
    ///
    /// Une confirmation, une seule, parce que ce qui est supprimé ne revient pas — et elle
    /// dit COMBIEN de photos ne sortiront pas, seul chiffre qui compte pour décider.
    /// </summary>
    private void OnPurgerLaFile(object sender, RoutedEventArgs e)
    {
        // Le bouton porte sa PROPRE tuile en DataContext : la retrouver par sa lettre dans
        // `_tuiles` la manquait dès que la barre avait été recomposée entre-temps — et le
        // bouton ne faisait alors rien du tout, sans un mot.
        if ((sender as FrameworkElement)?.DataContext is not MachineTile tuile)
        {
            FileLog.Write("Vider la file : la tuile de la machine n'a pas été retrouvée.");
            return;
        }

        if (tuile.NomDeFile.Length == 0)
        {
            FileLog.Write($"Vider la file : la machine « {tuile.Nom} » n'a pas de file Windows.");
            return;
        }

        var reponse = MessageBox.Show(
            $"Vider la file de « {tuile.NomDeFile} » ?\n\n" +
            $"{tuile.PagesEnFile} photo(s) attendent d'être imprimées : elles ne sortiront " +
            "pas, et ce qui est supprimé ne revient pas.\n\n" +
            "À faire quand la machine ne sort plus rien alors qu'elle se déclare prête. " +
            "Les tirages perdus se refont depuis « Commandes du jour ».",
            "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (reponse != MessageBoxResult.Yes) return;

        var supprimes = DnpSpouleur.Vider(tuile.NomDeFile);

        MessageText.Text = supprimes >= 0
            ? $"File de « {tuile.NomDeFile} » vidée — {supprimes} travail/travaux supprimé(s)."
            : $"La file de « {tuile.NomDeFile} » n'a pas répondu. Voir le journal.";

        _ = RefreshAsync();
    }

    // ----- estimation de ce qui reste -----

    /// <summary>
    /// Le format sur lequel porte l'estimation : celui qu'on S'APPRÊTE À TIRER quand un
    /// produit est choisi, sinon le premier format fixe du rouleau.
    ///
    /// « ~576 × 10x15 » annoncé à quelqu'un qui lance des A4 ne lui apprend rien, et c'est
    /// pourtant ce que le bandeau affichait.
    /// </summary>
    private static De100Format? FormatVise(De100PrinterInfo info)
    {
        if (info.Media is not { } media) return null;

        // le produit retenu pour la session, s'il y en a un et s'il sort du minilab
        if (App.Services.Printer.DernierFormatMinilab is { } demande)
        {
            var correspondant = De100Formats.ForPaperWidth(media.PaperWidthMm)
                .FirstOrDefault(f => f.Name.Equals(demande, StringComparison.OrdinalIgnoreCase));
            if (correspondant is not null) return correspondant;
        }

        return info.Formats.FirstOrDefault(f => !f.Format.IsVariable)?.Format
               ?? De100Formats.ForPaperWidth(media.PaperWidthMm).FirstOrDefault(f => !f.IsVariable);
    }

    /// <summary>Ce qu'on a appris de la consommation de cette machine.</summary>
    private static ObservationMachine? ObservationDe(char machine) =>
        App.Services.Consommables.TryGetValue(machine.ToString(), out var vue) ? vue : null;

    private sealed record InkGauge(string Info, Brush Couleur, double Hauteur);

    private sealed class MachineTile
    {
        public MachineTile(De100PrinterInfo info)
        {
            Lettre = info.MachineId.ToString();
            Nom = info.Model;
            // l'état ET le geste : voir ConduiteMachine
            Etat = ConduiteMachine.PourLeMinilab(info.Status).Message;

            var horsLigne = info.Status == De100PrinterStatus.Offline;
            if (horsLigne)
            {
                Papier = "";
                Restant = "";
                Encres = [];
                Fond = Couleur(info.Status, alerte: false);
                return;
            }

            Papier = info.Media is { } media
                ? $"{media.PaperWidthMm} mm · {media.Surface}"
                : "papier inconnu";

            // L'estimation porte sur le format QU'ON VA TIRER quand on le connaît, et non
            // sur le premier de la liste : annoncer « 576 × 10x15 » à quelqu'un qui lance
            // des A4 ne lui apprend rien. Et elle compte les ENCRES et le bac, pas
            // seulement le papier — voir EstimationConsommables.
            var vise = FormatVise(info);
            Restant = info.Media is { } m
                ? $"{m.PaperRemainingMm / 1000:0.0} m" +
                  (vise is null
                      ? ""
                      : " · " + EstimationConsommables
                          .Pour(vise, m, info.Supplies, ObservationDe(info.MachineId))
                          .Resume(vise.Name))
                : "";

            Encres = info.Supplies is { } s
                ?
                [
                    Jauge("Cyan", s.Cyan.Level, Color.FromRgb(0x00, 0xB0, 0xD0)),
                    Jauge("Magenta", s.Magenta.Level, Color.FromRgb(0xD8, 0x1B, 0x8C)),
                    Jauge("Jaune", s.Yellow.Level, Color.FromRgb(0xF2, 0xC4, 0x1D)),
                    Jauge("Noir", s.Black.Level, Color.FromRgb(0x22, 0x22, 0x22)),
                    Jauge(s.MaintenanceTank.Name, s.MaintenanceTank.Level, Color.FromRgb(0xB0, 0xBE, 0xC5)),
                ]
                : [];

            var alerte = info.Supplies?.InksBelow(SeuilAlerte).Any() == true;
            if (alerte) Etat += " · encre faible";
            Fond = Couleur(info.Status, alerte);
        }

        /// <summary>
        /// Une imprimante DNP. Elle n'a pas d'encres séparées — le ruban et le papier
        /// s'épuisent ensemble — donc pas de jauges : c'est le nombre de tirages restants
        /// qui compte, comme l'affiche DiLand.
        /// </summary>
        public MachineTile(Studio.Printing.Devices.Dnp.DnpPrinterInfo info)
        {
            Lettre = "D";
            Encres = [];

            // Machine muette au SDK. Deux raisons très différentes, et c'est le SPOULEUR
            // qui les départage : soit elle dort vraiment, soit DiLand tient le port USB —
            // ce qui est le cas presque en permanence en boutique. Afficher « en veille »
            // dans les deux cas, c'est ce qui la déclarait endormie pendant qu'elle tirait.
            if (info.EndormieOuInjoignable)
            {
                Nom = info.WindowsQueueName!;

                if (info.Spouleur is { } file && info.VueParLeSpouleur)
                {
                    // l'état ET le geste à faire : « Intervention nécessaire » tout seul
                    // n'a jamais dit à personne quoi toucher
                    Etat = ConduiteMachine.PourLaFile(file.Etat, file.PhotosRestantes).Message;
                    Papier = "consommables lisibles seulement DiLand fermé";
                    Restant = file.PhotosRestantes > 0
                        ? $"{file.PhotosRestantes} photo(s) à sortir"
                        : "rien dans la file";
                    Fond = CouleurSpouleur(file.Etat);
                    NomDeFile = file.Nom;
                    PagesEnFile = file.PhotosRestantes;
                    return;
                }

                Etat = "En veille";
                Papier = "consommables inconnus tant qu'elle dort";
                Restant = "elle se réveille au premier tirage";
                Fond = Pinceau(0x37, 0x47, 0x4F);
                return;
            }

            Nom = string.IsNullOrWhiteSpace(info.SerialNumber) ? "DNP" : $"DNP {info.SerialNumber}";
            Papier = $"{DecrireMedia(info.MediaSize)} · {info.MediaClass}";

            var pourcent = info.MediaRemainingPercent is { } pc ? $" ({pc:0} %)" : "";
            Restant = $"{info.MediaRemaining} tirages restants{pourcent}";

            Etat = info.Status.IsCommunicationFailure ? "Hors ligne"
                : info.Status.IsFault ? "Erreur"
                : info.Status.NeedsOperator ? "Intervention nécessaire"
                : info.Status.IsBusy ? "En cours d'impression"
                : info.Status.IsReady ? "Prête"
                : info.Status.Message;

            var alerte = info.Status.NeedsOperator || info.Status.IsFault || info.MediaRemaining <= 20;
            Fond = info.Status.IsCommunicationFailure
                ? Pinceau(0x4A, 0x4A, 0x4A)
                : info.Status.IsFault ? Pinceau(0xB3, 0x26, 0x1E)
                : alerte ? Pinceau(0x8A, 0x62, 0x0E)
                : info.Status.IsBusy ? Pinceau(0x1B, 0x5E, 0x8A)
                : Pinceau(0x2E, 0x6B, 0x33);
        }

        /// <summary>
        /// Le fond d'une DNP vue par le seul spouleur — les MÊMES couleurs que le minilab,
        /// pour que l'état se lise d'un coup d'œil sans avoir à savoir de quelle machine il
        /// s'agit : vert prête, bleu en train de tirer, orangé en attente d'un geste,
        /// rouge en panne, gris hors ligne.
        /// </summary>
        private static Brush CouleurSpouleur(EtatFileDnp etat) => etat switch
        {
            EtatFileDnp.Prete => Pinceau(0x2E, 0x6B, 0x33),
            EtatFileDnp.Impression => Pinceau(0x1B, 0x5E, 0x8A),
            EtatFileDnp.EnPause => Pinceau(0x8A, 0x62, 0x0E),
            EtatFileDnp.Erreur => Pinceau(0xB3, 0x26, 0x1E),
            EtatFileDnp.HorsLigne => Pinceau(0x4A, 0x4A, 0x4A),
            _ => Pinceau(0x37, 0x47, 0x4F),
        };

        /// <summary>
        /// L'état de la machine en toutes lettres.
        ///
        /// La couleur seule ne dit pas tout : « en veille » et « prête » sont deux
        /// situations différentes, et un fond bleu ne l'explique à personne. Un tirage
        /// envoyé à une machine en veille attend qu'elle se réveille, ce qui ressemble
        /// beaucoup à une machine en panne quand on ne le sait pas.
        /// </summary>
        private static string DecrireEtat(De100PrinterStatus status) => status switch
        {
            De100PrinterStatus.Offline => "Hors ligne",
            De100PrinterStatus.Ready => "Prête",
            De100PrinterStatus.Busy => "Occupée",
            De100PrinterStatus.Printing => "En cours d'impression",
            De100PrinterStatus.Sleep => "En veille",
            De100PrinterStatus.ErrorProcessingCanBeContinued => "Erreur — reprise possible",
            De100PrinterStatus.ErrorProcessingCannotBeContinued => "Erreur — arrêtée",
            _ => status.ToString(),
        };

        /// <summary>
        /// Le fond de la tuile suit l'état, et l'alerte consommable ne l'emporte que sur
        /// une machine par ailleurs saine : une machine en erreur doit rester rouge, même
        /// si son cyan est aussi bas.
        /// </summary>
        private static Brush Couleur(De100PrinterStatus status, bool alerte) => status switch
        {
            De100PrinterStatus.Offline => Pinceau(0x4A, 0x4A, 0x4A),
            De100PrinterStatus.Sleep => Pinceau(0x37, 0x47, 0x4F),
            De100PrinterStatus.Printing => Pinceau(0x1B, 0x5E, 0x8A),
            De100PrinterStatus.Busy => Pinceau(0x1B, 0x5E, 0x8A),
            De100PrinterStatus.ErrorProcessingCanBeContinued => Pinceau(0x8A, 0x62, 0x0E),
            De100PrinterStatus.ErrorProcessingCannotBeContinued => Pinceau(0xB3, 0x26, 0x1E),
            _ => alerte ? Pinceau(0x8A, 0x62, 0x0E) : Pinceau(0x2E, 0x6B, 0x33),
        };

        private static SolidColorBrush Pinceau(byte r, byte v, byte b) =>
            new(Color.FromRgb(r, v, b));

        /// <summary>Nom lisible d'un format DNP : « Size6x4 » ne parle à personne.</summary>
        private static string DecrireMedia(Studio.Printing.Devices.Dnp.DnpMediaSize media) =>
            media.ToString()
                .Replace("Size", "")
                .Replace("p", ",")
                .Replace("x", "×");

        private static InkGauge Jauge(string nom, int niveau, Color couleur) =>
            new($"{nom} : {niveau} %", new SolidColorBrush(couleur),
                Math.Max(2, Math.Clamp(niveau, 0, 100) / 100.0 * 54));

        public string Lettre { get; } = "";
        public string Nom { get; } = "";
        public string Etat { get; private set; } = "";
        public string Papier { get; } = "";
        public string Restant { get; } = "";
        public IReadOnlyList<InkGauge> Encres { get; } = [];
        public Brush Fond { get; } = Brushes.DimGray;

        /// <summary>La commande qui sort sur cette machine, s'il y en a une.</summary>
        public TravailImpression? Travail { get; set; }

        public Visibility TravailVisible =>
            Travail is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility ArretVisible => TravailVisible;

        /// <summary>File Windows de cette machine, quand elle en a une. Vide sinon.</summary>
        public string NomDeFile { get; private set; } = "";

        /// <summary>Pages que le spouleur dit encore avoir à sortir.</summary>
        public int PagesEnFile { get; private set; }

        /// <summary>
        /// « Vider la file » ne paraît que là où il sert : une machine du SPOULEUR qui a
        /// quelque chose en attente. Sur le minilab, la file est à la machine et se rappelle
        /// par le SDK — c'est le bouton d'arrêt qui s'en charge.
        ///
        /// Il ne se cache PAS quand la machine imprime normalement : une file qui descend
        /// n'a rien d'anormal, mais c'est justement l'écran où l'on constate qu'elle ne
        /// descend plus, et le bouton doit être là à ce moment-là.
        /// </summary>
        public Visibility PurgeVisible =>
            NomDeFile.Length > 0 && PagesEnFile > 0 ? Visibility.Visible : Visibility.Collapsed;

        public bool ArretPossible => Travail is { ArretDemande: false };

        public string ArretTexte => Travail is { ArretDemande: true } ? "Arrêt…" : "✕  Arrêter";

        /// <summary>
        /// Ce que l'opérateur cherche des yeux : combien de photos sont SORTIES, et si
        /// c'est fini. Pendant l'envoi on compte ce qui part ; dès que la machine tire, on
        /// compte le papier.
        /// </summary>
        public string TravailTexte
        {
            get
            {
                if (Travail is not { } t) return "";

                if (t.Etape.StartsWith("Tirage", StringComparison.Ordinal))
                {
                    // Le MOTIF avec le compte : « 3 en échec » ne dit pas quoi faire,
                    // « Paper size mismatch » si. C'est la machine qui parle.
                    var echecs = t.Rates > 0
                        ? $" · {t.Rates} en échec" +
                          (t.MotifDEchec.Length > 0 ? $" — {t.MotifDEchec}" : "")
                        : "";

                    if (t.TirageTermine)
                        return $"Commande {t.Numero} — terminée : {t.Sortis} photo(s) sorties{echecs}";

                    // LA question de l'opérateur qui a un client devant lui : ai-je le
                    // temps d'en servir un autre ? Elle n'a qu'une réponse, une durée.
                    var reste = t.DureeRestante is { } duree
                        ? " · " + EstimationDuree.Ecrire(duree, approximatif: t.Debit?.Fiable != true)
                        : "";

                    return $"Commande {t.Numero} — {t.Sortis} / {t.Total} photo(s) sorties" +
                           $"{echecs}{reste}";
                }

                return $"Commande {t.Numero} — {t.Etape} {t.Detail}";
            }
        }

        public double Fraction => Travail?.Fraction ?? 0;

        /// <summary>
        /// Une étape dont on ne connaît pas encore le total (le rendu qui démarre) doit
        /// montrer qu'il se passe quelque chose plutôt qu'une barre vide et immobile.
        /// </summary>
        public bool Indetermine => Travail is { Total: <= 0 };
    }
}
