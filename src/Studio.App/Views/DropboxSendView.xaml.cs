using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Cloud;
using Studio.Core.Mail;
using Studio.Printing;
using Studio.Sources;
using Studio.Web.Dropbox;

namespace Studio.App.Views;

/// <summary>
/// Envoi des photos au client par Dropbox, depuis l'onglet Tirage.
///
/// <b>Ce n'est pas « Dropbox Transfer »</b>, et cela ne peut pas l'être : Transfer est une
/// fonction du site de Dropbox, sans API — la demande traîne sur leur forum développeurs
/// depuis des années — et les autres services de transfert n'offrent rien de gratuit et
/// d'automatisable (WeTransfer a retiré son API en 2022, SwissTransfer n'en a pas, celle de
/// Smash est payante). Ce qui EST gratuit, c'est l'API Dropbox v2 : on téléverse dans un
/// dossier daté et on en crée un lien de partage. Du côté du client, la différence ne se
/// voit pas : un lien, un dossier à télécharger, aucun compte à créer.
///
/// Deux lots possibles, et c'est tout l'intérêt de faire le choix ICI : ce que l'opérateur
/// a coché, ou le dossier entier. Les deux comptes sont affichés côte à côte, parce que
/// « tout envoyer » sur un dossier de mariage n'a pas le même poids que sur trois photos.
/// </summary>
public partial class DropboxSendView : UserControl
{
    private readonly IReadOnlyList<string> _selection;
    private readonly string _rootPath;
    private readonly bool _avecSousDossiers;

    private IReadOnlyList<string>? _dossierEntier;
    private CancellationTokenSource? _arret;
    private bool _envoiEnCours;

    /// <param name="selection">Les photos cochées à l'écran des tirages.</param>
    /// <param name="rootPath">Dossier d'origine, proposé en second choix.</param>
    /// <param name="avecSousDossiers">Le dossier a-t-il été parcouru en profondeur ?</param>
    public DropboxSendView(IReadOnlyList<string> selection, string rootPath, bool avecSousDossiers)
    {
        ArgumentNullException.ThrowIfNull(selection);

        _selection = selection;
        _rootPath = rootPath;
        _avecSousDossiers = avecSousDossiers;

        InitializeComponent();

        Loaded += async (_, _) =>
        {
            NomBox.Text = NomParDefaut();
            VerifierLaConfiguration();
            AnnoncerLesLots();
            await CompterLeDossierAsync();
        };

        // un écran quitté pendant un envoi ne doit pas laisser la tâche courir dans le vide
        Unloaded += (_, _) => _arret?.Cancel();
    }

    /// <summary>Le nom du dossier d'origine : ce que l'opérateur reconnaîtra le plus vite.</summary>
    private string NomParDefaut() =>
        Path.GetFileName(_rootPath.TrimEnd('\\', '/')) is { Length: > 0 } dossier ? dossier : "Photos";

    /// <summary>
    /// Dit tout de suite si l'envoi est configuré sur ce poste, plutôt que de laisser
    /// découvrir le problème une fois le client prévenu qu'il va recevoir un lien.
    /// </summary>
    private void VerifierLaConfiguration()
    {
        var reglages = App.Services.Dropbox;
        if (reglages.EstUtilisable)
        {
            AvertissementConfig.Visibility = Visibility.Collapsed;
            AnnoncerLesReglagesDuLien(reglages.ExpirationJours, reglages.MotDePasse);
            return;
        }

        AvertissementConfig.Visibility = Visibility.Visible;
        AvertissementText.Text =
            "L'envoi par Dropbox n'est pas configuré sur ce poste : il manque " +
            reglages.CeQuiManque() + ".";
        EnvoyerButton.IsEnabled = false;
    }

