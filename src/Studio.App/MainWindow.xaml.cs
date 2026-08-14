using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Studio.App.Infrastructure;
using Studio.App.Views;
using Studio.Core.Cloud;

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
                VerrouillerPleinEcran();
                Navigator.Home(new KioskHomeView(), "Bienvenue");
                return;
            }

            if (App.Services.Mode.IsIdentite)
            {
                // poste identité : plein écran verrouillé sur le parcours identité. Le
                // serveur d'envoi reste nécessaire — le client apporte souvent sa photo
                // par téléphone. Sortie vers le Studio complet par le PIN (voir
                // IdentiteHomeView → DeverrouillerVersOperateur).
                VerrouillerPleinEcran();
                Navigator.Home(new IdentiteHomeView(), "Photos d'identité");
                await DemarrerServeurEnvoiAsync();
                DemarrerVerificationMaj();   // le poste identité est tenu par le staff
                return;
            }

            await DemarrerOperateurAsync();
        };
    }

    // ----- notification de mise à jour -----

    private readonly DispatcherTimer _majTimer = new() { Interval = TimeSpan.FromHours(3) };
    private bool _majDemarree;

    /// <summary>La version qui tourne, telle qu'elle a été compilée.</summary>
    private static Version VersionInstallee =>
        typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Lance la surveillance des mises à jour : une fois tout de suite, puis toutes les
    /// trois heures. <b>Jamais en mode borne</b> — le bandeau ne doit pas s'afficher devant
    /// un client, et cette méthode n'est appelée que pour l'opérateur et le poste identité.
    ///
    /// Un poste reste ouvert toute la journée, et des versions paraissent en journée : sans
    /// la vérification périodique, l'opérateur ne verrait une correction que le lendemain.
    /// </summary>
    private void DemarrerVerificationMaj()
    {
        if (_majDemarree) return;   // identité puis déverrouillage : ne pas empiler
        _majDemarree = true;

        _majTimer.Tick += (_, _) => _ = VerifierMajEnFond();
        _majTimer.Start();
        _ = VerifierMajEnFond();
    }

    /// <summary>
    /// Demande au dépôt s'il existe une version plus récente, et lève le bandeau le cas
    /// échéant. <b>Rien n'est installé</b> : on annonce, l'opérateur décide dans les
    /// Paramètres. Silencieux sur panne réseau — <see cref="MiseAJour.DernierePubliee"/> rend
    /// null plutôt que de lever.
    /// </summary>
    private async Task VerifierMajEnFond()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var publiee = await new MiseAJour(client).DernierePubliee();

            if (publiee is null || !MiseAJour.EstPlusRecente(publiee.Version, VersionInstallee))
                return;

            Dispatcher.Invoke(() =>
            {
                MajBannerText.Text = $"⬆  Mise à jour {publiee.Version.ToString(3)} disponible";
                MajBanner.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            FileLog.Write("Vérification de mise à jour en fond impossible", ex);
        }
    }

    private void OnMajBannerClicked(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Navigator.Go(new SettingsView(), "Paramètres");

    /// <summary>Plein écran sans bordure, au premier plan : borne et poste identité.</summary>
    private void VerrouillerPleinEcran()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Topmost = true;
    }

    /// <summary>
    /// Bascule du mode identité verrouillé vers le Studio complet, le temps de la session.
    ///
    /// <b><c>mode.json</c> n'est pas touché</b> : au prochain démarrage, le poste repart en
    /// identité. C'est voulu — le staff dépanne (un réglage, une réimpression) puis referme
    /// le poste, sans avoir à reconfigurer quoi que ce soit.
    /// </summary>
    public async void DeverrouillerVersOperateur()
    {
        _deverrouille = true;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Topmost = false;
        WindowState = WindowState.Maximized;
        await DemarrerOperateurAsync();
    }

    /// <summary>
    /// Vrai quand le staff a ouvert le Studio complet par le PIN. <c>mode.json</c> dit
    /// toujours « identite », mais la SESSION est passée en opérateur : le bouton Accueil ne
    /// doit plus reboucler sur l'accueil identité.
    /// </summary>
    private bool _deverrouille;

    /// <summary>
    /// Poste identité ENCORE verrouillé : le parcours doit revenir à l'accueil identité, pas
    /// à l'accueil opérateur.
    /// </summary>
    private bool EnIdentiteVerrouille => App.Services.Mode.IsIdentite && !_deverrouille;

    /// <summary>Démarrage du poste opérateur : accueil complet, maintenance, serveur d'envoi.</summary>
    private async Task DemarrerOperateurAsync()
    {
        Navigator.Home(new HomeView(), "Studio Photo");
        CheckPendingPrints();
        App.Services.RunMaintenanceInBackground();
        DemarrerVerificationMaj();
        await DemarrerServeurEnvoiAsync();
    }

    /// <summary>Le serveur d'envoi (upload téléphone + API bornes). Idempotent.</summary>
    private async Task DemarrerServeurEnvoiAsync()
    {
        try
        {
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
    }

    private void OnNavigated(UserControl view, string title)
    {
        view.Tag = title;
        ScreenHost.Content = view;
        TitleText.Text = title;

        // En BORNE (client), aucun bouton d'accueil : le parcours est verrouillé et la
        // sortie se fait par le PIN. Partout ailleurs — opérateur ET poste identité — le
        // bouton ramène au point de départ. En identité verrouillée il retourne à l'accueil
        // IDENTITÉ, ce qui donne le « client suivant » qui manquait.
        var horsAccueil = Navigator.CanGoBack && !App.Services.Mode.IsKiosk;
        BackButton.Visibility = Navigator.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.Visibility = horsAccueil ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.Content = EnIdentiteVerrouille ? "⌂ Client suivant" : "⌂ Accueil";
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
        //
        // En poste identité verrouillé, on revient à l'accueil IDENTITÉ — le Studio complet
        // reste derrière le PIN.
        if (EnIdentiteVerrouille)
            Navigator.Home(new IdentiteHomeView(), "Photos d'identité");
        else
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
