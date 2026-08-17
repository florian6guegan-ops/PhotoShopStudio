using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Studio.App.Infrastructure;
using Studio.Core.Cloud;
using Studio.Core.Domain;
using Studio.Core.Mail;
using Studio.Printing;
using Studio.Sources;

namespace Studio.App.Views;

/// <summary>
/// Le réglage du courriel, seul — pour le poste identité.
///
/// <b>Pourquoi un écran de plus.</b> Le Studio complet règle le courriel dans Paramètres,
/// au milieu du détourage, du wifi, du rapport de diagnostic et des raccourcis d'identité.
/// Sur un poste identité, ce n'est pas atteignable sans traverser tout le Studio complet —
/// et c'est pourtant le seul réglage dont ce poste-là a vraiment besoin, puisqu'il envoie
/// les photos au client. À Créteil, le compte courriel n'est toujours pas configuré pour
/// cette raison.
///
/// ⚠ <b>Les mêmes réglages, pas une copie.</b> Cet écran lit et écrit le MÊME
/// <see cref="MailSettings"/> (<c>mail.json</c>) par le même <c>App.Services.SaveMail</c>.
/// Il n'y a donc qu'un seul état, deux formulaires dessus — ce qui a fait des dégâts ici,
/// ce sont deux LOGIQUES jumelles qui divergent (<c>ReglagesRetenus</c> /
/// <c>ReglagesDe</c>), pas deux façons de saisir la même adresse.
///
/// Il est derrière le code staff : voir <see cref="IdentiteHomeView"/>. Un client ne doit
/// pas pouvoir lire le mot de passe du compte de la boutique.
/// </summary>
public partial class ReglagesIdentiteView : UserControl
{
    public ReglagesIdentiteView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            MontrerLaSource(App.Services.Identite);
            MontrerLeDetourage();
            Montrer(App.Services.Mail);
        };
    }

    // ----- d'où viennent les photos -----

    private void MontrerLaSource(ReglagesIdentite reglages)
    {
        var fixe = !string.IsNullOrWhiteSpace(reglages.DossierPhotos);

        DossierBox.Text = reglages.DossierPhotos;
        DossierRadio.IsChecked = fixe;
        CarteRadio.IsChecked = !fixe;

        CadrageAutoCheck.IsChecked = reglages.CadrageAutomatique;
        DireOuEnEstLeCadrage(reglages.CadrageAutomatique);

        // La case n'a de sens que sur un poste qui porte deux palettes — voir Habillage.
        HabillagePanel.Visibility = Habillage.EstReglable ? Visibility.Visible : Visibility.Collapsed;
        SombreCheck.IsChecked = reglages.ModeSombre;

        // La mise à jour d'Identité ne concerne qu'Identité : le Studio complet a la sienne
        // dans Paramètres, et sur une autre suite de publications.
        MajPanel.Visibility = EstIdentite ? Visibility.Visible : Visibility.Collapsed;
        MajVersionText.Text = $"Version installée : {VersionInstallee.ToString(3)}";

        DireOuVontLesPhotos();
    }

    /// <summary>
    /// Les formats mis en avant, sur l'écran du Catalogue.
    ///
    /// C'est le MÊME écran que celui du Studio complet (Catalogue → Raccourcis photo
    /// d'identité), et il enregistre dans le même fichier : Studio Photo Identité n'a
    /// simplement pas de Catalogue pour y mener.
    /// </summary>
    private void OnFormats(object sender, RoutedEventArgs e) =>
        Navigator.Go(new IdShortcutsView(), "Les formats mis en avant");

    /// <summary>
    /// Méthode et modèle de détourage. Le MÊME écran que celui des Paramètres du Studio
    /// complet, et le même <c>detourage.json</c> : ce logiciel-ci n'avait simplement aucune
    /// porte vers lui — voir <see cref="ReglagesDetourageView"/>.
    /// </summary>
    private void OnDetourage(object sender, RoutedEventArgs e) =>
        Navigator.Go(new ReglagesDetourageView(), "Détourage du fond blanc");

    /// <summary>
    /// Dit en une phrase ce que le poste fera du fond blanc. <b>C'est ce qui manquait le
    /// plus</b> : sur un poste jamais réglé, rien nulle part ne disait que le réseau était
    /// éteint, et la découpe par couleur qui renonce ressemble à une panne.
    /// </summary>
    private void MontrerLeDetourage()
    {
        var reglages = App.Services.Detourage;

        DetourageResumeText.Text = reglages.Actif
            ? $"Réseau de neurones, modèle « {reglages.ModeleDemande} » : le contour tient les " +
              "mèches de cheveux, et il faut compter quelques secondes par photo."
            : "Méthode par couleur : environ une seconde par photo, aucune exigence — mais la " +
              "photo reste INTACTE quand le fond n'est pas uni. C'est le réglage d'origine.";
    }

    /// <summary>
    /// Le cadrage automatique s'enregistre AU CLIC, et pas avec le bouton du bas.
    ///
    /// <b>Livré d'abord en attente d'« Enregistrer », il donnait un réglage qui paraissait
    /// pris et ne l'était pas</b> : on décoche la case, on revient à l'écran de cadrage, et
    /// la photo se cadre encore — « cocher ou décocher, il cadre quand même », signalé le
    /// 18/08/2026. La case voisine du mode sombre, elle, s'applique à l'instant : deux cases
    /// côte à côte qui n'obéissent pas à la même règle, c'est le piège assuré.
    ///
    /// La phrase d'état dit ce qui est ENREGISTRÉ, pas ce qui est coché : c'est la seule
    /// façon pour l'opérateur de vérifier que son geste a été pris.
    /// </summary>
    private void OnCadrageAuto(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        var voulu = CadrageAutoCheck.IsChecked == true;
        App.Services.SaveIdentite(App.Services.Identite with { CadrageAutomatique = voulu });

        DireOuEnEstLeCadrage(App.Services.Identite.CadrageAutomatique);
    }

    /// <summary>Ce qui est réellement enregistré, relu depuis les réglages.</summary>
    private void DireOuEnEstLeCadrage(bool actif) =>
        CadrageAutoEtatText.Text = actif
            ? "Enregistré : la photo s'ouvre cadrée sur le visage."
            : "Enregistré : la photo s'ouvre sur un cadre centré, à placer à la main.";

    /// <summary>
    /// Le mode sombre s'applique TOUT DE SUITE, avant même d'enregistrer : on choisit un
    /// habillage en le regardant, pas en lisant son nom. L'enregistrement suit avec le
    /// bouton, comme le reste de l'écran.
    /// </summary>
    private void OnModeSombre(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Habillage.Appliquer?.Invoke(SombreCheck.IsChecked == true);
    }

    /// <summary>
    /// Dit ce qui se passera vraiment au prochain « Ouvrir des photos » — et notamment
    /// qu'aucune carte n'est insérée, plutôt que de laisser l'opérateur le découvrir devant
    /// le client.
    /// </summary>
    private void DireOuVontLesPhotos()
    {
        if (DossierRadio.IsChecked == true)
        {
            var chemin = DossierBox.Text.Trim();

            if (chemin.Length == 0)
            {
                SourceEtatText.Text = "Aucun dossier choisi : « Ouvrir des photos » proposera les supports.";
                return;
            }

            SourceEtatText.Text = Directory.Exists(chemin)
                ? $"« Ouvrir des photos » ouvrira {chemin}."
                : $"Ce dossier n'existe pas (encore) : {chemin}. Tant qu'il manque, on retombera sur la carte mémoire.";
            return;
        }

        var supports = RemovableDriveWatcher.GetDrives();
        SourceEtatText.Text = supports.Count > 0
            ? "Support détecté : " + string.Join(", ", supports.Select(d => $"{d.Label} ({d.RootPath})"))
            : "Aucun support inséré pour l'instant — « Ouvrir des photos » proposera les supports du poste.";
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        DossierBox.IsEnabled = DossierRadio.IsChecked == true;
        ParcourirButton.IsEnabled = DossierRadio.IsChecked == true;
        DireOuVontLesPhotos();
    }

    private void OnParcourir(object sender, RoutedEventArgs e)
    {
        var boite = new OpenFolderDialog { Title = "Dossier des photos d'identité" };
        DossiersFavoris.Epingler(boite);

        if (DossierBox.Text.Trim() is { Length: > 0 } depart && Directory.Exists(depart))
            boite.InitialDirectory = depart;

        if (boite.ShowDialog() != true) return;

        DossierBox.Text = boite.FolderName;
        DossierRadio.IsChecked = true;
        DireOuVontLesPhotos();
    }

    private ReglagesIdentite SaisieSource() => new(
        DossierPhotos: DossierRadio.IsChecked == true ? DossierBox.Text.Trim() : "",
        ModeSombre: SombreCheck.IsChecked == true,
        CadrageAutomatique: CadrageAutoCheck.IsChecked == true);

    private void Montrer(MailSettings reglages)
    {
        ServeurBox.Text = reglages.Serveur;
        PortBox.Text = reglages.Port.ToString();
        ExpediteurBox.Text = reglages.Expediteur;
        NomBox.Text = reglages.NomExpediteur;
        MotDePasseBox.Password = reglages.MotDePasseApplication;
        ActifCheck.IsChecked = reglages.Actif;

        DireOuEnEstLaConfiguration(reglages);
    }

    /// <summary>Les réglages tels qu'ils sont saisis à l'écran, sans les enregistrer.</summary>
    private MailSettings Saisie() => new(
        Serveur: ServeurBox.Text.Trim(),
        // un port illisible vaut 0, ce que CeQuiManque() sait dire — plutôt que de lever
        // sur une frappe en cours
        Port: int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0,
        Expediteur: ExpediteurBox.Text.Trim(),
        NomExpediteur: NomBox.Text.Trim(),
        MotDePasseApplication: MotDePasseBox.Password,
        Actif: ActifCheck.IsChecked == true);

    /// <summary>
    /// Ce qui manque, en clair et en permanence — plutôt qu'un refus au moment d'envoyer,
    /// devant le client.
    /// </summary>
    private void DireOuEnEstLaConfiguration(MailSettings reglages)
    {
        if (reglages.EstUtilisable)
        {
            EtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            EtatText.Text = "L'envoi par courriel est configuré. " +
                            "Faites un essai pour vérifier que le serveur accepte.";
            return;
        }

        EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        EtatText.Text = "Envoi impossible pour l'instant : il manque " + reglages.CeQuiManque() + ".";
    }

    private void OnActifChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DireOuEnEstLaConfiguration(Saisie());
    }

    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        App.Services.SaveIdentite(SaisieSource());

        var reglages = Saisie();
        App.Services.SaveMail(reglages);
        DireOuEnEstLaConfiguration(reglages);
        DireOuVontLesPhotos();
    }

    private async void OnEssai(object sender, RoutedEventArgs e)
    {
        var reglages = Saisie();

        if (!reglages.EstUtilisable)
        {
            MessageBox.Show("Il manque encore " + reglages.CeQuiManque() + ".",
                "Courriel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EssaiButton.IsEnabled = false;
        Mouse.OverrideCursor = CurseurStudio.Attente;
        EtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        EtatText.Text = "Envoi de l'essai…";

        // le fichier d'essai part dans le cache : il ne regarde aucun client, et n'a pas à
        // grossir le dossier des envois
        var dossier = Path.Combine(App.Services.DataRoot, "cache", "essai-courriel");

        try
        {
            await Task.Run(() => PhotoMailer.EnvoyerUnEssai(reglages, dossier));

            EtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            EtatText.Text = $"Essai accepté par le serveur. Vérifiez la boîte {reglages.Expediteur}.";
        }
        catch (Exception ex)
        {
            FileLog.Write("Essai d'envoi par courriel (poste identité)", ex);
            EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            EtatText.Text = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            EssaiButton.IsEnabled = true;
        }
    }

    // ----- mise à jour -----

    /// <summary>
    /// L'exécutable qui tourne dit de QUEL logiciel il s'agit — et donc quelle suite de
    /// publications le concerne. Voir <see cref="Logiciel"/>, qui porte la question depuis
    /// que les raccourcis d'identité se la posent eux aussi.
    /// </summary>
    private static bool EstIdentite => Logiciel.EstIdentite;

    /// <summary>
    /// Un dépôt, deux applications, deux suites d'étiquettes : <c>v1.5.19</c> pour le
    /// Studio, <c>identite-v1.5.19</c> pour Identité — cette dernière en préversion, donc
    /// hors de « la dernière publication ». Voir <see cref="MiseAJour.PrefixeEtiquette"/>.
    /// </summary>
    private static MiseAJour Verificateur(System.Net.Http.HttpClient client) =>
        new(client) { PrefixeEtiquette = EstIdentite ? "identite-v" : "v" };

    private static Version VersionInstallee =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    private VersionPubliee? _majProposee;

    private async void OnChercherLaMaj(object sender, RoutedEventArgs e)
    {
        MajChercherButton.IsEnabled = false;
        MajInstallerButton.Visibility = Visibility.Collapsed;
        MajEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        MajEtatText.Text = "Recherche…";

        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var publiee = await Verificateur(client).DernierePubliee();

            if (publiee is null)
            {
                MajEtatText.Text = "Aucune version publiée n'a pu être lue. Vérifiez la " +
                                   "connexion à Internet — l'application continue de fonctionner.";
                return;
            }

            if (!MiseAJour.EstPlusRecente(publiee.Version, VersionInstallee))
            {
                MajEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
                MajEtatText.Text = "Cette version est à jour.";
                return;
            }

            _majProposee = publiee;
            MajEtatText.Foreground = (Brush)Application.Current.Resources["TitleBrush"];
            MajEtatText.Text =
                $"Version {publiee.Version.ToString(3)} disponible ({publiee.TailleLisible}).\n\n" +
                publiee.Notes;
            MajInstallerButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            FileLog.Write("Mise à jour (poste identité) : recherche impossible", ex);
            MajEtatText.Text = "Recherche impossible : " + ex.Message;
        }
        finally
        {
            MajChercherButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Télécharge, prépare, puis ferme l'application : le script reprend la main et la
    /// relance une fois les fichiers remplacés. On demande confirmation — elle va se fermer,
    /// peut-être au milieu d'un client.
    /// </summary>
    private async void OnInstallerLaMaj(object sender, RoutedEventArgs e)
    {
        if (_majProposee is not { } version) return;

        var reponse = MessageBox.Show(
            $"Installer la version {version.Version.ToString(3)} ?\n\n" +
            "L'application va se fermer, se mettre à jour, puis se rouvrir toute seule.\n" +
            "Terminez ce que vous êtes en train de faire avant de continuer.",
            "Mise à jour", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (reponse != MessageBoxResult.OK) return;

        MajInstallerButton.IsEnabled = false;
        MajEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        MajEtatText.Text = "Téléchargement…";

        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };

            var travail = Path.Combine(Path.GetTempPath(), "studio-maj-identite");
            var archive = await Verificateur(client).Telecharger(version, travail);

            var installe = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var script = MiseAJour.PreparerLInstallation(
                archive, installe, Path.Combine(installe, Logiciel.Executable));

            FileLog.Write($"Mise à jour (poste identité) : installation de {version.Version} lancée");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = script,
                UseShellExecute = true,
                CreateNoWindow = false,
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            FileLog.Write("Mise à jour (poste identité) : installation impossible", ex);
            MajEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            MajEtatText.Text = "Installation impossible : " + ex.Message;
            MajInstallerButton.IsEnabled = true;
        }
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();
}
