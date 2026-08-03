using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;

namespace Studio.App.Views;

/// <summary>
/// Suivi des consommables du minilab : papier restant, encres, bac de maintenance, et
/// nombre de tirages encore possibles pour chaque format.
///
/// Les valeurs viennent de la machine elle-même, via le relais 32 bits. Le nombre de
/// tirages par format est calculé ici : le minilab ne le donne pas, et DiLand ne le
/// calcule pas non plus.
/// </summary>
public partial class MachineStatusView : UserControl
{
    /// <summary>En dessous de ce niveau, on prévient l'opérateur avant qu'un tirage sorte faux.</summary>
    private const int SeuilAlerte = 25;

    /// <summary>
    /// En dessous de ce nombre de tirages restants, on prévient pour la DNP : son rouleau
    /// ne se recharge pas en trente secondes, et une commande de vingt photos passe vite.
    /// </summary>
    private const int SeuilTiragesBas = 25;

    public MachineStatusView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        MessageText.Text = "Interrogation des machines…";
        MachinesList.ItemsSource = null;
        Mouse.OverrideCursor = Cursors.Wait;

        var lignes = new List<MachineRow>();
        var minilabMuet = (string?)null;

        try
        {
            foreach (var etat in await App.Services.Minilab.SnapshotAsync())
                lignes.Add(new MachineRow(etat));
        }
        catch (Exception ex)
        {
            FileLog.Write("Lecture des consommables impossible", ex);
            minilabMuet =
                "Impossible d'interroger le minilab :\n\n" + ex.Message +
                "\n\nLes tirages ne sont pas affectés tant que la machine répond à l'impression.";
        }

        // La DNP à part, et sans faire échouer l'écran : DiLand tient son port en exclusif
        // tant qu'il tourne, donc son absence est une situation normale et non une panne.
        try
        {
            foreach (var dnp in await App.Services.Minilab.DnpSnapshotAsync())
                lignes.Add(new MachineRow(dnp));
        }
        catch (Exception ex)
        {
            FileLog.Write("Consommables : imprimante DNP indisponible", ex);
        }

