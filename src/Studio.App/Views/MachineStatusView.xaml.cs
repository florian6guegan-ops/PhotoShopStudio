using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
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

        try
        {
            var etats = await App.Services.Minilab.SnapshotAsync();
            var lignes = etats.Select(e => new MachineRow(e)).ToList();

            MachinesList.ItemsSource = lignes;
            MessageText.Text = lignes.Count == 0
                ? "Aucune machine détectée. Vérifiez que le minilab est allumé."
                : "";
        }
        catch (Exception ex)
        {
            FileLog.Write("Lecture des consommables impossible", ex);
            MessageText.Text =
                "Impossible d'interroger le minilab :\n\n" + ex.Message +
                "\n\nLes tirages ne sont pas affectés tant que la machine répond à l'impression.";
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
