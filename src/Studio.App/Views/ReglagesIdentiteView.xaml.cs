using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Studio.App.Infrastructure;
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

        DireOuVontLesPhotos();
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
        DossierRadio.IsChecked == true ? DossierBox.Text.Trim() : "");

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

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();
}