        try
        {
            MachinesList.ItemsSource = lignes;

            MessageText.Text =
                minilabMuet
                ?? (lignes.Count > 0 ? ""
                    : "Aucune machine détectée. Vérifiez que le minilab est allumé.");

            if (minilabMuet is null && !lignes.Any(l => l.EstDnp) && DiLandPresence.IsRunning())
            {
                MessageText.Text =
                    "La DS620 (DNP) n'apparaît pas : DiLand est ouvert et garde son port USB " +
                    "pour lui. Fermez DiLand — elle réapparaîtra toute seule en quelques secondes.";
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private sealed record SupplyRow(string Nom, int Niveau, Brush Couleur)
    {
        /// <summary>Largeur de la jauge, sur 220 points de large.</summary>
        public double Largeur => Math.Clamp(Niveau, 0, 100) / 100.0 * 220;

        public string Texte => $"{Niveau} %";
    }

    private sealed record FormatRow(string Nom, string Restants);

    private sealed record MachineRow
    {
        public MachineRow(De100PrinterInfo info)
        {
            var horsLigne = info.Status == De100PrinterStatus.Offline;

            Titre = $"Machine {info.MachineId} — {info.Model}";
            SousTitre = horsLigne
                ? "Hors ligne : aucune information disponible."
                : $"{Etat(info.Status)} · série {info.SerialNumber}";

            DetailVisibility = horsLigne ? Visibility.Collapsed : Visibility.Visible;

            if (horsLigne)
            {
                Consommables = [];
                Formats = [];
                Papier = Compteur = Alerte = "";
                AlerteVisibility = Visibility.Collapsed;
                return;
            }

            Compteur = $"{info.TotalPrintCount:N0} tirages depuis la mise en service";

            Papier = info.Media is { } media
                ? $"{media.PaperRemainingMm / 1000:0.00} m restants — rouleau {media.PaperWidthMm} mm, {media.Surface}"
                : "aucun rouleau déclaré";

            Consommables = info.Supplies is { } s
                ?
                [
                    new SupplyRow(s.Cyan.Name, s.Cyan.Level, Brushes.DarkCyan),
                    new SupplyRow(s.Magenta.Name, s.Magenta.Level, Brushes.MediumVioletRed),
                    new SupplyRow(s.Yellow.Name, s.Yellow.Level, Brushes.Goldenrod),
                    new SupplyRow(s.Black.Name, s.Black.Level, Brushes.Black),
                    new SupplyRow(s.MaintenanceTank.Name, s.MaintenanceTank.Level, Brushes.SlateGray),
                ]
                : [];

            // les formats à longueur libre n'ont pas de compte utile
            Formats = info.Formats
                .Where(f => !f.Format.IsVariable)
                .Select(f => new FormatRow(f.Format.Name, $"{f.RemainingPrints:N0}"))
                .ToList();

            var basses = info.Supplies?.InksBelow(SeuilAlerte).Select(i => $"{i.Name} ({i.Level} %)").ToList() ?? [];
            Alerte = basses.Count > 0
                ? "Encre bientôt épuisée : " + string.Join(", ", basses) +
                  ". Les couleurs peuvent sortir fausses avant la panne."
                : "";
            AlerteVisibility = basses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Une imprimante DNP (DS620).
        ///
        /// Elle n'a ni encres séparées ni bac de maintenance : le ruban et le papier
        /// s'épuisent ensemble, et c'est le nombre de tirages restants qui compte. Les
        /// jauges restent donc vides, et le rouleau se lit en tirages, pas en mètres.
        /// </summary>
        public MachineRow(DnpPrinterInfo info)
        {
            EstDnp = true;

            var horsLigne = info.Status.IsCommunicationFailure;

            Titre = info.EndormieOuInjoignable
                ? info.WindowsQueueName!
                : "DS620 (DNP)" +
                  (string.IsNullOrWhiteSpace(info.SerialNumber) ? "" : $" — série {info.SerialNumber}");

            SousTitre = info.EndormieOuInjoignable
                ? "En veille — elle se réveillera au premier tirage. Ses consommables ne " +
                  "sont lisibles que machine réveillée."
                : horsLigne
                    ? "Hors ligne : aucune information disponible."
                    : $"{EtatDnp(info.Status)} · micrologiciel {info.FirmwareVersion}";

            DetailVisibility = horsLigne ? Visibility.Collapsed : Visibility.Visible;
            Consommables = [];

            if (horsLigne)
            {
                Formats = [];
                Papier = Compteur = Alerte = "";
                AlerteVisibility = Visibility.Collapsed;
                return;
            }

            Compteur = $"{info.LifetimePrints:N0} tirages depuis la mise en service";

            var pourcent = info.MediaRemainingPercent is { } pc ? $" ({pc:0} %)" : "";
            Papier = $"{info.MediaRemaining:N0} tirages restants{pourcent} — " +
                     $"média {DecrireMedia(info.MediaSize)}, {info.MediaClass}";

            // Le format chargé est le seul possible : la DS620 change de média à la main.
            Formats = [new FormatRow(DecrireMedia(info.MediaSize), $"{info.MediaRemaining:N0}")];

            Alerte = info.Status.NeedsOperator
                ? "Intervention nécessaire sur la machine : " + info.Status.Message + "."
                : info.Status.IsFault
                    ? "Panne : " + info.Status.Message + ". La machine relève du SAV."
                    : info.MediaRemaining <= SeuilTiragesBas
                        ? $"Bientôt à court : {info.MediaRemaining} tirages restants. " +
                          "Prévoyez un rouleau avant la prochaine grosse commande."
                        : "";
            AlerteVisibility = Alerte.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Nom lisible d'un format DNP : « Size6x4 » ne parle à personne.</summary>
        private static string DecrireMedia(DnpMediaSize media) =>
            media.ToString().Replace("Size", "").Replace("p", ",").Replace("x", "×");

        private static string EtatDnp(DnpStatus status) =>
            status.IsCommunicationFailure ? "Hors ligne"
            : status.IsFault ? "Erreur — arrêtée"
            : status.NeedsOperator ? "Intervention nécessaire"
            : status.IsBusy ? "Impression en cours"
            : status.IsReady ? "Prête"
            : status.Message;

        /// <summary>Vrai pour une DNP : l'écran s'en sert pour expliquer une absence.</summary>
        public bool EstDnp { get; }

        public string Titre { get; }
        public string SousTitre { get; }
        public string Papier { get; }
        public string Compteur { get; }
        public string Alerte { get; }
        public Visibility AlerteVisibility { get; }
        public Visibility DetailVisibility { get; }
        public IReadOnlyList<SupplyRow> Consommables { get; }
        public IReadOnlyList<FormatRow> Formats { get; }

        private static string Etat(De100PrinterStatus status) => status switch
        {
            De100PrinterStatus.Ready => "Prête",
            De100PrinterStatus.Printing => "Impression en cours",
            De100PrinterStatus.Busy => "Occupée",
            De100PrinterStatus.Sleep => "En veille",
            De100PrinterStatus.ErrorProcessingCanBeContinued => "Erreur — le travail peut continuer",
            De100PrinterStatus.ErrorProcessingCannotBeContinued => "Erreur — travail interrompu",
            De100PrinterStatus.Offline => "Hors ligne",
            _ => status.ToString(),
        };
    }
}
