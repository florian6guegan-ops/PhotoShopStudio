using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Core.Mail;
using Studio.Printing;
using Studio.Store;

namespace Studio.App.Views;

/// <summary>
/// Envoi des photos au client par courriel — une prestation facturée, à 5,00 € par photo.
///
/// Trois fichiers partent, mais c'est UNE photo : le client paie la photo qu'on lui
/// envoie, pas les versions qu'on en tire. Les trois existent parce qu'elles ne servent
/// pas à la même chose (voir <see cref="PhotoMailer"/>), et découper le prix en trois
/// serait incompréhensible au comptoir.
///
/// Une commande Studio est créée, comme pour un tirage : c'est ce qui met la prestation
/// au ticket, dans le total du jour et dans les statistiques. Son enveloppe n'imprime
/// rien et se clôt aussitôt (voir <see cref="ProductOutput.Email"/>).
/// </summary>
public partial class MailSendView : UserControl
{
    /// <summary>Une photo à envoyer, telle que l'écran appelant l'a préparée.</summary>
    /// <param name="SourcePath">Fichier d'origine.</param>
    /// <param name="Crop">Cadrage retenu par l'opérateur.</param>
    /// <param name="RotationQuarterTurns">Quarts de tour appliqués.</param>
    /// <param name="FineRotationDegrees">Redressement fin, en degrés.</param>
    /// <param name="Adjustments">Corrections d'image, fond blanc compris.</param>
    public sealed record PhotoAEnvoyer(
        string SourcePath,
        CropSpec Crop,
        int RotationQuarterTurns,
        double FineRotationDegrees,
        ImageAdjustments Adjustments);

    private readonly IReadOnlyList<PhotoAEnvoyer> _photos;
    private readonly string? _nomClient;
    private bool _envoiEnCours;

