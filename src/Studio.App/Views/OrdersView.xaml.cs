using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.Core.Domain;

namespace Studio.App.Views;

public partial class OrdersView : UserControl
{
    public OrdersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var orders = App.Services.Store.ScanRecent(days: 7)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OrderRow(o))
            .ToList();

        OrdersList.ItemsSource = orders;
        EmptyText.Visibility = orders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private sealed record OrderRow(Order Order)
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
            Order.Envelopes.Select(env => new EnvelopeRow(Order, env)).ToList();
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
                    _ => Envelope.Status.ToString(),
                };
                return $"Enveloppe {Envelope.Number} — {Envelope.PrinterChannel} — {prints} tirage(s) — {status}";
            }
        }
    }
}
