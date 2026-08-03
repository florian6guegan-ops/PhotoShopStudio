using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Mail;
using Studio.Printing;

namespace Studio.App.Views;

/// <summary>
/// Les réglages propres à CE poste — pour l'instant, l'envoi des photos par courriel.
///
/// Ils vivent dans les données du poste (<c>config\mail.json</c>) et non dans le dépôt,
/// qui est public : le mot de passe d'application n'a rien à y faire, et un secret poussé
/// par mégarde ne se rattrape pas.
///
/// C'est ce qui permet d'installer un second poste opérateur sans toucher au code : on
/// ouvre cet écran, on saisit, on essaie.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Montrer(App.Services.Mail);
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

    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        var reglages = Saisie();
        App.Services.SaveMail(reglages);
        DireOuEnEstLaConfiguration(reglages);

        MessageBox.Show("Réglages enregistrés.", "Paramètres",
            MessageBoxButton.OK, MessageBoxImage.Information);
        Navigator.Back();
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