    /// <param name="photos">Les photos à envoyer. Le prix suit leur nombre.</param>
    /// <param name="nomClient">Nom porté sur la commande, s'il est connu.</param>
    public MailSendView(IReadOnlyList<PhotoAEnvoyer> photos, string? nomClient = null)
    {
        _photos = photos;
        _nomClient = nomClient;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            AnnoncerLePrix();
            VerifierLaConfiguration();
            RemplirLesMessages();
            AdresseBox.Focus();
        };
    }

    /// <summary>
    /// Les mots prêts à joindre, réglés dans Paramètres.
    ///
    /// Une entrée vide ouvre la liste : sans elle, on ne pourrait pas revenir à « pas de
    /// message » après en avoir choisi un, et la case resterait remplie sans qu'on sache
    /// comment la vider.
    /// </summary>
    private void RemplirLesMessages()
    {
        var messages = MailMessages.Load(App.Services.ConfigDir);

        MessagesCombo.ItemsSource = new[] { new MessagePredefini("— aucun message —", "") }
            .Concat(messages)
            .ToList();

        MessagesCombo.SelectedIndex = 0;
        MessagesCombo.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMessagePredefini(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesCombo.SelectedItem is MessagePredefini choix)
            MotBox.Text = choix.Texte;
    }

    /// <summary>
    /// Le prix, annoncé AVANT que le client s'engage.
    ///
    /// Il vient du catalogue et non d'une constante : l'exploitant le change au Catalogue
    /// comme n'importe quel tarif, sans qu'on recompile.
    /// </summary>
    private void AnnoncerLePrix()
    {
        var produit = App.Services.ProduitEnvoiCourriel();
        var total = produit.UnitPriceFor(_photos.Count) * _photos.Count;

        PrixText.Text = $"{total:0.00} €";
        PrixDetailText.Text = _photos.Count == 1
            ? $"1 photo envoyée — {produit.Price:0.00} € la photo."
            : $"{_photos.Count} photos envoyées — {produit.Price:0.00} € la photo.";
    }

    /// <summary>
    /// Dit tout de suite si l'envoi est configuré sur ce poste, plutôt que de laisser
    /// découvrir le problème une fois l'adresse saisie et le prix annoncé au client.
    /// </summary>
    private void VerifierLaConfiguration()
    {
        var reglages = App.Services.Mail;
        if (reglages.EstUtilisable)
        {
            AvertissementConfig.Visibility = Visibility.Collapsed;
            return;
        }

        AvertissementConfig.Visibility = Visibility.Visible;
        AvertissementText.Text =
            "L'envoi par courriel n'est pas configuré sur ce poste : " +
            reglages.CeQuiManque() + ".";
        MettreAJourLeBouton();
    }

    private void OnAdresseChanged(object sender, TextChangedEventArgs e)
    {
        AnnoncerLesAdresses();
        MettreAJourLeBouton();
    }

    /// <summary>Les adresses saisies, dans l'ordre, sans doublon.</summary>
    private IReadOnlyList<string> AdressesSaisies() => Destinataires.Analyser(AdresseBox.Text);

    /// <summary>
    /// Dit ce qui va partir, et à qui.
    ///
    /// Une adresse mal tapée est NOMMÉE plutôt que de simplement griser le bouton :
    /// « envoyer » désactivé sans un mot, sur trois adresses dont une fausse, ne dit pas
    /// laquelle reprendre.
    /// </summary>
    private void AnnoncerLesAdresses()
    {
        var adresses = AdressesSaisies();
        var douteuses = adresses.Where(a => !Destinataires.Recevable(a)).ToList();

        if (adresses.Count == 0)
        {
            AdressesText.Text = "";
            return;
        }

        if (douteuses.Count > 0)
        {
            AdressesText.Text = douteuses.Count == 1
                ? $"Adresse douteuse : {douteuses[0]}"
                : "Adresses douteuses : " + string.Join(", ", douteuses);
            AdressesText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            return;
        }

        AdressesText.Text = adresses.Count == 1
            ? "1 destinataire."
            : $"{adresses.Count} destinataires — un seul message, en copie cachée.";
        AdressesText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    /// <summary>
    /// Le bouton n'est actif que si l'envoi peut réellement aboutir : au moins une adresse,
    /// TOUTES plausibles, et un poste configuré.
    ///
    /// Toutes, et non « au moins une » : un envoi part en bloc, et une adresse fausse au
    /// milieu ferait refuser le message entier par le serveur après qu'on a facturé.
    /// </summary>
    private void MettreAJourLeBouton()
    {
        var adresses = AdressesSaisies();

        EnvoyerButton.IsEnabled =
            !_envoiEnCours
            && App.Services.Mail.EstUtilisable
            && adresses.Count > 0
            && adresses.All(Destinataires.Recevable);
    }

    private void OnOuvrirParametres(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SettingsView(), "Paramètres");

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private async void OnEnvoyer(object sender, RoutedEventArgs e)
    {
        if (_envoiEnCours) return;

        var adresses = AdressesSaisies();
        if (adresses.Count == 0) return;

        // ce qu'on lit dans la confirmation, au ticket et dans le journal
        var destinataire = string.Join(", ", adresses);
        var mot = MotBox.Text;
        var reglages = App.Services.Mail;

        var reponse = MessageBox.Show(
            $"Envoyer {_photos.Count} photo(s) à {destinataire} ?\n\n" +
            $"{PrixText.Text} seront portés à la commande.",
            "Envoi par courriel", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (reponse != MessageBoxResult.Yes) return;

        _envoiEnCours = true;
        EnvoyerButton.IsEnabled = false;
        RetourButton.IsEnabled = false;
        EtatText.Text = "Préparation des fichiers…";
        Mouse.OverrideCursor = CurseurStudio.Attente;

        // Le dossier porte la date : les fichiers RESTENT après l'envoi, pour qu'un envoi
        // refusé se rejoue sans tout refabriquer — une photo de 24 Mpx coûte plusieurs
        // secondes de rendu.
        var dossier = Path.Combine(
            App.Services.DataRoot, "courriel", DateTime.Now.ToString("yyyy-MM-dd"));

        try
        {
            // préparation ET envoi sur un fil de fond : un serveur SMTP qui ne répond pas
            // gèlerait la caisse deux minutes (c'est le délai posé dans PhotoMailer)
            await Task.Run(() =>
            {
                var i = 0;
                foreach (var photo in _photos)
                {
                    var nomDeBase = $"{DateTime.Now:HHmmss}-{++i:00}";

                    var fichiers = PhotoMailer.Preparer(
                        photo.SourcePath, photo.Crop, photo.RotationQuarterTurns,
                        photo.FineRotationDegrees, photo.Adjustments, dossier, nomDeBase);

                    PhotoMailer.Envoyer(reglages, adresses, fichiers, mot);
                }
            });

            EtatText.Text = "Envoi effectué.";
            var commande = Facturer(destinataire);

            Mouse.OverrideCursor = null;
            MessageBox.Show(
                $"Photos envoyées à {destinataire}.\n\n" +
                $"Commande {commande.DisplayNumber} — {commande.Total:0.00} €",
                "Envoi par courriel", MessageBoxButton.OK, MessageBoxImage.Information);

            Navigator.Home(new HomeView(), "Studio Photo");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Envoi par courriel impossible", ex);

            // RIEN n'est facturé quand l'envoi échoue : le client n'a pas ses photos.
            EtatText.Text = "Envoi impossible — rien n'a été facturé.";
            MessageBox.Show(
                ex.Message + "\n\nLes fichiers préparés sont conservés : vous pouvez réessayer.",
                "Envoi par courriel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _envoiEnCours = false;
            RetourButton.IsEnabled = true;
            MettreAJourLeBouton();
        }
    }

    /// <summary>
    /// Porte la prestation à une commande Studio : ticket, total du jour, statistiques.
    ///
    /// Une photo par article, et non un article de quantité N : c'est ce qui permet de
    /// relire plus tard QUELLES photos sont parties, et pas seulement combien.
    /// </summary>
    private Order Facturer(string destinataire)
    {
        var produit = App.Services.ProduitEnvoiCourriel();

        var articles = _photos
            .Select(p => new DraftItem(
                SourcePath: p.SourcePath,
                Product: produit,
                Quantity: 1,
                Crop: p.Crop,
                RotationQuarterTurns: p.RotationQuarterTurns,
                FineRotationDegrees: p.FineRotationDegrees,
                FitOverride: null,
                Adjustments: p.Adjustments))
            .ToList();

        var commande = App.Services.Orders.CreateOrder(
            "Operateur", articles,
            customerName: string.IsNullOrWhiteSpace(_nomClient) ? destinataire : _nomClient);

        // l'enveloppe n'imprime rien et se clôt d'elle-même : la commande doit passer
        // « Prête », sans quoi elle traînerait indéfiniment dans les commandes du jour
        foreach (var enveloppe in commande.Envelopes)
            App.Services.Printer.PrintEnvelope(commande, enveloppe);

        return commande;
    }
}
