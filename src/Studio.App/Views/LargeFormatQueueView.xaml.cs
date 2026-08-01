using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;

namespace Studio.App.Views;

/// <summary>
/// Les agrandissements qui attendent d'être tirés sur l'Epson. Ils ne partent jamais
/// tout seuls : l'application prépare les fichiers, l'opérateur les imprime un par un
/// puis confirme. C'est le pendant visible de <see cref="Studio.Core.Domain.ProductOutput.ManualFile"/>.
/// </summary>
public partial class LargeFormatQueueView : UserControl
{
    private readonly Order? _onlyOrder;
    private readonly Envelope? _onlyEnvelope;

    /// <param name="onlyOrder">Limiter à une commande ; null = toutes les commandes récentes.</param>
    /// <param name="onlyEnvelope">Limiter à une enveloppe précise.</param>
    public LargeFormatQueueView(Order? onlyOrder = null, Envelope? onlyEnvelope = null)
    {
        InitializeComponent();
        _onlyOrder = onlyOrder;
        _onlyEnvelope = onlyEnvelope;

        if (onlyOrder is not null)
            TitleText.Text = $"Agrandissements — commande {onlyOrder.DisplayNumber}";

        Loaded += (_, _) => Refresh();
    }

    /// <summary>Nombre d'enveloppes en attente, pour l'afficher sur l'écran d'accueil.</summary>
    public static int PendingCount()
    {
        try
        {
            var orders = App.Services.Store.ScanRecent(days: 30);
            return App.Services.Printer.FindEnvelopesAwaitingManualPrint(orders).Count;
        }
        catch (Exception ex)
        {
            FileLog.Write("Comptage des agrandissements en attente impossible", ex);
            return 0;
        }
    }

    private void Refresh()
    {
        var orders = _onlyOrder is not null ? [_onlyOrder] : App.Services.Store.ScanRecent(days: 30);

        var groups = App.Services.Printer.FindEnvelopesAwaitingManualPrint(orders)
            .Where(x => _onlyEnvelope is null || x.Envelope.Number == _onlyEnvelope.Number)
            .OrderBy(x => x.Order.CreatedAt)
            .Select(x => new EnvelopeGroup(x.Order, x.Envelope, x.Folder))
            .Where(g => g.Files.Count > 0)
            .ToList();

        GroupsList.ItemsSource = groups;
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrintFile(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FileRow row) return;

        Navigator.Go(
            new LargeFormatPrintView(
                row.Path,
                App.Services.CatalogDir,
                $"{row.ProductLabel} — commande {row.OrderNumber}",
                onDone: Navigator.Back),
            "Impression grand format");
    }

    private void OnConfirmPrinted(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not EnvelopeGroup group) return;

        var answer = MessageBox.Show(
            $"Confirmer que les {group.Files.Count} tirage(s) de la commande {group.Order.DisplayNumber} " +
            "sont bien sortis de l'Epson ?\n\nL'enveloppe quittera alors cette liste.",
            "Agrandissements", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            App.Services.Printer.ConfirmPrinted(group.Order, group.Envelope);
        }
        catch (Exception ex)
        {
            FileLog.Write("Confirmation d'un agrandissement impossible", ex);
            MessageBox.Show($"Confirmation impossible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Refresh();
    }

    private sealed record FileRow(string Path, string ProductLabel, string OrderNumber)
    {
        public string FileLabel => System.IO.Path.GetFileName(Path);

        public BitmapImage? Thumbnail
        {
            get
            {
                try
                {
                    // vignette décodée en petit : la liste peut contenir des fichiers énormes
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(Path);
                    image.DecodePixelWidth = 160;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"Vignette illisible : {Path}", ex);
                    return null;
                }
            }
        }
    }

    private sealed record EnvelopeGroup
    {
        public EnvelopeGroup(Order order, Envelope envelope, string folder)
        {
            Order = order;
            Envelope = envelope;

            // la liste des fichiers vient de l'orchestrateur, qui est aussi celui qui les
            // nomme : deux endroits qui devinent le même motif finissent par diverger
            var prefix = $"env{envelope.Number:00}-";
            var files = App.Services.Printer.ManualPrintFiles(order, envelope);

            Files = files.Select(f => new FileRow(f, DescribeProduct(f, prefix), order.DisplayNumber)).ToList();
        }

        public Order Order { get; }
        public Envelope Envelope { get; }
        public List<FileRow> Files { get; }

        public string Header => $"Commande {Order.DisplayNumber} — enveloppe {Envelope.Number} — {Files.Count} tirage(s)";

        public string SubHeader =>
            $"{Order.CreatedAt:ddd dd/MM HH:mm} — {Order.Source} — à tirer sur l'Epson depuis cette liste, " +
            "puis confirmer.";

        /// <summary>Retrouve le produit depuis le nom de fichier « envNN-code-001.png ».</summary>
        private static string DescribeProduct(string path, string prefix)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];

            var lastDash = name.LastIndexOf('-');
            var code = lastDash > 0 ? name[..lastDash] : name;

            var produit = App.Services.Catalog.Find(code);
            return produit is not null ? $"{produit.Name} ({produit.WidthMm:0}×{produit.HeightMm:0} mm)" : code;
        }
    }
}
