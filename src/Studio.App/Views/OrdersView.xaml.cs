using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;
using Studio.Store.DiLand;

namespace Studio.App.Views;

public partial class OrdersView : UserControl
{
    /// <summary>Ce que l'onglet retenu laisse voir.</summary>
    private enum Genre
    {
        Tout,

        /// <summary>Tous les tirages, quelle qu'en soit l'origine.</summary>
        Tirages,

        /// <summary>Les tirages préparés au comptoir.</summary>
        TiragesOperateur,

        /// <summary>Les tirages venus d'une borne.</summary>
        TiragesBorne,

        Identite,
    }

    /// <summary>
    /// Une commande vient-elle du comptoir ?
    ///
    /// La règle est prise à l'ENVERS — tout ce qui n'est pas l'opérateur vient d'une borne
    /// — parce que le champ <c>Source</c> est une chaîne libre et que les bornes s'y
    /// nomment de plusieurs façons : « borne » pour une reprise DiLand, « Borne1 » ou le
    /// nom donné à la borne dans sa configuration pour une commande Studio. Il n'y a en
    /// revanche qu'une seule façon d'être l'opérateur, et c'est ce code qui l'écrit.
    /// </summary>
    private const string SourceOperateur = "Operateur";

    private static bool DuComptoir(Order commande) =>
        string.Equals(commande.Source, SourceOperateur, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Les commandes lues au dernier passage. Gardées pour que changer d'onglet ne
    /// relise pas le disque : `ScanRecent` parcourt sept jours de dossiers.
    /// </summary>
    private List<Order> _commandes = [];

    public OrdersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _commandes = App.Services.Store.ScanRecent(days: 7)
            .OrderByDescending(o => o.CreatedAt)
            .ToList();

        // les compteurs comptent les ENVELOPPES, pas les commandes : une commande mixte
        // pèse dans les deux onglets, et annoncer « 3 » de part et d'autre pour trois
        // commandes dont une seule est une planche serait faux
        OngletTirages.Content = $"Tirages photo ({Compter(Genre.Tirages)})";
        OngletTiragesOperateur.Content = $"↳ opérateur ({Compter(Genre.TiragesOperateur)})";
        OngletTiragesBorne.Content = $"↳ borne ({Compter(Genre.TiragesBorne)})";
        OngletIdentite.Content = $"Photos d'identité ({Compter(Genre.Identite)})";

        Afficher();
    }

    private int Compter(Genre genre) =>
        _commandes.Sum(o => o.Envelopes.Count(e => Retenue(o, e, genre)));

