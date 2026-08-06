using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Cloud;
using Studio.Core.Domain;
using Studio.Core.Imaging;
using Studio.Core.Mail;
using Studio.Imaging;
using Studio.Printing;
using Studio.Store.DiLand;
using Studio.Web;
using Studio.Web.Dropbox;

namespace Studio.App.Views;

/// <summary>
/// Les réglages propres à CE poste : l'envoi des photos par courriel, le détourage du
/// fond blanc, et le WiFi du magasin.
///
/// Ils vivent dans les données du poste (<c>config\mail.json</c>, <c>config\detourage.json</c>,
/// <c>config\wifi.json</c>)
/// et non dans le dépôt, qui est public : le mot de passe d'application n'a rien à y faire,
/// et un secret poussé par mégarde ne se rattrape pas.
///
/// Le détourage y est pour une autre raison : la bonne réponse dépend de la MACHINE. Un
/// poste à Quadro P2000 et un poste mieux doté n'ont pas le même réglage, et c'est ce que
/// l'écran doit dire — d'où les chiffres affichés plutôt qu'une case nue.
///
/// C'est ce qui permet d'installer un second poste opérateur sans toucher au code : on
/// ouvre cet écran, on saisit, on essaie.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Montrer(App.Services.Mail);
            MontrerDropbox(App.Services.Dropbox);
            MontrerLaMarque(App.Services.Marque);
            MontrerLeDetourage(App.Services.Detourage);
            MontrerLeWifi(App.Services.Wifi);
            MontrerLesTarifsIdentite(App.Services.TarifsIdentite);
            MontrerLesFavoris(App.Services.Favoris);
            MontrerLePoste(App.Services.Poste);
            MontrerLaVersion();
        };
    }

    // ===== Tarif des photos d'identité =====

    private void MontrerLesTarifsIdentite(TarifsIdentite tarifs)
    {
        TarifIdFranceBox.Text = tarifs.FranceEur.ToString("0.00");
        TarifIdEtrangerBox.Text = tarifs.EtrangerEur.ToString("0.00");
        DireOuEnEstLeTarifIdentite(tarifs);
    }

    /// <summary>
    /// Enregistre à chaque frappe, comme le reste de cet écran — mais uniquement si les
    /// DEUX cases contiennent un prix lisible. Un montant à moitié tapé ne doit pas se
    /// retrouver dans la configuration.
    /// </summary>
    private void OnTarifIdentiteChange(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        if (LirePrix(TarifIdFranceBox.Text) is not { } france ||
            LirePrix(TarifIdEtrangerBox.Text) is not { } etranger)
        {
            TarifIdEtatText.Text = "Saisissez un montant, par exemple 10 ou 12,50. " +
                                   "Tant qu'il n'est pas lisible, l'ancien tarif s'applique.";
            return;
        }

        var tarifs = new TarifsIdentite { FranceEur = france, EtrangerEur = etranger };
        App.Services.SaveTarifsIdentite(tarifs);
        DireOuEnEstLeTarifIdentite(tarifs);
    }

    private void DireOuEnEstLeTarifIdentite(TarifsIdentite tarifs) =>
        TarifIdEtatText.Text =
            $"Une planche française est facturée {tarifs.FranceEur:0.00} €, " +
            $"une étrangère {tarifs.EtrangerEur:0.00} €.";

    /// <summary>
    /// Un montant saisi à la main. La virgule ET le point sont acceptés : le pavé numérique
    /// d'un clavier français produit un point, et l'opérateur écrit une virgule.
    /// </summary>
    private static decimal? LirePrix(string saisie)
    {
        var texte = saisie.Trim().Replace(',', '.').Replace("€", "").Trim();

        return decimal.TryParse(texte, System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture, out var prix)
               && prix >= 0
            ? prix
            : null;
    }

    // ===== Dossiers favoris =====

    /// <summary>
    /// Un favori tel que l'écran le manipule.
    ///
    /// <b>Le chemin peut rester vide</b>, et c'est le cas des trois par défaut : « Bureau »
    /// et « Téléchargements » ne sont pas au même endroit d'un poste à l'autre, et les figer
    /// dans le fichier de configuration ferait un réglage qui ne survit pas au premier
    /// changement de session. On montre donc où le favori MÈNE plutôt que ce qu'il contient,
    /// et « Parcourir » ne sert qu'à rattraper ce que Windows ne sait pas trouver seul.
    /// </summary>
    private sealed class LigneFavori : ObservableObject
    {
        private string _libelle = "";
        private string _chemin = "";
        private bool _actif = true;

        public LigneFavori(DossierFavori favori)
        {
            _libelle = favori.Libelle;
            _chemin = favori.Chemin;
            _actif = favori.Actif;
            Cle = favori.Cle;
        }

        /// <summary>Ce que Windows sait trouver seul ; vide pour un dossier désigné à la main.</summary>
        public string Cle { get; }

        public string Libelle
        {
            get => _libelle;
            set => Set(ref _libelle, value);
        }

        public string Chemin
        {
            get => _chemin;
            set { if (Set(ref _chemin, value)) Rafraichir(); }
        }

        public bool Actif
        {
            get => _actif;
            set => Set(ref _actif, value);
        }

        public DossierFavori Vers() => new(Libelle.Trim(), Chemin.Trim(), Cle, Actif);

        /// <summary>Où ce favori mène vraiment, ou pourquoi il ne mène nulle part.</summary>
        public string Etat => DossiersUtilisateur.Resoudre(Vers()) is { } chemin
            ? "→  " + chemin
            : Cle.Length > 0
                ? "introuvable sur ce poste — cliquez sur « Parcourir » pour le désigner"
                : "dossier introuvable";

        public Brush EtatCouleur => DossiersUtilisateur.Resoudre(Vers()) is not null
            ? (Brush)Application.Current.Resources["MutedBrush"]
            : (Brush)Application.Current.Resources["DangerBrush"];

        public void Rafraichir()
        {
            OnPropertyChanged(nameof(Etat));
            OnPropertyChanged(nameof(EtatCouleur));
        }
    }

    private readonly List<LigneFavori> _favoris = [];

    private void MontrerLesFavoris(FavorisSettings reglage)
    {
        _favoris.Clear();
        foreach (var favori in reglage.Effectifs) _favoris.Add(new LigneFavori(favori));
        ReafficherLesFavoris();
    }

    private void ReafficherLesFavoris()
    {
        foreach (var ligne in _favoris) ligne.Rafraichir();

        // la liste n'est pas observable : on la repose pour que les ajouts et les retraits
        // se voient
        FavorisList.ItemsSource = null;
        FavorisList.ItemsSource = _favoris;
    }

    /// <summary>
    /// Enregistre les favoris tels qu'ils sont à l'écran.
    ///
    /// À chaque frappe, comme le reste de cet écran : un bouton « enregistrer » de plus
    /// serait un bouton de plus à oublier, et la liste est courte.
    /// </summary>
    private void EnregistrerLesFavoris()
    {
        App.Services.SaveFavoris(new FavorisSettings
        {
            Dossiers = _favoris.Where(f => f.Libelle.Trim().Length > 0).Select(f => f.Vers()).ToList(),
        });
    }

    private void OnFavoriChange(object sender, RoutedEventArgs e)
    {
        // pendant la construction du gabarit, les contrôles lèvent leur événement avant que
        // la liste ne soit à nous
        if (!IsLoaded) return;

        foreach (var ligne in _favoris) ligne.Rafraichir();
        EnregistrerLesFavoris();
    }

    private void OnFavoriParcourir(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not LigneFavori ligne) return;

        var boite = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Quel dossier pour « {ligne.Libelle} » ?",
            InitialDirectory = DossiersUtilisateur.Resoudre(ligne.Vers()) ?? "",
        };
        DossiersFavoris.Epingler(boite);

        if (boite.ShowDialog() != true) return;

        ligne.Chemin = boite.FolderName;
        EnregistrerLesFavoris();
        ReafficherLesFavoris();
    }

    private void OnFavoriRetirer(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not LigneFavori ligne) return;

        _favoris.Remove(ligne);
        EnregistrerLesFavoris();
        ReafficherLesFavoris();
    }

    private void OnFavoriAjouter(object sender, RoutedEventArgs e)
    {
        var boite = new Microsoft.Win32.OpenFolderDialog { Title = "Quel dossier épingler ?" };
        DossiersFavoris.Epingler(boite);

        if (boite.ShowDialog() != true) return;

        // le nom du dossier fait un libellé tout trouvé ; il se réécrit sur place
        var nom = Path.GetFileName(boite.FolderName.TrimEnd('\\', '/'));
        _favoris.Add(new LigneFavori(
            new DossierFavori(nom.Length > 0 ? nom : boite.FolderName, boite.FolderName)));

        EnregistrerLesFavoris();
        ReafficherLesFavoris();
    }

    private void OnFavorisParDefaut(object sender, RoutedEventArgs e)
    {
        _favoris.Clear();
        foreach (var favori in FavorisSettings.ParDefaut()) _favoris.Add(new LigneFavori(favori));

        EnregistrerLesFavoris();
        ReafficherLesFavoris();
    }

    /// <summary>Une file Windows telle que l'écran des paramètres la présente.</summary>
    private sealed record LigneImprimante(ImprimanteDetectee Vue)
    {
        public string RoleTexte => Vue.Role switch
        {
            RoleImprimante.GrandFormat => "Agrandissements",
            RoleImprimante.Sublimation => "Sublimation",
            RoleImprimante.Minilab => "Minilab",
            _ => "Autre",
        };

        public Brush RoleBrush => Vue.Role switch
        {
            RoleImprimante.Aucun => (Brush)Application.Current.Resources["PanelBrush"],
            _ => (Brush)Application.Current.Resources["AccentDarkBrush"],
        };

        /// <summary>Le nom, et ce qui l'a fait reconnaître — sans quoi la liste ne s'explique pas.</summary>
        public string Detail =>
            Vue.Motif.Length == 0 ? Vue.Nom : $"{Vue.Nom}  ·  {Vue.Motif}";
    }

    /// <summary>
    /// Montre ce que le poste a trouvé, et ce que l'opérateur a imposé.
    ///
    /// <b>On affiche TOUJOURS ce que la détection a trouvé</b>, même quand un réglage
    /// l'emporte : sans cela, personne ne peut savoir si la case est vide parce que la
    /// détection marche ou parce qu'elle a échoué.
    /// </summary>
    private void MontrerLePoste(PosteSettings poste)
    {
        DiLandBox.Text = poste.DiLandRacine;

        var trouve = DiLandLocator.Trouver(poste.DiLandRacine);
        DiLandTrouveText.Text = trouve is null
            ? "Aucune installation de DiLand trouvée sur ce poste — indiquez son dossier ci-dessous."
            : $"Trouvé : {trouve}";

        var imprimantes = DetectionImprimantes.Detecter();
        ImprimantesList.ItemsSource = imprimantes
            .OrderByDescending(i => i.Role != RoleImprimante.Aucun)
            .Select(i => new LigneImprimante(i))
            .ToList();

        RemplirLesRoles(GrandFormatCombo, imprimantes, poste.ImprimanteGrandFormat);
        RemplirLesRoles(SublimationCombo, imprimantes, poste.ImprimanteSublimation);

        RapportAdresseBox.Text = poste.AdresseRapport;
        CadrageAutoCheck.IsChecked = poste.CadrageAutoVisage;
    }

    /// <summary>Nom réservé au choix « laisser Studio décider ».</summary>
    private const string DetectionAutomatique = "Automatique — celle que Studio reconnaît";

    /// <summary>
    /// Remplit une liste de rôle avec TOUTES les files, et non les seules reconnues : une
    /// machine que la détection n'a pas su lire est exactement celle qu'on vient désigner
    /// à la main.
    /// </summary>
    private static void RemplirLesRoles(ComboBox liste,
        IReadOnlyList<ImprimanteDetectee> imprimantes, string reglage)
    {
        var choix = new List<string> { DetectionAutomatique };
        choix.AddRange(imprimantes.Select(i => i.Nom));

        liste.ItemsSource = choix;

        // un réglage qui ne désigne plus rien — machine débranchée, file renommée —
        // retombe sur « automatique » plutôt que de laisser la liste vide
        var rang = string.IsNullOrWhiteSpace(reglage) ? 0 : choix.FindIndex(
            c => c.Equals(reglage, StringComparison.OrdinalIgnoreCase));

        liste.SelectedIndex = rang >= 0 ? rang : 0;
    }

    /// <summary>Le nom retenu dans une liste de rôle, ou vide pour « automatique ».</summary>
    private static string RoleChoisi(ComboBox liste) =>
        liste.SelectedIndex <= 0 ? "" : (string)liste.SelectedItem;

    private PosteSettings SaisiePoste() => new(
        DiLandBox.Text.Trim(),
        RoleChoisi(GrandFormatCombo),
        RoleChoisi(SublimationCombo),
        RapportAdresseBox.Text.Trim(),
        CadrageAutoCheck.IsChecked == true);

    /// <summary>
    /// Choisit le dossier de DiLand. On accepte le dossier d'INSTALLATION : personne ne
    /// retient « Data\AllUsersData\Repositories\Default ».
    /// </summary>
    private void OnParcourirDiLand(object sender, RoutedEventArgs e)
    {
        var boite = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Où DiLand est-il installé ?",
            InitialDirectory = DiLandBox.Text.Trim() is { Length: > 0 } depart
                ? depart
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        DossiersFavoris.Epingler(boite);

        if (boite.ShowDialog() != true) return;

        DiLandBox.Text = boite.FolderName;

        // on le dit TOUT DE SUITE : découvrir au prochain démarrage que le dossier ne
        // convenait pas, c'est une journée sans commandes de bornes
        DiLandTrouveText.Text = DiLandLocator.DepotDe(boite.FolderName) is { } depot
            ? $"Trouvé : {depot}"
            : "Ce dossier ne contient pas de dépôt DiLand — ni « Database.db », ni dossier « Orders ».";
    }

    /// <summary>La version proposée, une fois qu'on l'a trouvée. Nulle tant qu'on n'a rien cherché.</summary>
    private VersionPubliee? _majProposee;

    /// <summary>La version qui tourne, telle qu'elle a été compilée.</summary>
    private static Version VersionInstallee =>
        typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private void MontrerLaVersion()
    {
        VersionText.Text = $"Version installée : {VersionInstallee.ToString(3)}";
        MajEtatText.Text = "";
        MajInstallerButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Demande au dépôt s'il existe mieux. <b>Rien n'est installé ici</b> — on annonce, et
    /// l'opérateur décide.
    /// </summary>
    private async void OnChercherUneMaj(object sender, RoutedEventArgs e)
    {
        MajChercherButton.IsEnabled = false;
        MajEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        MajEtatText.Text = "Recherche…";

        try
        {
            using var client = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

            var publiee = await new MiseAJour(client).DernierePubliee();

            if (publiee is null)
            {
                MajEtatText.Text =
                    "Aucune version publiée n'a pu être lue. Vérifiez la connexion à Internet — " +
                    "l'application continue de fonctionner normalement.";
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
            FileLog.Write("Mise à jour : recherche impossible", ex);
            MajEtatText.Text = "Recherche impossible : " + ex.Message;
        }
        finally
        {
            MajChercherButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Télécharge, prépare, puis ferme l'application : le script reprend la main et la
    /// relance une fois les fichiers remplacés.
    ///
    /// <b>On demande confirmation</b>, parce que l'application va se fermer — et qu'elle
    /// peut l'être au milieu d'une commande.
    /// </summary>
    private async void OnInstallerLaMaj(object sender, RoutedEventArgs e)
    {
        if (_majProposee is not { } version) return;

        var reponse = MessageBox.Show(
            $"Installer la version {version.Version.ToString(3)} ?\n\n" +
            "Studio Photo va se fermer, se mettre à jour, puis se rouvrir tout seul.\n" +
            "Terminez ce que vous êtes en train de faire avant de continuer.",
            "Mise à jour", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (reponse != MessageBoxResult.OK) return;

        MajInstallerButton.IsEnabled = false;
        MajEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        MajEtatText.Text = "Téléchargement…";

        try
        {
            using var client = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
            };

            var travail = Path.Combine(Path.GetTempPath(), "studio-maj");
            var archive = await new MiseAJour(client).Telecharger(version, travail);

            var installe = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var executable = Path.Combine(installe, "Studio.App.exe");

            var script = MiseAJour.PreparerLInstallation(archive, installe, executable);

            FileLog.Write($"Mise à jour : installation de la version {version.Version} lancée");

            // le script attend notre fermeture, remplace les fichiers, puis nous relance
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
            FileLog.Write("Mise à jour : installation impossible", ex);

            MajEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            MajEtatText.Text =
                "Installation impossible : " + ex.Message +
                "\n\nL'application actuelle est intacte.";

            MajInstallerButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Fabrique le rapport dans le dossier temporaire et rend son chemin, ou null si la
    /// fabrication a échoué — auquel cas l'écran l'a déjà dit.
    /// </summary>
    private string? FabriquerLeRapport()
    {
        try
        {
            var chemin = Path.Combine(Path.GetTempPath(), RapportDiagnostic.NomPropose());

            var contenu = RapportDiagnostic.Fabriquer(
                FileLog.LogsDir, App.Services.ConfigDir, chemin, RapportNoteBox.Text);

            RapportEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            RapportEtatText.Text =
                $"Rapport prêt ({contenu.TailleLisible}) : {contenu.Fichiers.Count} fichier(s) — " +
                string.Join(", ", contenu.Fichiers.Take(6)) +
                (contenu.Fichiers.Count > 6 ? "…" : "");

            return chemin;
        }
        catch (Exception ex)
        {
            FileLog.Write("Rapport de diagnostic : fabrication impossible", ex);

            RapportEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            RapportEtatText.Text = "Impossible de fabriquer le rapport : " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Envoie le rapport par courriel.
    ///
    /// L'adresse est retenue dans les réglages du poste : sur un poste de collègue, elle ne
    /// se saisit qu'une fois — et c'est justement le jour où quelque chose ne marche pas
    /// qu'on ne veut pas avoir à la chercher.
    /// </summary>
    private async void OnEnvoyerLeRapport(object sender, RoutedEventArgs e)
    {
        var adresse = RapportAdresseBox.Text.Trim();
        if (adresse.Length == 0)
        {
            RapportEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            RapportEtatText.Text = "Indiquez l'adresse où envoyer le rapport.";
            return;
        }

        if (FabriquerLeRapport() is not { } chemin) return;

        RapportEnvoyerButton.IsEnabled = false;
        try
        {
            await Task.Run(() => RapportDiagnostic.Envoyer(
                App.Services.Mail, adresse, chemin, RapportNoteBox.Text));

            // l'adresse est gardée : elle ne se saisit qu'une fois par poste
            App.Services.SavePoste(SaisiePoste() with { AdresseRapport = adresse });

            RapportEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            RapportEtatText.Text = $"Rapport envoyé à {adresse}.";
            FileLog.Write($"Rapport de diagnostic envoyé à {adresse}");
        }
        catch (Exception ex)
        {
            FileLog.Write("Rapport de diagnostic : envoi impossible", ex);

            RapportEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            RapportEtatText.Text =
                $"Envoi impossible : {ex.Message}\n\nLe fichier reste disponible : {chemin}";
        }
        finally
        {
            RapportEnvoyerButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Écrit le rapport dans un fichier, sans courriel.
    ///
    /// <b>C'est le recours quand rien ne marche</b> — et c'est un cas réel : un poste dont
    /// l'envoi par courriel n'est pas configuré est exactement celui dont on a le plus
    /// besoin des journaux. Le fichier se transmet ensuite comme on veut.
    /// </summary>
    private void OnEnregistrerLeRapport(object sender, RoutedEventArgs e)
    {
        if (FabriquerLeRapport() is not { } chemin) return;

        var boite = new Microsoft.Win32.SaveFileDialog
        {
            FileName = RapportDiagnostic.NomPropose(),
            Filter = "Archive ZIP|*.zip",
            Title = "Où enregistrer le rapport ?",
        };
        DossiersFavoris.Epingler(boite);

        if (boite.ShowDialog() != true) return;

        try
        {
            File.Copy(chemin, boite.FileName, overwrite: true);

            RapportEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            RapportEtatText.Text = "Rapport enregistré : " + boite.FileName;
        }
        catch (Exception ex)
        {
            RapportEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            RapportEtatText.Text = "Enregistrement impossible : " + ex.Message;
        }
    }

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

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    /// <summary>
    /// Formats mis en avant dans le module photo d'identité.
    ///
    /// Ils étaient au Catalogue, où l'on règle les PRODUITS — prix, imprimante, papier.
    /// Un raccourci n'en est pas un : il dit ce que l'écran identité présente en premier,
    /// et c'est un réglage du poste. Déplacé ici le 04/08/2026, à la demande de
    /// l'exploitant, qui les cherchait dans Paramètres.
    /// </summary>
    /// <summary>
    /// Les mots prêts à joindre aux photos. Ils vivent dans leur propre fichier — voir
    /// <c>MailMessages</c> — parce que cet écran-ci réécrit <c>mail.json</c> en entier.
    /// </summary>
    private void OnMessagesPredefinis(object sender, RoutedEventArgs e) =>
        Navigator.Go(new MailMessagesView(), "Messages prédéfinis");

    private void OnIdShortcuts(object sender, RoutedEventArgs e) =>
        Navigator.Go(new IdShortcutsView(), "Raccourcis photo d'identité");

    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        var reglages = Saisie();
        App.Services.SaveMail(reglages);
        DireOuEnEstLaConfiguration(reglages);

        // le détourage s'applique SANS redémarrage : SaveDetourage réinitialise la session
        // ONNX, sans quoi le nouveau modèle n'aurait cours qu'au prochain lancement
        App.Services.SaveDetourage(SaisieDetourage());

        App.Services.SaveWifi(SaisieWifi());

        // le dépôt DiLand est relâché par SavePoste : corriger un chemin qui ne marchait
        // pas doit valoir tout de suite, et non au prochain démarrage
        App.Services.SavePoste(SaisiePoste());

        // Le jeton n'est PAS pris à l'écran : il n'y est pas. Il vient de l'autorisation,
        // qui l'a déjà enregistré — le relire ici l'effacerait à chaque « Enregistrer ».
        App.Services.SaveDropbox(SaisieDropbox());

        // la marque s'applique sans redémarrage : SaveMarque la repose sur l'orchestrateur,
        // sans quoi les planches sortiraient avec l'ancienne jusqu'au prochain lancement
        App.Services.SaveMarque(SaisieMarque());

        MessageBox.Show("Réglages enregistrés.", "Paramètres",
            MessageBoxButton.OK, MessageBoxImage.Information);
        Navigator.Back();
    }

    // ----- envoi par Dropbox -----

    /// <summary>
    /// L'aléa PKCE de l'autorisation en cours.
    ///
    /// Il naît avec l'adresse ouverte dans le navigateur et meurt avec l'échange du code :
    /// les deux vont ensemble, et un code validé avec le mauvais aléa serait refusé. Voir
    /// <see cref="DropboxAuth"/>.
    /// </summary>
    private string? _dropboxCodeVerifier;

    private void MontrerDropbox(DropboxSettings reglages)
    {
        DropboxAppKeyBox.Text = reglages.AppKey;
        DropboxDossierBox.Text = reglages.DossierRacine;
        DropboxExpirationBox.Text = reglages.ExpirationJours.ToString();
        DropboxRetentionBox.Text = reglages.RetentionJours.ToString();
        DropboxMotDePasseBox.Text = reglages.MotDePasse;
        DropboxActifCheck.IsChecked = reglages.Actif;

        DireOuEnEstDropbox(reglages);
    }

    /// <summary>
    /// Les réglages tels qu'ils sont saisis, SANS le jeton : celui-ci ne s'affiche pas et
    /// ne se saisit pas, il vient de l'autorisation. On reprend donc celui qui est en place.
    /// </summary>
    private DropboxSettings SaisieDropbox() => new(
        AppKey: DropboxAppKeyBox.Text.Trim(),
        RefreshToken: App.Services.Dropbox.RefreshToken,
        DossierRacine: DropboxDossierBox.Text.Trim(),
        // une saisie illisible vaut 0, c'est-à-dire « pas d'expiration » : plus sûr que de
        // lever au milieu d'une frappe
        ExpirationJours: int.TryParse(DropboxExpirationBox.Text.Trim(), out var jours) && jours > 0
            ? jours
            : 0,
        MotDePasse: DropboxMotDePasseBox.Text,
        Actif: DropboxActifCheck.IsChecked == true,
        // une saisie illisible vaut 0, c'est-à-dire « ne jamais supprimer » : entre deux
        // interprétations d'une frappe en cours, on choisit celle qui n'efface rien
        RetentionJours: int.TryParse(DropboxRetentionBox.Text.Trim(), out var retenus) && retenus > 0
            ? retenus
            : 0);

    private void DireOuEnEstDropbox(DropboxSettings reglages)
    {
        if (reglages.EstUtilisable)
        {
            DropboxEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            DropboxEtatText.Text = "Le compte Dropbox est connecté.";
            return;
        }

        DropboxEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        DropboxEtatText.Text = "Envoi impossible pour l'instant : il manque " + reglages.CeQuiManque() + ".";
    }

    private void OnDropboxActifChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DireOuEnEstDropbox(SaisieDropbox());
    }

    private void OnDropboxAppKeyChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        DireOuEnEstDropbox(SaisieDropbox());
    }

    private void OnDropboxConsole(object sender, RoutedEventArgs e) =>
        Ouvrir("https://www.dropbox.com/developers/apps");

    /// <summary>
    /// Première moitié de l'autorisation : ouvrir Dropbox dans le navigateur.
    ///
    /// La clé est ENREGISTRÉE au passage. Sans cela, un opérateur qui la colle, connecte le
    /// compte puis quitte sans « Enregistrer » perdrait la clé tout en gardant le jeton —
    /// des réglages à moitié posés, et un envoi qui échoue sans qu'on comprenne pourquoi.
    /// </summary>
    private void OnDropboxConnecter(object sender, RoutedEventArgs e)
    {
        var cle = DropboxAppKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cle))
        {
            MessageBox.Show(
                "Saisissez d'abord la clé de l'application Dropbox.\n\n" +
                "Elle se crée sur dropbox.com/developers/apps — bouton « Console Dropbox » " +
                "juste à côté.",
                "Paramètres", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        App.Services.SaveDropbox(SaisieDropbox());

        var demande = DropboxAuth.Preparer(cle);
        _dropboxCodeVerifier = demande.CodeVerifier;

        DropboxCodePanel.Visibility = Visibility.Visible;
        DropboxCodeBox.Text = "";
        DropboxCodeBox.Focus();

        Ouvrir(demande.Url);
    }

    /// <summary>Seconde moitié : échanger le code recopié contre un jeton durable.</summary>
    private async void OnDropboxValiderLeCode(object sender, RoutedEventArgs e)
    {
        if (_dropboxCodeVerifier is null)
        {
            MessageBox.Show("Reprenez au bouton « Connecter le compte ».", "Paramètres",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DropboxValiderButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            var jeton = await DropboxAuth.EchangerAsync(
                DropboxAppKeyBox.Text.Trim(), DropboxCodeBox.Text, _dropboxCodeVerifier);

            // le jeton est enregistré AVANT toute autre chose : il ne sert qu'une fois et
            // le perdre imposerait de tout recommencer
            App.Services.SaveDropbox(SaisieDropbox() with { RefreshToken = jeton, Actif = true });

            DropboxActifCheck.IsChecked = true;
            DropboxCodePanel.Visibility = Visibility.Collapsed;
            _dropboxCodeVerifier = null;

            // on nomme le compte : « connecté » sans dire À QUOI laisserait passer une
            // connexion au compte personnel plutôt qu'à celui du studio
            using var client = new DropboxClient(
                await DropboxAuth.JetonDAccesAsync(DropboxAppKeyBox.Text.Trim(), jeton));

            var compte = await client.NomDuCompteAsync();

            DropboxEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            DropboxEtatText.Text = $"Compte Dropbox connecté : {compte}.";
        }
        catch (Exception ex)
        {
            FileLog.Write("Autorisation Dropbox impossible", ex);
            DropboxEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            DropboxEtatText.Text = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            DropboxValiderButton.IsEnabled = true;
        }
    }

    private static void Ouvrir(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"Ouverture de {url} impossible", ex);
            MessageBox.Show($"Impossible d'ouvrir le navigateur. Adresse à saisir à la main :\n\n{url}",
                "Paramètres", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ----- marque sur les planches identité -----

    private void MontrerLaMarque(MarqueSettings reglages)
    {
        MarqueMentionBox.Text = reglages.Mention;
        MarqueLogoBox.Text = reglages.LogoPath;
        MarqueQrBox.Text = reglages.QrTexte;
        MarqueActiveCheck.IsChecked = reglages.BandeActive;

        DireOuEnEstLaMarque(reglages);
    }

    private MarqueSettings SaisieMarque() => new(
        Mention: MarqueMentionBox.Text.Trim(),
        LogoPath: MarqueLogoBox.Text.Trim(),
        QrTexte: MarqueQrBox.Text.Trim(),
        BandeActive: MarqueActiveCheck.IsChecked == true);

    /// <summary>
    /// Un logo introuvable est DIT, et non découvert sur le tirage : le fichier vit hors du
    /// dépôt, il peut avoir été déplacé, et la planche sortirait alors sans lui en silence.
    /// </summary>
    private void DireOuEnEstLaMarque(MarqueSettings reglages)
    {
        if (!string.IsNullOrWhiteSpace(reglages.LogoPath) && !File.Exists(reglages.LogoPath))
        {
            MarqueEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            MarqueEtatText.Text =
                "Le fichier du logo est introuvable : les planches sortiront sans lui.";
            return;
        }

        MarqueEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
        MarqueEtatText.Text = reglages.BandeActive
            ? "La bande sera imprimée en bas des planches."
            : "Les planches porteront la date seule, comme avant.";
    }

    private void OnMarqueChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DireOuEnEstLaMarque(SaisieMarque());
    }

    private void OnChoisirLeLogo(object sender, RoutedEventArgs e)
    {
        var boite = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Logo de la boutique",
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tous les fichiers|*.*",
            CheckFileExists = true,
        };
        DossiersFavoris.Epingler(boite);

        if (boite.ShowDialog() != true) return;

        MarqueLogoBox.Text = boite.FileName;
        DireOuEnEstLaMarque(SaisieMarque());
    }

    // ----- détourage du fond blanc -----

    /// <summary>
    /// Vrai le temps de poser les cases à l'ouverture.
    ///
    /// Sans lui, cocher le modèle puissant depuis le code déclenchait l'avertissement
    /// destiné au CLIC de l'opérateur : on ouvrait Paramètres et une boîte de dialogue
    /// sautait au visage, sur un réglage qu'on venait juste de relire.
    /// </summary>
    private bool _chargementDesReglages;

    private void MontrerLeDetourage(DetourageSettings reglages)
    {
        _chargementDesReglages = true;
        try
        {
            CouleurRadio.IsChecked = !reglages.Actif;
            ReseauLegerRadio.IsChecked = reglages.Actif && !reglages.ModelePuissant;
            ReseauPuissantRadio.IsChecked = reglages.Actif && reglages.ModelePuissant;
        }
        finally
        {
            _chargementDesReglages = false;
        }

        DecrireLeMateriel();
        DecrireLesModeles();
        DireOuEnEstLeDetourage(reglages);
    }

    /// <summary>Les réglages de détourage tels qu'ils sont à l'écran, sans les enregistrer.</summary>
    private DetourageSettings SaisieDetourage() => new(
        Actif: CouleurRadio.IsChecked != true,
        ModelePuissant: ReseauPuissantRadio.IsChecked == true);

    private void OnDetourageChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _chargementDesReglages) return;

        DireOuEnEstLeDetourage(SaisieDetourage());
    }

    /// <summary>
    /// Cocher le modèle puissant avertit sur-le-champ quand la machine ne suivra pas, ou
    /// quand le fichier n'est pas là.
    ///
    /// <b>Le réglage n'est pas refusé pour autant</b> : le poste peut changer de carte, et
    /// le fichier peut être posé cinq minutes plus tard. C'est à l'exploitant de décider —
    /// on lui donne les chiffres, pas un verrou.
    /// </summary>
    private void OnModelePuissantChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _chargementDesReglages) return;

        OnDetourageChanged(sender, e);

        var avertissements = new List<string>();

        if (BiRefNetMatting.CheminDuModele(DetourageSettings.ModelePuissantFichier) is null)
            avertissements.Add(
                $"Le fichier « {DetourageSettings.ModelePuissantFichier} » n'est pas installé " +
                $"sur ce poste.\n\nIl doit être posé dans :\n{DossierDesModeles()}\n\n" +
                "En son absence, c'est le modèle « lite » qui sera utilisé.");

        if (CarteGraphique.Principale() is { MemoireGo: { } go } carte &&
            go < DetourageSettings.MemoireVideoMinimaleGo)
            avertissements.Add(
                $"La carte de ce poste ({carte.Nom}) n'a que {go:0.#} Go de mémoire vidéo, " +
                $"pour {DetourageSettings.MemoireVideoRecommandeeGo:0} Go recommandés.\n\n" +
                "Le modèle puissant réussira probablement la première photo puis échouera " +
                "sur la suivante, faute de mémoire. Studio retombera alors sur la méthode " +
                "par couleur — sans rien perdre, mais après avoir fait attendre.");

        if (avertissements.Count == 0) return;

        MessageBox.Show(string.Join("\n\n———\n\n", avertissements),
            "Détourage du fond blanc", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Le dossier où poser les modèles, celui que le moteur regarde en premier.</summary>
    private static string DossierDesModeles() =>
        BiRefNetMatting.DossiersCherches.FirstOrDefault()
        ?? Path.Combine(App.Services.DataRoot, "models");

    private void DecrireLeMateriel()
    {
        var carte = CarteGraphique.Principale();

        if (carte is null)
        {
            MaterielText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            MaterielText.Text = "Carte graphique : non identifiée sur ce poste.";
            return;
        }

        var assez = carte.MemoireGo is null ||
                    carte.MemoireGo >= DetourageSettings.MemoireVideoMinimaleGo;

        MaterielText.Foreground = (Brush)Application.Current.Resources[assez ? "OkBrush" : "TitleBrush"];
        MaterielText.Text = $"Carte de ce poste : {carte}." +
                            (assez ? "" : " Le modèle puissant y est déconseillé.");
    }

    private void DecrireLesModeles()
    {
        var leger = BiRefNetMatting.CheminDuModele(DetourageSettings.ModeleLeger);
        var puissant = BiRefNetMatting.CheminDuModele(DetourageSettings.ModelePuissantFichier);

        var lignes = new List<string>
        {
            $"« {DetourageSettings.ModeleLeger} » : " + (leger is null ? "absent" : "installé"),
            $"« {DetourageSettings.ModelePuissantFichier} » : " + (puissant is null ? "absent" : "installé"),
        };

        ModelesText.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        ModelesText.Text = string.Join("   ·   ", lignes) +
                           $"\nLes modèles se posent à la main dans {DossierDesModeles()} — " +
                           "ils ne sont pas dans le logiciel, un demi-gigaoctet n'a rien à faire " +
                           "dans un dépôt public.";
    }

    /// <summary>Ce que va faire le détourage, tel que l'écran est réglé — en une phrase.</summary>
    private void DireOuEnEstLeDetourage(DetourageSettings reglages)
    {
        if (!reglages.Actif)
        {
            DetourageEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            DetourageEtatText.Text =
                "Détourage par la méthode couleur : environ une seconde par photo, " +
                "aucune exigence matérielle.";
            return;
        }

        var demande = reglages.ModeleDemande;

        // ce que le moteur retiendra RÉELLEMENT avec ces réglages, et non ce qu'on lui
        // demande : les deux diffèrent dès qu'un fichier manque
        var precedent = BiRefNetMatting.ModelePrefere;
        BiRefNetMatting.ModelePrefere = demande;
        var retenu = BiRefNetMatting.ModeleRetenu;
        BiRefNetMatting.ModelePrefere = precedent;

        if (retenu is not null && Path.GetFileName(retenu).Equals(demande, StringComparison.OrdinalIgnoreCase))
        {
            DetourageEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            DetourageEtatText.Text = $"Détourage par le réseau, modèle « {demande} ».";
            return;
        }

        DetourageEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        DetourageEtatText.Text = $"« {demande} » n'est pas installé sur ce poste : " +
            (retenu is not null
                ? $"« {Path.GetFileName(retenu)} » sera utilisé à sa place."
                : "aucun modèle n'est installé, le détourage se fera par la méthode couleur.");
    }

    // ----- WiFi du magasin -----

    /// <summary>
    /// Vrai le temps de poser les champs à l'ouverture : les gestionnaires de saisie
    /// doivent alors se taire, sinon l'aperçu se redessine trois fois pour rien.
    /// </summary>
    private bool _chargementDuWifi;

    private void MontrerLeWifi(WifiConfig reglages)
    {
        _chargementDuWifi = true;
        try
        {
            WifiSsidBox.Text = reglages.Ssid;
            WifiCleBox.Text = reglages.Password;
            WifiMasqueCheck.IsChecked = reglages.Hidden;
            WifiSecuriteCombo.SelectedIndex = IndexDeLaSecurite(reglages.Security);
        }
        finally
        {
            _chargementDuWifi = false;
        }

        RafraichirLApercuWifi();
    }

    /// <summary>
    /// Les trois lignes de la liste, dans l'ordre du XAML. Tout ce qui n'est ni WEP ni
    /// « ouvert » vaut WPA — c'est aussi la règle de <see cref="WifiConfig"/>, qui négocie
    /// WPA2 et WPA3 sous le même nom.
    /// </summary>
    private static int IndexDeLaSecurite(string? securite) => securite switch
    {
        var s when string.Equals(s, "WEP", StringComparison.OrdinalIgnoreCase) => 1,
        var s when string.Equals(s, "nopass", StringComparison.OrdinalIgnoreCase) => 2,
        _ => 0,
    };

    private static string SecuriteDeLIndex(int index) => index switch
    {
        1 => "WEP",
        2 => "nopass",
        _ => "WPA",
    };

    /// <summary>Le réseau tel qu'il est saisi à l'écran, sans l'enregistrer.</summary>
    private WifiConfig SaisieWifi() => new()
    {
        Ssid = WifiSsidBox.Text.Trim(),
        Password = WifiCleBox.Text,
        Security = SecuriteDeLIndex(WifiSecuriteCombo.SelectedIndex),
        Hidden = WifiMasqueCheck.IsChecked == true,
    };

    private void OnWifiChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _chargementDuWifi) return;
        RafraichirLApercuWifi();
    }

    /// <summary>
    /// Redessine le code et dit ce qu'il vaut.
    ///
    /// Le code est fabriqué à partir de ce qui est À L'ÉCRAN, avant tout enregistrement :
    /// on le scanne avec son propre téléphone et l'on sait tout de suite s'il marche,
    /// plutôt que de le découvrir devant un client.
    /// </summary>
    private void RafraichirLApercuWifi()
    {
        var saisie = SaisieWifi();

        if (saisie.Network() is not { } reseau)
        {
            WifiQrImage.Source = null;
            WifiEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            WifiEtatText.Text = ReseauDeWindows() is { } automatique
                ? $"Aucun réseau saisi : Studio lit celui de Windows ({automatique.Ssid})."
                : "Aucun réseau saisi, et Windows n'en connaît aucun sur ce poste — " +
                  "le code de connexion ne s'affichera pas sur l'écran « téléphone ».";
            return;
        }

        try
        {
            WifiQrImage.Source = EnImage(WifiQr.Png(reseau));

            WifiEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            WifiEtatText.Text = reseau.Security == "nopass"
                ? $"Réseau « {reseau.Ssid} », sans clé."
                : $"Réseau « {reseau.Ssid} », clé de {reseau.Password.Length} caractère(s).";
        }
        catch (Exception ex)
        {
            FileLog.Write("Aperçu du code WiFi impossible", ex);
            WifiQrImage.Source = null;
            WifiEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            WifiEtatText.Text = $"Code impossible à produire : {ex.Message}";
        }
    }

    /// <summary>
    /// Ce que Windows connaît, lu UNE FOIS pour la vie de l'écran.
    ///
    /// La lecture lance <c>netsh</c> et exporte un profil : une seconde, sur le fil de
    /// l'interface. La refaire à chaque frappe ferait ramer la saisie — et la réponse ne
    /// change pas pendant qu'on remplit un formulaire.
    /// </summary>
    private WifiNetwork? ReseauDeWindows()
    {
        if (!_reseauDeWindowsLu)
        {
            _reseauDeWindows = WifiQr.Current();
            _reseauDeWindowsLu = true;
        }

        return _reseauDeWindows;
    }

    private WifiNetwork? _reseauDeWindows;
    private bool _reseauDeWindowsLu;

    private static BitmapImage EnImage(byte[] png)
    {
        using var flux = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = flux;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Envoie un vrai message à l'adresse d'expédition elle-même.
    ///
    /// C'est le seul contrôle qui vaille : « le serveur accepte » ne se devine pas d'un
    /// mot de passe bien tapé. Il part avec ce qui est À L'ÉCRAN, sans qu'on ait besoin
    /// d'enregistrer d'abord — sinon on écraserait une configuration qui marchait par une
    /// qu'on n'a pas encore vérifiée.
    /// </summary>
    private async void OnEssai(object sender, RoutedEventArgs e)
    {
        var reglages = Saisie();

        if (!reglages.EstUtilisable)
        {
            MessageBox.Show(
                "Il manque encore " + reglages.CeQuiManque() + ".",
                "Paramètres", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EssaiButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
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
            FileLog.Write("Essai d'envoi par courriel", ex);
            EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            EtatText.Text = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            EssaiButton.IsEnabled = true;
        }
    }
}
