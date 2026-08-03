using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
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
            AdresseBox.Focus();
        };
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

    private void OnAdresseChanged(object sender, TextChangedEventArgs e) => MettreAJourLeBouton();

    /// <summary>
    /// Le bouton n'est actif que si l'envoi peut réellement aboutir : une adresse
    /// plausible, et un poste configuré.
    /// </summary>
    private void MettreAJourLeBouton() =>
        EnvoyerButton.IsEnabled =
            !_envoiEnCours
            && App.Services.Mail.EstUtilisable
            && AdresseRecevable(AdresseBox.Text);

    /// <summary>
    /// Contrôle volontairement grossier : une arobase entourée de quelque chose, et un
    /// point après. On ne cherche pas à valider une adresse — seul le serveur sait — mais
    /// à rattraper la faute de frappe évidente avant de facturer.
    /// </summary>
    private static bool AdresseRecevable(string? adresse)
    {
        if (string.IsNullOrWhiteSpace(adresse)) return false;

        var arobase = adresse.Trim().IndexOf('@');
        if (arobase <= 0) return false;

        var domaine = adresse.Trim()[(arobase + 1)..];
        return domaine.Contains('.') && !domaine.StartsWith('.') && !domaine.EndsWith('.');
    }

    private void OnOuvrirParametres(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SettingsView(), "Paramètres");

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private async void OnEnvoyer(object sender, RoutedEventArgs e)
    {
        if (_envoiEnCours) return;

        var destinataire = AdresseBox.Text.Trim();
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
        Mouse.OverrideCursor = Cursors.Wait;

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

                    PhotoMailer.Envoyer(reglages, destinataire, fichiers, mot);
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
