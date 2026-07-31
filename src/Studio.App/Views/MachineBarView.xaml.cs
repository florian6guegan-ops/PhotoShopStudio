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
        try
        {
            var etats = await App.Services.Minilab.SnapshotAsync();
            var lignes = etats.Select(e => new MachineTile(e)).ToList();

            MachinesList.ItemsSource = lignes;
            MessageText.Text = lignes.Count == 0 ? "Aucune machine détectée" : "";
        }
        catch (Exception ex)
        {
            // le bandeau ne doit jamais empêcher de travailler : on signale, sans bloquer
            FileLog.Write("Bandeau machines indisponible", ex);
            MachinesList.ItemsSource = null;
            MessageText.Text = "Minilab injoignable — les tirages restent possibles si la machine répond.";
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