    /// <summary>
    /// Ce que le lien portera. Annoncé AVANT l'envoi et au conditionnel, parce que Dropbox
    /// réserve l'expiration et le mot de passe aux comptes payants : sur un compte gratuit
    /// il les refuse, et le lien sort permanent.
    /// </summary>
    private void AnnoncerLesReglagesDuLien(int jours, string motDePasse)
    {
        var reglages = App.Services.Dropbox;
        var lignes = new List<string>();

        // Le ménage EN PREMIER : c'est lui qui décide vraiment de la durée de vie du lien,
        // parce qu'il marche sur toutes les offres, alors que l'expiration du lien demande
        // un compte payant. C'est aussi ce que le client lira dans son courriel.
        if (reglages.RetentionJours > 0)
            lignes.Add($"Les photos seront supprimées du Dropbox du studio au bout de " +
                       $"{reglages.RetentionJours} jours, et le lien cessera alors de marcher. " +
                       "C'est ce délai qui sera annoncé au client.");
        else
            lignes.Add("Les photos resteront dans le Dropbox du studio jusqu'à ce que vous " +
                       "les supprimiez (aucune suppression automatique n'est réglée).");

        var demandes = new List<string>();
        if (jours > 0) demandes.Add($"expiration du lien au bout de {jours} jours");
        if (!string.IsNullOrWhiteSpace(motDePasse)) demandes.Add("mot de passe");

        if (demandes.Count > 0)
            lignes.Add("En plus : " + string.Join(" et ", demandes) +
                       ". Ces deux options demandent un compte Dropbox payant ; sur un compte " +
                       "gratuit le lien partira quand même, simplement sans elles.");

        LienReglageText.Text = string.Join("\n\n", lignes);
    }

    // ----- les deux lots -----

    private void AnnoncerLesLots()
    {
        SelectionRadio.Content = _selection.Count switch
        {
            0 => "La sélection — aucune photo cochée",
            1 => "La sélection — 1 photo",
            _ => $"La sélection — {_selection.Count} photos",
        };

        // rien de coché : le second choix devient le seul, et il est pris d'office plutôt
        // que de laisser un bouton « Envoyer » grisé sans explication
        SelectionRadio.IsEnabled = _selection.Count > 0;
        if (_selection.Count == 0) DossierRadio.IsChecked = true;

        DossierRadio.Content = _dossierEntier is null
            ? "Le dossier entier — comptage…"
            : $"Le dossier entier — {_dossierEntier.Count} photos";

        MettreAJourLeVolume();
        MettreAJourLeBouton();
    }

    /// <summary>
    /// Compte les photos du dossier, hors du fil d'interface.
    ///
    /// Le comptage se fait à l'ouverture et non au clic sur le second choix : sur une carte
    /// de plusieurs milliers de fichiers, il prend une seconde, et une seconde après un clic
    /// donne l'impression que l'écran a planté.
    /// </summary>
    private async Task CompterLeDossierAsync()
    {
        try
        {
            _dossierEntier = await Task.Run(() =>
                PhotoScanner.Scan(_rootPath, _avecSousDossiers, PhotoScanner.MaxAffichable)
                    .Where(f => !PhotoScanner.IsPdf(f))
                    .ToList());
        }
        catch (Exception ex)
        {
            FileLog.Write($"Comptage du dossier impossible ({_rootPath})", ex);
            _dossierEntier = [];
        }

        AnnoncerLesLots();
    }

    /// <summary>Les fichiers que le choix courant fera partir.</summary>
    private IReadOnlyList<string> LotChoisi() =>
        DossierRadio.IsChecked == true ? _dossierEntier ?? [] : _selection;

