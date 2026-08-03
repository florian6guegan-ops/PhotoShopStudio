using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Domain;

namespace Studio.App.Views;

public partial class OrdersView : UserControl
{
    /// <summary>Ce que l'onglet retenu laisse voir.</summary>
    private enum Genre
    {
        Tout,
        Tirages,
        Identite,
    }

    /// <summary>
    /// Les commandes lues au dernier passage. Gardées pour que changer d'onglet ne
    /// relise pas le disque : `ScanRecent` parcourt sept jours de dossiers.
    /// </summary>
    private List<Order> _commandes = [];

    public OrdersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _commandes = App.Services.Store.ScanRecent(days: 7)
            .OrderByDescending(o => o.CreatedAt)
            .ToList();

        // les compteurs comptent les ENVELOPPES, pas les commandes : une commande mixte
        // pèse dans les deux onglets, et annoncer « 3 » de part et d'autre pour trois
        // commandes dont une seule est une planche serait faux
        OngletTirages.Content = $"Tirages photo ({Compter(Genre.Tirages)})";
        OngletIdentite.Content = $"Photos d'identité ({Compter(Genre.Identite)})";

        Afficher();
    }

    private int Compter(Genre genre) =>
        _commandes.Sum(o => o.Envelopes.Count(e => Retenue(e, genre)));

    private Genre OngletRetenu =>
        OngletTirages.IsChecked == true ? Genre.Tirages
        : OngletIdentite.IsChecked == true ? Genre.Identite
        : Genre.Tout;

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Afficher();
    }

    private void Afficher()
    {
        var genre = OngletRetenu;

        // une commande ne paraît que si elle a quelque chose à montrer dans cet onglet,
        // et n'y montre alors que les enveloppes concernées
        var lignes = _commandes
            .Select(o => new OrderRow(o, o.Envelopes.Where(e => Retenue(e, genre)).ToList()))
            .Where(r => r.Envelopes.Count > 0)
            .ToList();

        OrdersList.ItemsSource = lignes;

        EmptyText.Text = genre switch
        {
            Genre.Tirages => "Aucun tirage photo ces derniers jours.",
            Genre.Identite => "Aucune planche de photos d'identité ces derniers jours.",
            _ => "Aucune commande ces derniers jours.",
        };
        EmptyText.Visibility = lignes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Cette enveloppe a-t-elle sa place dans l'onglet retenu ?
    ///
    /// Le tri se fait par ENVELOPPE et non par ligne, parce que c'est l'enveloppe qu'on
    /// réimprime : n'en montrer que la moitié laisserait croire qu'on peut ne retirer que
    /// celle-là. Une enveloppe qui mêle une planche et des tirages — le cas quand les deux
    /// sortent sur la même machine — paraît donc dans les DEUX onglets, entière. Rien ne
    /// disparaît, et le bouton dit la vérité sur ce qu'il va sortir.
    /// </summary>
    private static bool Retenue(Envelope enveloppe, Genre genre) => genre switch
    {
        Genre.Tirages => enveloppe.Lines.Any(l => !EstIdentite(l)),
        Genre.Identite => enveloppe.Lines.Any(EstIdentite),
        _ => true,
    };

    /// <summary>
    /// Une ligne de planche d'identité.
    ///
    /// On interroge d'abord le CATALOGUE : un produit à <c>Sheet</c> est une planche, et
    /// c'est la seule définition qui vaille. Repli sur la taille de case portée par
    /// l'article, pour les commandes enregistrées avant que ce champ existe — et pour
    /// celles dont le produit a été supprimé du catalogue depuis.
    /// </summary>
    private static bool EstIdentite(OrderLine ligne) =>
        App.Services.Catalog.Find(ligne.ProductCode)?.Sheet is not null
        || ligne.Items.Any(i => i.SheetCellWidthMm is > 0);

    // ----- retourner aux photos d'une commande -----

    /// <summary>
    /// Le dossier des photos d'origine d'une commande, ou null en le disant.
    ///
    /// Elles sont TOUJOURS recopiées à la création de la commande (voir
    /// <c>OrderFolderStore</c>) : le client peut avoir débranché sa clé USB depuis, et une
    /// réimpression qui dépendrait de son support serait inutilisable. C'est ce qui permet
    /// de ressortir les fichiers ou de les retravailler des jours plus tard.
    /// </summary>
    private static string? DossierDesPhotos(OrderRow ligne)
    {
        var dossier = App.Services.Store.GetPhotosFolder(ligne.Order);

        if (Directory.Exists(dossier) && Directory.EnumerateFiles(dossier).Any())
            return dossier;

        MessageBox.Show(
            $"Les photos de la commande {ligne.Order.DisplayNumber} ne sont plus sur le disque.\n\n" +
            "Elles ont sans doute été archivées : les commandes de plus de trente jours sont " +
            "déplacées dans le dossier d'archive.",
            "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>
    /// Recopie les photos de la commande dans les téléchargements, <b>même si elles l'ont
    /// déjà été</b> : c'est le geste qu'on refait quand un client redemande ses fichiers.
    ///
    /// Les originaux, et non les rendus : c'est ce dont le client a besoin pour faire
    /// tirer ailleurs ou retoucher lui-même.
    /// </summary>
    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow ligne) return;
        if (DossierDesPhotos(ligne) is not { } source) return;

        var telechargements = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (!Directory.Exists(telechargements))
            telechargements = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var destination = Path.Combine(telechargements,
            $"Commande-{ligne.Order.DisplayNumber}-{ligne.Order.CreatedAt:yyyy-MM-dd-HHmm}");

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

            Directory.CreateDirectory(destination);
            foreach (var fichier in Directory.EnumerateFiles(source))
                File.Copy(fichier, Path.Combine(destination, Path.GetFileName(fichier)),
                    overwrite: true);

            Mouse.OverrideCursor = null;

            // on ouvre le dossier : sans cela l'opérateur doit aller le chercher, et rien
            // à l'écran ne lui dit où il est
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Commandes du jour : téléchargement impossible", ex);
            MessageBox.Show($"Téléchargement impossible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Rouvre les photos de la commande pour les recadrer et les corriger — le même écran
    /// que « Modifier » sur une commande de borne.
    ///
    /// <b>La commande d'origine n'est pas touchée.</b> Un tirage depuis cet écran donnera
    /// une NOUVELLE commande, avec son numéro et son prix : c'est ce qu'il faut, parce
    /// qu'une commande déjà encaissée ne doit pas changer de contenu ni de montant. Le
    /// bouton le dit dans son infobulle.
    /// </summary>
    private void OnModify(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow ligne) return;
        if (DossierDesPhotos(ligne) is not { } source) return;

        var combien = Directory.EnumerateFiles(source).Count();

        Navigator.Go(
            new PhotoGridView(source, ProduitMajoritaire(ligne.Order), avecSousDossiers: false),
            $"Commande {ligne.Order.DisplayNumber} — {combien} photo(s)");
    }

    /// <summary>
    /// Le produit à présélectionner : celui qui pèse le plus de tirages dans la commande.
    ///
    /// Sur une commande de soixante 10×15 et d'un 13×18, présélectionner le 10×15 évite
    /// soixante corrections à la main. Un produit disparu du catalogue rend null, et
    /// l'écran demandera alors le format.
    /// </summary>
    private static string? ProduitMajoritaire(Order commande)
    {
        var code = commande.Envelopes
            .SelectMany(e => e.Lines)
            .GroupBy(l => l.ProductCode)
            .OrderByDescending(g => g.Sum(l => l.TotalPrints))
            .Select(g => g.Key)
            .FirstOrDefault();

        return code is not null && App.Services.Catalog.Find(code) is not null ? code : null;
    }

    private async void OnPrintTicket(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow row) return;
        var services = App.Services;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Run(() => Studio.Printing.EscPosTicket.Send(
                Studio.Printing.EscPosTicket.Build(row.Order, services.Catalog, services.Ticket),
                services.Ticket));
            Mouse.OverrideCursor = null;
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(
                $"Ticket non imprimé : {ex.Message}\n\n" +
                $"Vérifiez l'imprimante ticket ({services.Ticket.Host}).",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnReprint(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not EnvelopeRow row) return;

        var answer = MessageBox.Show(
            $"Réimprimer l'enveloppe {row.Envelope.Number} de la commande {row.Order.DisplayNumber} " +
            $"({row.Envelope.PrinterChannel}) ?\n\nLes tirages sortiront une nouvelle fois.",
            "Réimpression", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Run(() =>
                App.Services.Printer.PrintEnvelope(row.Order, row.Envelope, operatorConfirmed: true));
            Mouse.OverrideCursor = null;
            MessageBox.Show($"Enveloppe {row.Envelope.Number} réimprimée.", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show($"Échec de la réimpression : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Refresh();
    }

    /// <summary>
    /// Ouvre la file des agrandissements limitée à cette enveloppe : l'opérateur y tire
    /// chaque image sur l'Epson, puis confirme.
    /// </summary>
    private void OnPrintLargeFormat(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not EnvelopeRow row) return;

        Navigator.Go(new LargeFormatQueueView(row.Order, row.Envelope),
            $"Agrandissements — commande {row.Order.DisplayNumber}");
    }

    /// <param name="Retenues">
    /// Les seules enveloppes que l'onglet laisse voir. Elles ne sont PAS recalculées ici :
    /// c'est l'affichage qui décide de ce qu'il montre, la ligne ne fait que le porter.
    /// </param>
    private sealed record OrderRow(Order Order, IReadOnlyList<Envelope> Retenues)
    {
        public string Header =>
            $"N° {Order.DisplayNumber} — {Order.CreatedAt:ddd dd/MM HH:mm} — {Order.Source} — {Order.Total:0.00} €";

        public string StatusText => Order.Status switch
        {
            OrderStatus.Draft => "Brouillon",
            OrderStatus.Submitted => "À traiter",
            OrderStatus.InReview => "En cours",
            OrderStatus.Printing => "Impression…",
            OrderStatus.Ready => "Prête",
            OrderStatus.Delivered => "Remise",
            OrderStatus.Cancelled => "Annulée",
            _ => Order.Status.ToString(),
        };

        public Brush StatusBrush => Order.Status switch
        {
            OrderStatus.Ready or OrderStatus.Delivered => (Brush)Application.Current.Resources["OkBrush"],
            OrderStatus.Cancelled => (Brush)Application.Current.Resources["DangerBrush"],
            _ => (Brush)Application.Current.Resources["AccentBrush"],
        };

        public List<EnvelopeRow> Envelopes =>
            Retenues.Select(env => new EnvelopeRow(Order, env)).ToList();
    }

    private sealed record EnvelopeRow(Order Order, Envelope Envelope)
    {
        public string Label
        {
            get
            {
                var prints = Envelope.Lines.Sum(l => l.TotalPrints);
                var status = Envelope.Status switch
                {
                    EnvelopeStatus.Pending => "en attente",
                    EnvelopeStatus.Rendering => "préparation…",
                    EnvelopeStatus.Spooled => "envoyée à l'imprimante",
                    EnvelopeStatus.Printed => "imprimée",
                    EnvelopeStatus.Error => $"ERREUR : {Envelope.Error}",
                    EnvelopeStatus.AwaitingManualPrint => "à tirer sur l'Epson",
                    _ => Envelope.Status.ToString(),
                };
                return $"Enveloppe {Envelope.Number} — {Envelope.PrinterChannel} — {prints} tirage(s) — {status}";
            }
        }

        /// <summary>Une enveloppe d'agrandissements se tire depuis la boîte grand format, pas par le spouleur.</summary>
        public bool IsLargeFormat => Envelope.Status == EnvelopeStatus.AwaitingManualPrint;

        public Visibility LargeFormatVisibility => IsLargeFormat ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ReprintVisibility => IsLargeFormat ? Visibility.Collapsed : Visibility.Visible;
    }
}
