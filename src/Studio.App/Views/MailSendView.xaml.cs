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
    private readonly bool _revenirEnArriere;
    private readonly Action<Order>? _surEnvoi;
    private bool _envoiEnCours;

    /// <param name="photos">Les photos à envoyer. Le prix suit leur nombre.</param>
    /// <param name="nomClient">Nom porté sur la commande, s'il est connu.</param>
    /// <param name="revenirEnArriere">
    /// <b>Rendre la main à l'écran appelant au lieu de retourner à l'accueil.</b>
    ///
    /// L'envoi finissait toujours par <c>Navigator.Home</c>, qui VIDE la pile d'écrans.
    /// Depuis les photos d'identité, cela jetait la photo en cours : le client qui voulait
    /// ses photos par courriel ET sa planche imprimée devait tout refaire — rechercher le
    /// fichier, recadrer, régler. Signalé depuis la boutique le 13/08/2026.
    ///
    /// Or l'écran d'identité dit lui-même que les deux vont ensemble : « C'est une
    /// prestation à part, facturée à la photo : elle n'imprime rien, et imprimer n'envoie
    /// rien. Un client peut vouloir les deux, ou l'un des deux. »
    ///
    /// Le <see cref="Navigator"/> empile les instances vivantes : revenir en arrière rend
    /// l'écran d'identité tel qu'il était, recadrage et corrections compris.
    ///
    /// Faux par défaut — depuis les commandes, l'envoi CLÔT le geste et l'accueil est la
    /// bonne destination.
    /// </param>
    /// <param name="surEnvoi">
    /// Appelé quand l'envoi a RÉUSSI et qu'il est facturé, avec la commande.
    ///
    /// Sert à l'écran d'identité, qui porte alors sa photo à l'historique des trente jours.
    /// Le placer ici, et pas au clic, est la règle même de cet écran : « rien n'est facturé
    /// quand l'envoi échoue » — rien ne doit être historisé non plus.
    /// </param>
    public MailSendView(IReadOnlyList<PhotoAEnvoyer> photos, string? nomClient = null,
        bool revenirEnArriere = false, Action<Order>? surEnvoi = null)
    {
        _photos = photos;
        _nomClient = nomClient;
        _revenirEnArriere = revenirEnArriere;
        _surEnvoi = surEnvoi;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            AnnoncerLePrix();
            VerifierLaConfiguration();
            RemplirLesMessages();
            AdresseBox.Focus();

            // ⚠ LA PRÉPARATION COMMENCE ICI, PAS AU CLIC.
            //
            // Elle attendait le bouton « Envoyer » : l'opérateur tapait l'adresse, appuyait,
            // et regardait alors la caisse se figer le temps d'un rendu de 24 Mpx — six
            // secondes ici, soixante-seize à Arcueil le 19/08/2026. Or rien de ce travail ne
            // dépend de l'adresse : les trois fichiers sont entièrement décidés par la photo,
            // son cadrage et ses corrections, tous connus dès l'ouverture de l'écran.
            //
            // On les fabrique donc PENDANT qu'il tape. Le temps de saisir une adresse au
            // clavier couvre le rendu, et « Envoyer » ne fait plus que l'envoi.
            //
            // Rien n'est facturé ni envoyé pour autant — un opérateur qui revient en arrière
            // laisse simplement trois fichiers de plus dans le dossier du jour, exactement
            // comme un envoi refusé, et ils resservent tels quels s'il revient.
            LancerLaPreparation();
        };
    }

    /// <summary>
    /// Les trois fichiers de chaque photo, en cours de fabrication ou déjà prêts.
    /// Null tant que l'écran n'est pas chargé.
    /// </summary>
    private Task<PhotosDuClient[]>? _preparation;

    /// <summary>
    /// Le dossier du jour, décidé une fois : la préparation et l'envoi doivent nommer le
    /// même, sans quoi l'envoi refabriquerait tout à côté.
    /// </summary>
    private string? _dossierPrepare;

    /// <summary>
    /// Met en route la fabrication des fichiers, sans rien attendre.
    ///
    /// Les erreurs ne sont PAS avalées ici : elles restent dans la tâche et remontent à
    /// l'envoi, là où l'opérateur peut en faire quelque chose. Une photo illisible doit dire
    /// « envoi impossible » au moment de l'envoi, pas afficher une alerte pendant qu'on tape
    /// une adresse.
    /// </summary>
    private void LancerLaPreparation()
    {
        if (_preparation is not null) return;

        // Le dossier porte la date : les fichiers RESTENT après l'envoi, pour qu'un envoi
        // refusé se rejoue sans tout refabriquer — une photo de 24 Mpx coûte plusieurs
        // secondes de rendu.
        _dossierPrepare = Path.Combine(
            App.Services.DataRoot, "courriel", DateTime.Now.ToString("yyyy-MM-dd"));

        var horodatage = DateTime.Now.ToString("HHmmss");
        var photos = _photos;
        var dossier = _dossierPrepare;

        _preparation = Task.Run(() =>
        {
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            var lots = new PhotosDuClient[photos.Count];

            // DEUX À LA FOIS, pas davantage : une photo de 24 Mpx tient plusieurs centaines
            // de méga-octets dans ImageMagick le temps du rendu, et le détourage du fond
            // passe de toute façon par un verrou unique (le réseau ONNX ne se partage pas).
            // Au-delà, on échangerait du temps contre de la mémoire — c'est ce qui fait
            // tomber le poste, pas ce qui l'accélère.
            Parallel.For(0, photos.Count,
                new ParallelOptions { MaxDegreeOfParallelism = 2 },
                i =>
                {
                    var photo = photos[i];
                    lots[i] = PhotoMailer.Preparer(
                        photo.SourcePath, photo.Crop, photo.RotationQuarterTurns,
                        photo.FineRotationDegrees, photo.Adjustments, dossier,
                        $"{horodatage}-{i + 1:00}");
                });

            FileLog.Write(
                $"Courriel : {photos.Count} photo(s) préparée(s) en " +
                $"{chrono.Elapsed.TotalSeconds:0.0} s, pendant la saisie de l'adresse.");

            return lots;
        });
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

        try
        {
            var chrono = System.Diagnostics.Stopwatch.StartNew();

            // ⚠ ON RÉCUPÈRE, ON NE REFABRIQUE PAS.
            //
            // La préparation a démarré à l'ouverture de l'écran (voir LancerLaPreparation) :
            // le plus souvent elle est déjà finie quand l'opérateur a fini de taper, et cet
            // await rend la main aussitôt. Quand elle ne l'est pas, on attend ce qui reste —
            // jamais plus que ce qu'on aurait attendu de toute façon.
            LancerLaPreparation();
            var lots = await _preparation!;

            var prepare = chrono.Elapsed;
            EtatText.Text = "Envoi en cours…";

            // envoi sur un fil de fond : un serveur SMTP qui ne répond pas gèlerait la
            // caisse deux minutes (c'est le délai posé dans PhotoMailer)
            await Task.Run(() =>
            {
                // UNE SEULE connexion pour tous les messages : voir EnvoyerPlusieurs
                PhotoMailer.EnvoyerPlusieurs(reglages, adresses, lots, mot);

                FileLog.Write(
                    $"Courriel : envoi terminé en {chrono.Elapsed.TotalSeconds:0.0} s " +
                    $"(dont {prepare.TotalSeconds:0.0} s d'attente de la préparation).");
            });

            EtatText.Text = "Envoi effectué.";
            var commande = Facturer(destinataire);

            // La photo est partie : elle entre à l'historique des trente jours, si l'écran
            // appelant sait quoi y mettre. Un échec ici ne doit pas faire croire à un envoi
            // raté — les photos SONT chez le client.
            try
            {
                _surEnvoi?.Invoke(commande);
            }
            catch (Exception ex)
            {
                FileLog.Write("Photo non portée à l'historique des photos d'identité", ex);
            }

            Mouse.OverrideCursor = null;
            MessageBox.Show(
                $"Photos envoyées à {destinataire}.\n\n" +
                $"Commande {commande.DisplayNumber} — {commande.Total:0.00} €",
                "Envoi par courriel", MessageBoxButton.OK, MessageBoxImage.Information);

            if (_revenirEnArriere)
                Navigator.Back();
            else
                AccueilStudio.Rentrer();
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Envoi par courriel impossible", ex);

            // UNE PRÉPARATION EN ÉCHEC NE DOIT PAS SE REJOUER TELLE QUELLE : une tâche
            // fautive garde son exception pour toujours, et « réessayer » rendrait
            // indéfiniment la même erreur sans jamais retoucher au disque. On la jette, le
            // prochain essai repart d'une préparation neuve.
            if (_preparation is { IsFaulted: true }) _preparation = null;

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