    /// <summary>
    /// Le volume, parce que c'est lui qui décide du temps d'attente au comptoir : deux
    /// cents photos de reflex, ce sont plusieurs gigaoctets et un quart d'heure de ligne
    /// montante, et cela se dit au client avant de commencer.
    /// </summary>
    private void MettreAJourLeVolume()
    {
        var lot = LotChoisi();
        if (lot.Count == 0)
        {
            VolumeText.Text = "";
            return;
        }

        long octets = 0;
        foreach (var fichier in lot)
        {
            try { octets += new FileInfo(fichier).Length; }
            catch (IOException) { /* fichier disparu entre-temps : il manquera à l'envoi, pas au compte */ }
        }

        VolumeText.Text = $"{lot.Count} photo(s), {octets / 1024.0 / 1024:0.#} Mo à téléverser.";
    }

    private void OnLotChange(object sender, RoutedEventArgs e)
    {
        // les Checked partent dès l'analyse du XAML, avant que les champs ne soient là
        if (!IsLoaded) return;

        MettreAJourLeVolume();
        MettreAJourLeBouton();
    }

    // ----- adresse du client -----

    /// <summary>Les adresses saisies, lues comme partout ailleurs dans l'application.</summary>
    private IReadOnlyList<string> AdressesSaisies() => Destinataires.Analyser(AdresseBox.Text);

    /// <summary>
    /// Dit ce qui va partir, et à qui.
    ///
    /// Une adresse mal tapée est NOMMÉE plutôt que d'échouer en silence au moment de
    /// l'envoi : le lien, lui, sera déjà créé, et le client sera reparti.
    /// </summary>
    private void OnAdresseChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;

        var adresses = AdressesSaisies();
        if (adresses.Count == 0)
        {
            AdressesText.Text = "";
            return;
        }

        var douteuses = adresses.Where(a => !Destinataires.Recevable(a)).ToList();
        if (douteuses.Count > 0)
        {
            AdressesText.Text = douteuses.Count == 1
                ? $"Adresse douteuse : {douteuses[0]}"
                : "Adresses douteuses : " + string.Join(", ", douteuses);
            AdressesText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            return;
        }

        AdressesText.Foreground = (System.Windows.Media.Brush)FindResource("OkBrush");
        AdressesText.Text = adresses.Count == 1
            ? "Le lien partira à cette adresse."
            : $"Le lien partira à ces {adresses.Count} adresses.";

