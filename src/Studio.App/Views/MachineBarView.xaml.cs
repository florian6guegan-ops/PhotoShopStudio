using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Studio.App.Infrastructure;
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
    private const int SeuilAlerte = 25;

    private readonly DispatcherTimer _minuteur;

    /// <summary>Dernier état lu des machines : on le rejoue quand seul l'avancement change.</summary>
    private List<MachineTile> _tuiles = [];

    public MachineBarView()
    {
        InitializeComponent();

        _minuteur = new DispatcherTimer { Interval = Periode };
        _minuteur.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            _minuteur.Start();
            BrancherSuivi();
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _minuteur.Stop();
            DebrancherSuivi();
        };
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

    /// <summary>Relit l'état des machines. Appelable après une impression.</summary>
    public async Task RefreshAsync()
    {
        var lignes = new List<MachineTile>();

        // Le minilab d'abord : il répond vite et de façon fiable. La DNP est interrogée
        // ensuite et séparément — son SDK peut rester bloqué quand DiLand tient le port
        // USB, et ce blocage ne doit pas effacer les machines déjà connues.
        try
        {
            foreach (var fuji in await App.Services.Minilab.SnapshotAsync())
                lignes.Add(new MachineTile(fuji));

            _tuiles = lignes;
            Reafficher();
            MessageText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write("Bandeau : minilab indisponible", ex);
            MessageText.Text = "Minilab injoignable — les tirages restent possibles si la machine répond.";
        }

        try
        {
            foreach (var dnp in await App.Services.Minilab.DnpSnapshotAsync())
                lignes.Add(new MachineTile(dnp));

            _tuiles = lignes;
            Reafficher();
        }
        catch (Exception ex)
        {
            // silencieux à l'écran : les machines Fuji restent affichées
            FileLog.Write("Bandeau : imprimante DNP indisponible", ex);
        }
    }

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

    private sealed record InkGauge(string Info, Brush Couleur, double Hauteur);

    private sealed class MachineTile
    {
        public MachineTile(De100PrinterInfo info)
        {
            Lettre = info.MachineId.ToString();
            Nom = info.Model;
            Etat = DecrireEtat(info.Status);

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

            var format = info.Formats.FirstOrDefault(f => !f.Format.IsVariable);
            Restant = info.Media is { } m
                ? $"{m.PaperRemainingMm / 1000:0.0} m" +
                  (format is null ? "" : $" · ~{format.RemainingPrints} × {format.Format.Name}")
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
            Nom = string.IsNullOrWhiteSpace(info.SerialNumber) ? "DNP" : $"DNP {info.SerialNumber}";
            Papier = $"{DecrireMedia(info.MediaSize)} · {info.MediaClass}";

            var pourcent = info.MediaRemainingPercent is { } pc ? $" ({pc:0} %)" : "";
            Restant = $"{info.MediaRemaining} tirages restants{pourcent}";

            Encres = [];

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
                    var echecs = t.Rates > 0 ? $" · {t.Rates} en échec" : "";
                    return t.TirageTermine
                        ? $"Commande {t.Numero} — terminée : {t.Sortis} photo(s) sorties{echecs}"
                        : $"Commande {t.Numero} — {t.Sortis} / {t.Total} photo(s) sorties{echecs}";
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
