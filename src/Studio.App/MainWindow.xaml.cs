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
        BrancherSuiviImpressions();

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
            App.Services.RunMaintenanceInBackground();
            try
            {
                // upload téléphone + API bornes disponibles dès le démarrage
                FileLog.Write("Démarrage du serveur d'envoi…");
                await App.Services.EnsureUploadServerAsync();
                FileLog.Write("Serveur d'envoi démarré (port 8123)");
            }
            catch (Exception ex)
            {
                FileLog.Write("Échec du démarrage du serveur d'envoi", ex);
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

        // Les deux boutons vont ensemble : sur l'accueil il n'y a ni retour à faire, ni
        // accueil à rejoindre. En mode borne, aucun des deux — le parcours client est
        // verrouillé et n'a pas d'accueil opérateur où revenir.
        var horsAccueil = Navigator.CanGoBack && !App.Services.Mode.IsKiosk;
        BackButton.Visibility = Navigator.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.Visibility = horsAccueil ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => Navigator.Back();

    /// <summary>
    /// Retour à l'accueil depuis n'importe où, en mettant de côté ce qui était en
    /// préparation.
    ///
    /// <b>Le bouton ramène TOUJOURS à l'accueil</b>, même si la mise de côté échoue :
    /// c'est sa promesse, et un opérateur coincé sur un écran parce qu'un fichier ne
    /// s'écrit pas serait le pire des deux maux. L'échec se dit, il ne bloque pas.
    ///
    /// Le travail est cherché dans toute la PILE et non sur le seul écran affiché : depuis
    /// le recadrage d'une photo, c'est la grille qui porte la commande, deux écrans plus
    /// bas — voir <see cref="Reprises.Trouver"/>.
    /// </summary>
    private void OnHomeClicked(object sender, RoutedEventArgs e)
    {
        if (Reprises.Trouver() is { } ecran)
        {
            var resume = ecran.ResumeDeLAttente;
            if (ecran.EnregistrerPourReprise())
                FileLog.Write($"Accueil : travail mis en attente — {resume}");
        }

        // Rien n'est annoncé à l'écran, et c'est voulu : l'accueil affiche déjà le bandeau
        // « En attente » avec la commande et son heure, juste sous les yeux de celui qui
        // vient d'appuyer. Une boîte de dialogue à chaque retour serait un clic de plus,
        // cinquante fois par jour.
        Navigator.Home(new HomeView(), "Studio Photo");
    }

    /// <summary>
    /// Le bandeau des impressions en cours.
    ///
    /// C'est le seul retour qu'on ait une fois la commande partie, puisqu'il n'y a plus de
    /// boîte de dialogue à la fin : l'opérateur reprend la main immédiatement et surveille
    /// l'avancement d'un coup d'œil, d'où qu'il soit dans l'application.
    /// </summary>
    private void BrancherSuiviImpressions()
    {
        var suivi = App.Services.Impressions;

        suivi.PropertyChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            PrintBannerText.Text = suivi.Message;
            PrintBanner.Visibility = suivi.Visibilite;
            PrintBanner.Background = suivi.EnAlerte
                ? (System.Windows.Media.Brush)FindResource("DangerBrush")
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1B, 0x5E, 0x20));
        });
    }

    /// <summary>Un clic sur le bandeau efface l'avertissement, une fois lu.</summary>
    private void OnPrintBannerClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (App.Services.Impressions.EnAlerte) App.Services.Impressions.Acquitter();
    }

    /// <summary>
    /// Récupération après arrêt : les enveloppes parties à l'imprimante sans confirmation
    /// de sortie sont SIGNALÉES, jamais réimprimées automatiquement.
    ///
    /// Il y avait ici une boîte de dialogue par enveloppe : « le tirage est-il bien sorti ?
    /// Oui = ne rien refaire / Non = réimprimer ». Elle barrait l'écran au démarrage et
    /// forçait une réponse immédiate — or la question est mal posée quand la machine
    /// bloque : rien n'est sorti, on répond « Non », et l'on renvoie des tirages que le
    /// minilab a déjà en file. C'est exactement ce qui est arrivé le 01/08/2026, deux fois
    /// vingt-neuf tirages en double sur une file déjà à l'arrêt.
    ///
    /// Le garde-fou tient toujours : rien ne repart tout seul. Mais l'information passe
    /// par le bandeau, qui attend, et la réimpression se décide depuis « Commandes du
    /// jour » — là où l'on voit ce qu'on renvoie.
    /// </summary>
    private void CheckPendingPrints()
    {
        var services = App.Services;
        var recent = services.Store.ScanRecent(days: 3);
        var pending = services.Printer.FindEnvelopesNeedingConfirmation(recent);
        if (pending.Count == 0) return;

        foreach (var (order, envelope) in pending)
            FileLog.Write($"Impression non confirmée : commande {order.DisplayNumber}, " +
                          $"enveloppe {envelope.Number} ({envelope.PrinterChannel})");

        var quoi = pending.Count == 1
            ? $"Commande {pending[0].Order.DisplayNumber} : partie à l'imprimante, sortie non confirmée."
            : $"{pending.Count} impressions parties à l'imprimante, sortie non confirmée.";

        services.Impressions.Informer(
            $"{quoi} Rien n'a été renvoyé. Réimpression depuis « Commandes du jour » — cliquez pour effacer.",
            surAcquittement: () =>
            {
                foreach (var (order, envelope) in pending)
                    services.Printer.ConfirmPrinted(order, envelope);
            });
    }
}