        // le courriel n'est pas configuré : le dire ICI, pendant qu'on saisit, et non
        // après un téléversement de plusieurs minutes
        if (!App.Services.Mail.EstUtilisable)
        {
            AdressesText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            AdressesText.Text = "L'envoi par courriel n'est pas configuré sur ce poste : " +
                                App.Services.Mail.CeQuiManque() + ". Le lien sera seulement copié.";
        }
    }

    private void MettreAJourLeBouton() =>
        EnvoyerButton.IsEnabled =
            !_envoiEnCours && App.Services.Dropbox.EstUtilisable && LotChoisi().Count > 0;

    // ----- envoi -----

    private async void OnEnvoyer(object sender, RoutedEventArgs e)
    {
        if (_envoiEnCours) return;

        var lot = LotChoisi();
        if (lot.Count == 0) return;

        _envoiEnCours = true;
        _arret = new CancellationTokenSource();

        ChoixCarte.IsEnabled = false;
        ReglagesCarte.IsEnabled = false;
        ResultatCarte.Visibility = Visibility.Collapsed;
        AvancementCarte.Visibility = Visibility.Visible;
        ArreterButton.Visibility = Visibility.Visible;
        EnvoyerButton.IsEnabled = false;
        EtatText.Text = "";

        var avancement = new Progress<AvancementEnvoi>(a =>
        {
            AvancementBarre.Value = a.Part;
            AvancementText.Text = a.Faits >= a.Total
                ? "Création du lien de partage…"
                : $"Envoi {a.Faits + 1} / {a.Total} — {a.Fichier}";
        });

        try
        {
            var resultat = await DropboxTransfer.EnvoyerAsync(
                App.Services.Dropbox, lot, NomBox.Text, avancement, _arret.Token);

            MontrerLeResultat(resultat);

            // Le ménage suit l'envoi, et non l'inverse : c'est APRÈS avoir écrit qu'on sait
            // le compte encore assez grand pour la fois suivante. Détaché, parce que le
            // client attend son lien et non la fin d'un rangement.
            _ = Task.Run(() => DropboxMenage.FaireLeMenageAsync(App.Services.Dropbox));
        }
        catch (OperationCanceledException)
        {
            // L'arrêt laisse dans le Dropbox les photos DÉJÀ parties : le dire, sans quoi
            // l'opérateur croirait n'avoir rien envoyé et retrouverait un dossier à moitié
            // plein la fois suivante.
            EtatText.Text =
                "Envoi arrêté. Les photos déjà parties restent dans le Dropbox du studio : " +
                "supprimez le dossier depuis Dropbox si vous ne voulez pas les garder.";
        }
        catch (Exception ex)
        {
            FileLog.Write("Envoi Dropbox impossible", ex);
            EtatText.Text = ex.Message;
        }
        finally
        {
            _envoiEnCours = false;
            _arret?.Dispose();
            _arret = null;

            AvancementCarte.Visibility = Visibility.Collapsed;
            ArreterButton.Visibility = Visibility.Collapsed;
            ChoixCarte.IsEnabled = true;
            ReglagesCarte.IsEnabled = true;
            MettreAJourLeBouton();
        }
    }

    private void MontrerLeResultat(ResultatEnvoi resultat)
    {
        _dernierResultat = resultat;
        LienBox.Text = resultat.Url;
        ResultatCarte.Visibility = Visibility.Visible;

        var details = new List<string>
        {
            $"{resultat.Fichiers} photo(s), {resultat.Octets / 1024.0 / 1024:0.#} Mo",
            $"dossier « {resultat.Dossier} »",
        };

        var reglages = App.Services.Dropbox;

        // La date limite RÉELLE, celle qu'on annonce au client : la première des deux
        // échéances. Voir JoursDeValidite.
        if (JoursDeValidite(resultat, reglages) is { } jours)
            details.Add($"valable jusqu'au {DateTime.Now.AddDays(jours):dd/MM/yyyy}");
        else
            details.Add("sans date limite");

        // On dit ce qui a été OBTENU et non ce qui a été demandé : un lien qu'on croit
        // protégé alors qu'il ne l'est pas est pire qu'un lien qu'on sait ouvert.
        if (resultat.Protege) details.Add("protégé par mot de passe");
        else if (!string.IsNullOrWhiteSpace(reglages.MotDePasse))
            details.Add("SANS mot de passe (compte Dropbox gratuit)");

        ResultatDetailText.Text = string.Join(" · ", details);

        // le lien est copié d'office : c'est le geste suivant dans tous les cas, et
        // l'oublier ferait recommencer un envoi de plusieurs minutes
        CopierLeLien(silencieux: true);
        EtatText.Text = "Lien copié dans le presse-papier.";

        // et il part au client si son adresse a été prise pendant le téléversement
        if (AdressesSaisies().Count > 0) EnvoyerLeLienAuClient();
    }

    /// <summary>
    /// Le dernier envoi réussi. Sert au bouton de rattrapage : renvoyer le lien ne doit
    /// jamais retéléverser les photos.
    /// </summary>
    private ResultatEnvoi? _dernierResultat;

    private void OnEnvoyerLeLien(object sender, RoutedEventArgs e)
    {
        if (AdressesSaisies().Count == 0)
        {
            MessageBox.Show(
                "Saisissez d'abord l'adresse du client, dans le cadre au-dessus.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnvoyerLeLienAuClient();
    }

    /// <summary>
    /// Envoie le lien par courriel.
    ///
    /// L'échec ne remonte PAS en boîte de dialogue : les photos sont déjà en ligne et le
    /// lien est dans le presse-papier, donc rien n'est perdu — l'opérateur peut le donner
    /// autrement. Une alerte modale au milieu du comptoir ferait croire à un envoi manqué.
    /// </summary>
    private void EnvoyerLeLienAuClient()
    {
        if (_dernierResultat is not { } resultat) return;

        var adresses = AdressesSaisies();
        if (adresses.Count == 0) return;

        var reglages = App.Services.Dropbox;

        try
        {
            PhotoMailer.EnvoyerLeLien(
                App.Services.Mail,
                adresses,
                resultat.Url,
                resultat.Fichiers,
                nomClient: null,
                mot: string.IsNullOrWhiteSpace(MotBox.Text) ? null : MotBox.Text,
                joursDeValidite: JoursDeValidite(resultat, reglages),
                protege: resultat.Protege);

            EtatText.Text = adresses.Count == 1
                ? $"Lien envoyé à {adresses[0]}."
                : $"Lien envoyé à {adresses.Count} destinataires.";
        }
        catch (Exception ex)
        {
            FileLog.Write("Envoi du lien Dropbox par courriel impossible", ex);
            EtatText.Text =
                "Les photos sont bien en ligne et le lien est copié, mais le courriel n'est " +
                $"pas parti : {ex.Message}";
        }
    }

    /// <summary>
    /// Le nombre de jours pendant lesquels le client pourra RÉELLEMENT télécharger.
    ///
    /// Deux échéances courent en parallèle, et c'est la PREMIÈRE qui compte :
    ///
    /// 1. l'expiration du lien Dropbox — seulement si elle a été acceptée, ce qui demande
    ///    un compte payant (voir <see cref="ResultatEnvoi.Expire"/>) ;
    /// 2. le ménage automatique, qui supprime le dossier au bout de
    ///    <see cref="DropboxSettings.RetentionJours"/> jours — sur toutes les offres, y
    ///    compris gratuites.
    ///
    /// Le défaut corrigé le 05/08/2026 : seule la première était annoncée. Sur un compte
    /// gratuit elle n'existe pas, le courriel ne promettait donc rien, et le client
    /// découvrait un lien mort trois jours plus tard. Sur un compte payant, c'était pire —
    /// le message annonçait trente jours quand le ménage effaçait au bout de trois.
    /// </summary>
    private static int? JoursDeValidite(ResultatEnvoi resultat, DropboxSettings reglages)
    {
        var echeances = new List<int>();

        if (resultat.Expire && reglages.ExpirationJours > 0) echeances.Add(reglages.ExpirationJours);
        if (reglages.RetentionJours > 0) echeances.Add(reglages.RetentionJours);

        return echeances.Count > 0 ? echeances.Min() : null;
    }

    private void OnArreter(object sender, RoutedEventArgs e) => _arret?.Cancel();

    // ----- le lien -----

    private void OnCopierLeLien(object sender, RoutedEventArgs e) => CopierLeLien(silencieux: false);

    private void CopierLeLien(bool silencieux)
    {
        if (string.IsNullOrWhiteSpace(LienBox.Text)) return;

        try
        {
            Clipboard.SetText(LienBox.Text);
            if (!silencieux) EtatText.Text = "Lien copié dans le presse-papier.";
        }
        catch (Exception ex)
        {
            // le presse-papier peut être tenu par une autre application : ce n'est pas
            // grave, le lien reste lisible et sélectionnable à l'écran
            FileLog.Write("Copie du lien dans le presse-papier impossible", ex);
            if (!silencieux)
                EtatText.Text = "Le presse-papier n'a pas voulu : copiez le lien à la main ci-dessus.";
        }
    }

    private void OnOuvrirLeLien(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LienBox.Text)) return;

        try
        {
            Process.Start(new ProcessStartInfo(LienBox.Text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"Ouverture du lien impossible ({LienBox.Text})", ex);
            EtatText.Text = "Impossible d'ouvrir le navigateur.";
        }
    }

    private void OnOuvrirParametres(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SettingsView(), "Paramètres");

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();
}
