using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Web;

namespace Studio.App.Views;

/// <summary>
/// Dernier écran borne : format (un pour toute la commande), récapitulatif, envoi.
/// Le GUID de commande est figé à l'ouverture : réessayer n'enverra jamais un doublon.
/// </summary>
public partial class KioskCheckoutView : UserControl
{
    private readonly IReadOnlyList<KioskSelection> _selection;
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly List<ProductCard> _cards;

    public KioskCheckoutView(IReadOnlyList<KioskSelection> selection)
    {
        _selection = selection;
        InitializeComponent();

        _cards = App.Services.Catalog.Enabled
            .Where(p => p.Sheet is null)
            .Select(p => new ProductCard(p, OnCardChanged))
            .ToList();
        ProductsList.ItemsSource = _cards;
        UpdateSummary();
    }

    private void OnProductClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not ProductCard card) return;
        foreach (var c in _cards) c.Selected = c == card;
    }

    private void OnCardChanged() => UpdateSummary();

    private ProductCard? SelectedCard => _cards.FirstOrDefault(c => c.Selected);

    private void UpdateSummary()
    {
        var prints = _selection.Sum(s => s.Quantity);
        var card = SelectedCard;
        SummaryText.Text = card is null
            ? $"{prints} tirage(s) — choisissez un format"
            : $"{prints} tirage(s) × {card.Product.Price:0.00} € = {prints * card.Product.Price:0.00} €";
        SendButton.IsEnabled = card is not null;
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        var card = SelectedCard;
        if (card is null) return;

        var items = new List<KioskItemDto>();
        var paths = new List<string>();
        for (var i = 0; i < _selection.Count; i++)
        {
            var ext = Path.GetExtension(_selection[i].Path).ToLowerInvariant();
            items.Add(new KioskItemDto($"{i + 1:000}{ext}", card.Product.Code, _selection[i].Quantity));
            paths.Add(_selection[i].Path);
        }
        var dto = new KioskOrderDto(_orderId, App.Services.Mode.BorneName, null, items);

        SendButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;
        try
        {
            using var client = new KioskClient(App.Services.Mode.OperatorUrl);
            var ack = await client.SubmitAsync(dto, paths);
            Navigator.Home(new KioskDoneView(ack.DisplayNumber), "Merci !");
        }
        catch (Exception)
        {
            BusyPanel.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = true;
            MessageBox.Show(
                "L'envoi n'a pas abouti (réseau ?).\n\n" +
                "Touchez à nouveau « Envoyer ma commande » pour réessayer, " +
                "ou demandez de l'aide à l'accueil.",
                "Un instant…", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed class ProductCard : ObservableObject
    {
        private readonly Action _changed;
        private bool _selected;

        public ProductCard(Product product, Action changed)
        {
            Product = product;
            _changed = changed;
        }

        public Product Product { get; }
        public string Name => Product.Name;
        public string PriceLabel => $"{Product.Price:0.00} € la photo";

        public bool Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(BorderBrush));
                _changed();
            }
        }

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;
    }
}
