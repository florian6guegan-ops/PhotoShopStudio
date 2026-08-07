using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Printing;

namespace Studio.App.Views;

/// <summary>
/// Prévient le client que sa commande est prête à être retirée.
///
/// <b>Ce message ne livre rien.</b> Il annonce que les tirages attendent au comptoir, et
/// c'est tout : aucune pièce jointe. Joindre les photos reviendrait à les donner sans les
/// vendre — l'envoi des fichiers est une prestation à part, facturée, qui passe par
/// <see cref="MailSendView"/>.
///
/// L'écran montre le message TEL QUE LE CLIENT LE LIRA, avant l'envoi. Un courriel qui part
/// chez un client se relit avant, pas après.
/// </summary>
public partial class PrevenirClientView : UserControl
{
    private readonly Order _commande;
    private readonly string _quoi;
    private bool _envoiEnCours;

    /// <param name="commande">La commande dont on annonce la mise à disposition.</param>
    /// <param name="quoi">Ce qu'elle contient, en clair : « 24 tirages 10×15 ».</param>
    public PrevenirClientView(Order commande, string quoi)
    {
        ArgumentNullException.ThrowIfNull(commande);

        _commande = commande;
        _quoi = quoi;

        InitializeComponent();

        Loaded += (_, _) =>
        {
            ResumeText.Text = $"Commande {commande.DisplayNumber}" +
                              (string.IsNullOrWhiteSpace(commande.CustomerName)
                                  ? ""
                                  : $" — {commande.CustomerName}") +
                              $"\n{quoi}";

            VerifierLaConfiguration();
            Rafraichir();
            AdresseBox.Focus();
        };
    }

    /// <summary>
    /// Dit tout de suite si l'envoi est possible, plutôt qu'au moment d'appuyer.
    ///
    /// Même raison qu'à l'écran des Paramètres : une configuration absente doit se
    /// découvrir avant d'avoir promis quelque chose au client.
    /// </summary>
    private void VerifierLaConfiguration()
    {
        var reglages = App.Services.Mail;
        if (reglages.EstUtilisable) return;

        EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        EtatText.Text = "L'envoi par courriel n'est pas configuré : il manque " +
                        reglages.CeQuiManque() +
                        ".\nOuvrez Paramètres → Envoi par courriel.";
    }

    private void OnSaisieChangee(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Rafraichir();
    }

    /// <summary>Le bouton ne s'allume que sur une adresse plausible, et l'aperçu suit la saisie.</summary>
    private void Rafraichir()
    {
        var adresse = AdresseBox.Text.Trim();
        EnvoyerButton.IsEnabled = !_envoiEnCours
                                  && App.Services.Mail.EstUtilisable
                                  && AdressePlausible(adresse);

        ApercuText.Text = PhotoMailer.ApercuCommandePrete(
            _commande.DisplayNumber, _quoi, _commande.CustomerName,
            MotBox.Text, App.Services.Mail.NomExpediteur);
    }

    /// <summary>
    /// Un contrôle de FORME, volontairement grossier : une arobase et un point après elle.
    ///
    /// Valider une adresse pour de bon est impossible sans lui écrire, et un contrôle
    /// tatillon refuserait des adresses valides — ce serait pire que de laisser partir un
    /// message vers une adresse fautive, que le serveur signalera.
    /// </summary>
    private static bool AdressePlausible(string adresse)
    {
        var arobase = adresse.IndexOf('@');
        return arobase > 0
               && adresse.IndexOf('.', arobase) > arobase + 1
               && !adresse.EndsWith('.')
               && !adresse.Contains(' ');
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private async void OnEnvoyer(object sender, RoutedEventArgs e)
    {
        if (_envoiEnCours) return;

        var adresse = AdresseBox.Text.Trim();
        var mot = MotBox.Text;

        _envoiEnCours = true;
        EnvoyerButton.IsEnabled = false;
        Mouse.OverrideCursor = CurseurStudio.Attente;
        EtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        EtatText.Text = "Envoi en cours…";

        try
        {
            await Task.Run(() => PhotoMailer.PrevenirCommandePrete(
                App.Services.Mail, adresse, _commande.DisplayNumber, _quoi,
                _commande.CustomerName, mot));

            Mouse.OverrideCursor = null;

            FileLog.Write($"Commande {_commande.DisplayNumber} : client prévenu à {adresse}.");

            // On repart tout de suite : le geste est fait, et l'opérateur a un client
            // devant lui. La trace reste au journal.
            MessageBox.Show(
                $"Le client a été prévenu à {adresse}.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);

            Navigator.Back();
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            _envoiEnCours = false;

            FileLog.Write($"Commande {_commande.DisplayNumber} : impossible de prévenir {adresse}", ex);

            EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            EtatText.Text = ex.Message;

            Rafraichir();
        }
    }
}
