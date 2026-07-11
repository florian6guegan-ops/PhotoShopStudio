using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.App.Views;

namespace Studio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Navigator.Navigated += OnNavigated;
        Loaded += async (_, _) =>
        {
            if (App.Services.Mode.IsKiosk)
            {
                // borne : plein écran verrouillé, parcours client uniquement
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                Topmost = true;
                Navigator.Home(new KioskHomeView(), "Bienvenue");
                return;
            }

            Navigator.Home(new HomeView(), "Studio Photo");
            CheckPendingPrints();
            try
            {
                // upload téléphone + API bornes disponibles dès le démarrage
                await App.Services.EnsureUploadServerAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Serveur d'envoi non démarré : {ex.Message}\n" +
                    "Le poste fonctionne, mais téléphone et bornes seront indisponibles.",
                    "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    private void OnNavigated(UserControl view, string title)
    {
        view.Tag = title;
        ScreenHost.Content = view;
        TitleText.Text = title;
        BackButton.Visibility = Navigator.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => Navigator.Back();

    /// <summary>
    /// Récupération après crash : les enveloppes envoyées au spouleur sans confirmation
    /// de fin sont soumises à l'opérateur — jamais réimprimées automatiquement.
    /// </summary>
    private void CheckPendingPrints()
    {
        var services = App.Services;
        var recent = services.Store.ScanRecent(days: 3);
        var pending = services.Printer.FindEnvelopesNeedingConfirmation(recent);

        foreach (var (order, envelope) in pending)
        {
            var answer = MessageBox.Show(
                $"Commande {order.DisplayNumber}, enveloppe {envelope.Number} ({envelope.PrinterChannel}) :\n" +
                "l'application s'est arrêtée pendant l'impression.\n\n" +
                "Le tirage est-il bien sorti de l'imprimante ?\n\n" +
                "Oui = ne rien refaire   /   Non = réimprimer",
                "Impression à confirmer", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                services.Printer.ConfirmPrinted(order, envelope);
            }
            else
            {
                try
                {
                    services.Printer.PrintEnvelope(order, envelope, operatorConfirmed: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Échec de la réimpression : {ex.Message}", "Studio Photo",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
