using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Studio.App.Infrastructure;
using Studio.Printing.Devices.Fuji;

namespace Studio.App.Views;

/// <summary>
/// Bandeau permanent des machines, repris de DiLand : chaque imprimante avec son papier
/// et ses jauges d'encre, visible depuis n'importe quel écran.
///
/// C'est le seul endroit où l'opérateur voit venir une panne de consommable sans avoir
/// à aller la chercher — et une encre vide au mauvais moment coûte une commande.
/// </summary>
public partial class MachineBarView : UserControl
{
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(2);
    private const int SeuilAlerte = 25;

    private readonly DispatcherTimer _minuteur;

    public MachineBarView()
    {
        InitializeComponent();

        _minuteur = new DispatcherTimer { Interval = Periode };
        _minuteur.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            _minuteur.Start();
            await RefreshAsync();
        };
        Unloaded += (_, _) => _minuteur.Stop();
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

            MachinesList.ItemsSource = lignes.ToList();
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

            MachinesList.ItemsSource = lignes.ToList();
        }
        catch (Exception ex)
        {
            // silencieux à l'écran : les machines Fuji restent affichées
            FileLog.Write("Bandeau : imprimante DNP indisponible", ex);
        }
    }

    private sealed record InkGauge(string Info, Brush Couleur, double Hauteur);

    private sealed record MachineTile
    {
        public MachineTile(De100PrinterInfo info)
        {
            var horsLigne = info.Status == De100PrinterStatus.Offline;

            Lettre = info.MachineId.ToString();
            Nom = info.Model;

            if (horsLigne)
            {
                Papier = "hors ligne";
                Restant = "";
                Encres = [];
                Fond = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
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

            // vert quand tout va bien, ambre dès qu'une encre faiblit : lisible d'un coup d'œil
            var alerte = info.Supplies?.InksBelow(SeuilAlerte).Any() == true;
            Fond = new SolidColorBrush(alerte
                ? Color.FromRgb(0x8A, 0x62, 0x0E)
                : Color.FromRgb(0x2E, 0x6B, 0x33));
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

            var alerte = info.Status.NeedsOperator || info.Status.IsFault || info.MediaRemaining <= 20;
            Fond = new SolidColorBrush(info.Status.IsCommunicationFailure
                ? Color.FromRgb(0x4A, 0x4A, 0x4A)
                : alerte ? Color.FromRgb(0x8A, 0x62, 0x0E) : Color.FromRgb(0x2E, 0x6B, 0x33));

            if (!info.Status.IsReady && !info.Status.IsBusy)
                Papier = info.Status.Message;
        }

        /// <summary>Nom lisible d'un format DNP : « Size6x4 » ne parle à personne.</summary>
        private static string DecrireMedia(Studio.Printing.Devices.Dnp.DnpMediaSize media) =>
            media.ToString()
                .Replace("Size", "")
                .Replace("p", ",")
                .Replace("x", "×");

        private static InkGauge Jauge(string nom, int niveau, Color couleur) =>
            new($"{nom} : {niveau} %", new SolidColorBrush(couleur),
                Math.Max(2, Math.Clamp(niveau, 0, 100) / 100.0 * 40));

        public string Lettre { get; } = "";
        public string Nom { get; } = "";
        public string Papier { get; } = "";
        public string Restant { get; } = "";
        public IReadOnlyList<InkGauge> Encres { get; } = [];
        public Brush Fond { get; } = Brushes.DimGray;
    }
}