    private Genre OngletRetenu =>
        OngletTirages.IsChecked == true ? Genre.Tirages
        : OngletTiragesOperateur.IsChecked == true ? Genre.TiragesOperateur
        : OngletTiragesBorne.IsChecked == true ? Genre.TiragesBorne
        : OngletIdentite.IsChecked == true ? Genre.Identite
        : Genre.Tout;

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Afficher();
    }

    private void Afficher()
    {
        var genre = OngletRetenu;

        // une commande ne paraît que si elle a quelque chose à montrer dans cet onglet,
        // et n'y montre alors que les enveloppes concernées
        var lignes = _commandes
            .Select(o => new OrderRow(o, o.Envelopes.Where(e => Retenue(o, e, genre)).ToList()))
            .Where(r => r.Envelopes.Count > 0)
            .ToList();

        OrdersList.ItemsSource = lignes;

        EmptyText.Text = genre switch
        {
            Genre.Tirages => "Aucun tirage photo ces derniers jours.",
            Genre.TiragesOperateur => "Aucun tirage préparé au comptoir ces derniers jours.",
            Genre.TiragesBorne => "Aucun tirage venu d'une borne ces derniers jours.",
            Genre.Identite => "Aucune planche de photos d'identité ces derniers jours.",
            _ => "Aucune commande ces derniers jours.",
        };
        EmptyText.Visibility = lignes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Cette enveloppe a-t-elle sa place dans l'onglet retenu ?
    ///
    /// Le tri se fait par ENVELOPPE et non par ligne, parce que c'est l'enveloppe qu'on
    /// réimprime : n'en montrer que la moitié laisserait croire qu'on peut ne retirer que
    /// celle-là. Une enveloppe qui mêle une planche et des tirages — le cas quand les deux
    /// sortent sur la même machine — paraît donc dans les DEUX onglets, entière. Rien ne
    /// disparaît, et le bouton dit la vérité sur ce qu'il va sortir.
    /// </summary>
    /// <remarks>
    /// L'origine, elle, se lit sur la COMMANDE et non sur l'enveloppe : c'est la commande
    /// entière qui vient du comptoir ou d'une borne.
    /// </remarks>
    private static bool Retenue(Order commande, Envelope enveloppe, Genre genre) => genre switch
    {
        Genre.Tirages => EstDesTirages(enveloppe),
        Genre.TiragesOperateur => EstDesTirages(enveloppe) && DuComptoir(commande),
        Genre.TiragesBorne => EstDesTirages(enveloppe) && !DuComptoir(commande),
        Genre.Identite => enveloppe.Lines.Any(EstIdentite),
        _ => true,
    };

    private static bool EstDesTirages(Envelope enveloppe) =>
        enveloppe.Lines.Any(l => !EstIdentite(l));

    /// <summary>
    /// Une ligne de planche d'identité.
    ///
    /// On interroge d'abord le CATALOGUE : un produit à <c>Sheet</c> est une planche, et
    /// c'est la seule définition qui vaille. Repli sur la taille de case portée par
    /// l'article, pour les commandes enregistrées avant que ce champ existe — et pour
    /// celles dont le produit a été supprimé du catalogue depuis.
    /// </summary>
    private static bool EstIdentite(OrderLine ligne) =>
        App.Services.Catalog.Find(ligne.ProductCode)?.Sheet is not null
        || ligne.Items.Any(i => i.SheetCellWidthMm is > 0);

    // ----- retourner aux photos d'une commande -----

    /// <summary>
    /// Le dossier des photos d'origine d'une commande, ou null en le disant.
    ///
    /// Elles sont TOUJOURS recopiées à la création de la commande (voir
    /// <c>OrderFolderStore</c>) : le client peut avoir débranché sa clé USB depuis, et une
    /// réimpression qui dépendrait de son support serait inutilisable. C'est ce qui permet
    /// de ressortir les fichiers ou de les retravailler des jours plus tard.
    /// </summary>
    private static string? DossierDesPhotos(OrderRow ligne)
    {
        var dossier = App.Services.Store.GetPhotosFolder(ligne.Order);

        if (Directory.Exists(dossier) && Directory.EnumerateFiles(dossier).Any())
            return dossier;

        MessageBox.Show(
            $"Les photos de la commande {ligne.Order.DisplayNumber} ne sont plus sur le disque.\n\n" +
            "Elles ont sans doute été archivées : les commandes de plus de trente jours sont " +
            "déplacées dans le dossier d'archive.",
            "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>
    /// Recopie les photos de la commande dans les téléchargements, <b>même si elles l'ont
    /// déjà été</b> : c'est le geste qu'on refait quand un client redemande ses fichiers.
    ///
    /// Les originaux, et non les rendus : c'est ce dont le client a besoin pour faire
    /// tirer ailleurs ou retoucher lui-même.
    /// </summary>
    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow ligne) return;
        if (DossierDesPhotos(ligne) is not { } source) return;

        var telechargements = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (!Directory.Exists(telechargements))
            telechargements = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var destination = Path.Combine(telechargements,
            $"Commande-{ligne.Order.DisplayNumber}-{ligne.Order.CreatedAt:yyyy-MM-dd-HHmm}");

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

            Directory.CreateDirectory(destination);
            foreach (var fichier in Directory.EnumerateFiles(source))
                File.Copy(fichier, Path.Combine(destination, Path.GetFileName(fichier)),
                    overwrite: true);

            Mouse.OverrideCursor = null;

            // on ouvre le dossier : sans cela l'opérateur doit aller le chercher, et rien
            // à l'écran ne lui dit où il est
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Commandes du jour : téléchargement impossible", ex);
            MessageBox.Show($"Téléchargement impossible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Rouvre les photos de la commande pour les recadrer et les corriger — le même écran
    /// que « Modifier » sur une commande de borne.
    ///
    /// <b>La commande d'origine n'est pas touchée.</b> Un tirage depuis cet écran donnera
    /// une NOUVELLE commande, avec son numéro et son prix : c'est ce qu'il faut, parce
    /// qu'une commande déjà encaissée ne doit pas changer de contenu ni de montant. Le
    /// bouton le dit dans son infobulle.
    /// </summary>
    private void OnModify(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow ligne) return;
        if (DossierDesPhotos(ligne) is not { } source) return;

        var combien = Directory.EnumerateFiles(source).Count();

        // Une planche d'identité se reprend dans l'écran d'IDENTITÉ, pas dans celui des
        // tirages.
        //
        // « Modifier » ouvrait la grille pour tout le monde. Sur une planche, l'opérateur
        // y trouvait le recadrage des tirages — cadre libre, pas de gabarit, pas de repères
        // de crâne et de menton — c'est-à-dire précisément ce qui ne permet PAS de refaire
        // une photo d'identité. Or c'est le seul motif de rouvrir une planche : le guichet
        // a refusé le cadrage. Signalé par l'exploitant le 04/08/2026.
        if (DocumentDeLaPlanche(ligne) is { } document)
        {
            Navigator.Go(
                new IdPhotoView(source, document, avecSousDossiers: false),
                $"Commande {ligne.Order.DisplayNumber} — {document.Country}, " +
                $"{document.WidthMm:0.#}×{document.HeightMm:0.#} mm");
            return;
        }

        Navigator.Go(
            new PhotoGridView(source, ProduitMajoritaire(ligne.Order), avecSousDossiers: false),
            $"Commande {ligne.Order.DisplayNumber} — {combien} photo(s)");
    }

    /// <summary>
    /// La norme visée par la planche d'identité de cette commande, ou null si ce n'est pas
    /// une planche.
    ///
    /// Elle se déduit de la TAILLE DE CASE enregistrée sur l'article
    /// (<c>OrderItem.SheetCellWidthMm</c>) : c'est tout ce que la commande garde du
    /// document, et c'est suffisant — la géométrie du cadrage ne dépend que des cotes et
    /// des bornes de visage.
    ///
    /// Deux replis, dans cet ordre :
    ///
    /// 1. les commandes enregistrées avant que ce champ existe ne portent pas de case ; on
    ///    prend alors celle du PRODUIT, qui n'en connaît qu'une, mais qui est la bonne dans
    ///    l'immense majorité des cas — la boutique tire du 35 × 45 ;
    /// 2. une cote absente du référentiel donne quand même un document utilisable, sans
    ///    bornes de visage : mieux vaut un gabarit à la bonne taille sans contrôle de
    ///    conformité que l'écran des tirages.
    /// </summary>
    private static IdDocumentSpec? DocumentDeLaPlanche(OrderRow ligne)
    {
        var articles = ligne.Retenues
            .SelectMany(e => e.Lines)
            .Where(EstIdentite)
            .ToList();

        if (articles.Count == 0) return null;

        var largeur = articles
            .SelectMany(l => l.Items)
            .Select(i => i.SheetCellWidthMm ?? 0)
            .FirstOrDefault(v => v > 0);

        var hauteur = articles
            .SelectMany(l => l.Items)
            .Select(i => i.SheetCellHeightMm ?? 0)
            .FirstOrDefault(v => v > 0);

        if (largeur <= 0 || hauteur <= 0)
        {
            var sheet = App.Services.Catalog.Find(articles[0].ProductCode)?.Sheet;
            largeur = sheet?.CellWidthMm ?? 0;
            hauteur = sheet?.CellHeightMm ?? 0;
        }

        if (largeur <= 0 || hauteur <= 0) return IdDocumentSpec.France;

        return ReferentielIdentite.ParLesCotes(largeur, hauteur)
               ?? new IdDocumentSpec("Reprise", "planche enregistrée", largeur, hauteur, 0, 0);
    }

    /// <summary>
    /// Le produit à présélectionner : celui qui pèse le plus de tirages dans la commande.
    ///
    /// Sur une commande de soixante 10×15 et d'un 13×18, présélectionner le 10×15 évite
    /// soixante corrections à la main. Un produit disparu du catalogue rend null, et
    /// l'écran demandera alors le format.
    /// </summary>
    private static string? ProduitMajoritaire(Order commande)
    {
        var code = commande.Envelopes
            .SelectMany(e => e.Lines)
            .GroupBy(l => l.ProductCode)
            .OrderByDescending(g => g.Sum(l => l.TotalPrints))
            .Select(g => g.Key)
            .FirstOrDefault();

        return code is not null && App.Services.Catalog.Find(code) is not null ? code : null;
    }

    /// <summary>
    /// Envoie par courriel les photos d'identité d'une commande déjà passée, avec le
    /// cadrage et les corrections telles qu'elles ont été tirées.
    ///
    /// <b>Le même module que l'écran identité</b> — <see cref="MailSendView"/> — et non une
    /// seconde version : le prix, les messages prédéfinis, le contrôle de configuration et
    /// les trois fichiers envoyés (photo entière, cadrage léger, cadrage pleine résolution)
    /// doivent rester les mêmes des deux côtés.
    ///
    /// Ce sont les ORIGINAUX de la commande qui repartent, jamais les rendus : la commande
    /// garde ses photos dans son dossier (voir <see cref="DossierDesPhotos"/>), et c'est
    /// d'elles que le client a besoin.
    /// </summary>
    private void OnSendByMail(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow ligne) return;
        if (DossierDesPhotos(ligne) is not { } dossier) return;

        var photos = ligne.Retenues
            .SelectMany(env => env.Lines)
            .Where(EstIdentite)
            .SelectMany(l => l.Items)
            .Select(item => (Item: item, Chemin: Path.Combine(dossier, item.FileName)))
            .Where(x => File.Exists(x.Chemin))
            .Select(x => new MailSendView.PhotoAEnvoyer(
                x.Chemin,
                x.Item.Crop,
                x.Item.RotationQuarterTurns,
                x.Item.FineRotationDegrees,
                x.Item.Adjustments))
            .ToList();

        if (photos.Count == 0)
        {
            MessageBox.Show(
                "Les fichiers de cette commande ne sont plus sur le disque.\n\n" +
                "Les commandes de plus de trente jours sont déplacées dans le dossier " +
                "d'archive : il faut les en ressortir pour pouvoir les envoyer.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Navigator.Go(
            new MailSendView(photos, ligne.Order.CustomerName),
            $"Commande {ligne.Order.DisplayNumber} — envoyer par courriel");
    }

    /// <summary>
    /// Prévient le client que sa commande l'attend en magasin.
    ///
    /// Sans les photos : ce message annonce, il ne livre pas. L'envoi des fichiers est une
    /// prestation à part, facturée — c'est le bouton « ✉ Envoyer ».
    /// </summary>
    private void OnPrevenirClient(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow ligne) return;

        Navigator.Go(
            new PrevenirClientView(ligne.Order, DecrireLeContenu(ligne)),
            $"Commande {ligne.Order.DisplayNumber} — prévenir le client");
    }

    /// <summary>
    /// Ce que la commande contient, en une phrase que le client comprendra.
    ///
    /// En PRODUITS et en nombre de tirages : « 24 tirages 10x15 et 1 planche identité ».
    /// Le client ne connaît ni les enveloppes, ni les codes du catalogue.
    /// </summary>
    private static string DecrireLeContenu(OrderRow ligne)
    {
        var morceaux = ligne.Retenues
            .SelectMany(e => e.Lines)
            .GroupBy(l => App.Services.Catalog.Find(l.ProductCode)?.Name ?? l.ProductCode)
            .Select(g =>
            {
                var tirages = g.Sum(l => l.TotalPrints);
                return $"{tirages} × {g.Key}";
            })
            .ToList();

        return morceaux.Count == 0 ? "votre commande" : string.Join(", ", morceaux);
    }

    private async void OnPrintTicket(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OrderRow row) return;
        var services = App.Services;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Run(() => Studio.Printing.EscPosTicket.Send(
                Studio.Printing.EscPosTicket.Build(row.Order, services.Catalog, services.Ticket),
                services.Ticket));
            Mouse.OverrideCursor = null;
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(
                $"Ticket non imprimé : {ex.Message}\n\n" +
                $"Vérifiez l'imprimante ticket ({services.Ticket.Host}).",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Réimprime une enveloppe, <b>par le suivi des impressions</b> comme tout le reste.
    ///
    /// Elle appelait <c>PrintEnvelope</c> dans un <c>Task.Run</c> à elle, hors du suivi.
    /// Deux conséquences, vues sur la commande 04-032 du 04/08/2026 :
    ///
    /// 1. <b>le verdict de la machine se perdait</b> — <c>SuiviImpressions.TirageTermine</c>
    ///    cherche le travail par son numéro de commande, n'en trouvait aucun, et le motif
    ///    du refus n'arrivait jamais à l'écran. Le minilab avait pourtant répondu ;
    /// 2. <b>« Enveloppe réimprimée » s'affichait dès l'ENVOI</b>, avant le moindre tirage.
    ///    Sur le minilab, le verdict arrive dix secondes plus tard : le message annonçait
    ///    donc une réussite qu'il ne pouvait pas connaître.
    ///
    /// Plus de boîte de dialogue à la fin : l'avancement et l'issue se lisent dans le
    /// bandeau, comme pour une commande neuve.
    /// </summary>
    private void OnReprint(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not EnvelopeRow row) return;

        var answer = MessageBox.Show(
            $"Réimprimer l'enveloppe {row.Envelope.Number} de la commande {row.Order.DisplayNumber} " +
            $"({row.Envelope.PrinterChannel}) ?\n\nLes tirages sortiront une nouvelle fois.",
            "Réimpression", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        App.Services.Impressions.Lancer(row.Order,
            imprimer: (avancement, arret) =>
                App.Services.Printer.PrintEnvelope(row.Order, row.Envelope,
                    operatorConfirmed: true, progression: avancement, ct: arret),
            apresSucces: Refresh);
    }

    /// <summary>
    /// Ouvre la file des agrandissements limitée à cette enveloppe : l'opérateur y tire
    /// chaque image sur l'Epson, puis confirme.
    /// </summary>
    private void OnPrintLargeFormat(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not EnvelopeRow row) return;

        Navigator.Go(new LargeFormatQueueView(row.Order, row.Envelope),
            $"Agrandissements — commande {row.Order.DisplayNumber}");
    }

    /// <param name="Retenues">
    /// Les seules enveloppes que l'onglet laisse voir. Elles ne sont PAS recalculées ici :
    /// c'est l'affichage qui décide de ce qu'il montre, la ligne ne fait que le porter.
    /// </param>
    private sealed record OrderRow(Order Order, IReadOnlyList<Envelope> Retenues)
    {
        public string Header =>
            $"N° {Order.DisplayNumber} — {Order.CreatedAt:ddd dd/MM HH:mm} — {Order.Total:0.00} €";

        /// <summary>
        /// L'origine, en un mot. Le nom brut de la borne est gardé quand il en dit plus
        /// que « Borne » — une boutique à deux bornes veut savoir laquelle.
        /// </summary>
        public string OrigineTexte => DuComptoir(Order)
            ? "Comptoir"
            : Order.Source switch
            {
                null or "" => "Borne",
                // « borne » tout court est la reprise d'une commande DiLand ; les bornes
                // Studio, elles, donnent leur nom
                var s when string.Equals(s, DiLandImporter.SourceName, StringComparison.OrdinalIgnoreCase)
                    => "Borne",
                var s => s,
            };

        public Brush OrigineBrush => DuComptoir(Order)
            ? (Brush)Application.Current.Resources["AccentDarkBrush"]
            : new SolidColorBrush(Color.FromRgb(0x6A, 0x4C, 0x93));

        public string StatusText => Order.Status switch
        {
            OrderStatus.Draft => "Brouillon",
            OrderStatus.Submitted => "À traiter",
            OrderStatus.InReview => "En cours",
            OrderStatus.Printing => "Impression…",
            OrderStatus.Ready => "Prête",
            OrderStatus.Delivered => "Remise",
            OrderStatus.Cancelled => "Annulée",
            _ => Order.Status.ToString(),
        };

        public Brush StatusBrush => Order.Status switch
        {
            OrderStatus.Ready or OrderStatus.Delivered => (Brush)Application.Current.Resources["OkBrush"],
            OrderStatus.Cancelled => (Brush)Application.Current.Resources["DangerBrush"],
            _ => (Brush)Application.Current.Resources["AccentBrush"],
        };

        public List<EnvelopeRow> Envelopes =>
            Retenues.Select(env => new EnvelopeRow(Order, env)).ToList();

        /// <summary>
        /// L'envoi par courriel ne s'affiche que sur les planches d'IDENTITÉ.
        ///
        /// Ce n'est pas une restriction technique — le module enverrait n'importe quelle
        /// photo — mais l'usage : c'est la photo d'identité qu'un client redemande par
        /// courriel, pour une démarche en ligne. Sur un paquet de soixante 10×15, le bouton
        /// n'aurait aucun sens et se cliquerait par erreur.
        /// </summary>
        public Visibility MailVisibility =>
            Retenues.SelectMany(e => e.Lines).Any(EstIdentite)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private sealed record EnvelopeRow(Order Order, Envelope Envelope)
    {
        public string Label
        {
            get
            {
                var prints = Envelope.Lines.Sum(l => l.TotalPrints);
                var status = Envelope.Status switch
                {
                    EnvelopeStatus.Pending => "en attente",
                    EnvelopeStatus.Rendering => "préparation…",
                    EnvelopeStatus.Spooled => "envoyée à l'imprimante",
                    EnvelopeStatus.Printed => "imprimée",
                    EnvelopeStatus.Error => $"ERREUR : {Envelope.Error}",
                    EnvelopeStatus.AwaitingManualPrint => "à tirer sur l'Epson",
                    _ => Envelope.Status.ToString(),
                };
                return $"Enveloppe {Envelope.Number} — {Envelope.PrinterChannel} — {prints} tirage(s) — {status}";
            }
        }

        /// <summary>Une enveloppe d'agrandissements se tire depuis la boîte grand format, pas par le spouleur.</summary>
        public bool IsLargeFormat => Envelope.Status == EnvelopeStatus.AwaitingManualPrint;

        public Visibility LargeFormatVisibility => IsLargeFormat ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ReprintVisibility => IsLargeFormat ? Visibility.Collapsed : Visibility.Visible;
    }
}
