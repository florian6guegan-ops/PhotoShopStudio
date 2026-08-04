using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.App.Views;

/// <summary>
/// L'accueil : trois gestes du métier, les réglages hors du chemin, et surtout le
/// RÉCEPTEUR des commandes de bornes.
///
/// Les commandes de bornes arrivaient derrière une tuile, donc derrière un clic et un
/// écran. Or c'est le flux qui tombe tout seul, sans que personne l'ait demandé : il doit
/// être visible en permanence, et se traiter d'ici — modifier, télécharger, ou tirer tel
/// quel. La couleur de la ligne dit son état, et l'avancement compte le papier qui sort.
///
/// Une commande tirée quitte la liste et va dans l'historique, où elle se conserve un mois
/// (<see cref="KioskOrderJournal.Retention"/>).
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>Période de relecture des bornes : une commande doit apparaître toute seule.</summary>
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(15);

    private readonly ObservableCollection<CommandeBorne> _lignes = [];
    private readonly DispatcherTimer _minuteur;

    public HomeView()
    {
        InitializeComponent();

        KioskList.ItemsSource = _lignes;

        _minuteur = new DispatcherTimer { Interval = Periode };
        _minuteur.Tick += (_, _) => RafraichirBornes();

        Loaded += (_, _) =>
        {
            MettreAJourAgrandissements();
            RafraichirLAttente();
            RafraichirBornes();
            BrancherSuivi();
            _minuteur.Start();
        };

        Unloaded += (_, _) =>
        {
            _minuteur.Stop();
            DebrancherSuivi();
        };
    }

    // ----- le secondaire -----

    private void MettreAJourAgrandissements()
    {
        var attente = LargeFormatQueueView.PendingCount();
        LargeFormatTitle.Text = attente > 0
            ? $"Agrandissements ({attente})"
            : "Agrandissements";
    }

    private void OnStats(object sender, RoutedEventArgs e) =>
        Navigator.Go(new StatsView(), "Statistiques");

    private void OnOrders(object sender, RoutedEventArgs e) =>
        Navigator.Go(new OrdersView(), "Commandes du jour");

    private void OnCatalog(object sender, RoutedEventArgs e) =>
        Navigator.Go(new CatalogView(), "Catalogue et imprimantes");

    private void OnMachineStatus(object sender, RoutedEventArgs e) =>
        Navigator.Go(new MachineStatusView(), "État des machines");

    /// <summary>
    /// Les réglages propres à CE poste — l'envoi par courriel pour l'instant. Séparés du
    /// Catalogue, qui décrit ce que la boutique vend et vaut pour tous les postes.
    /// </summary>
    private void OnSettings(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SettingsView(), "Paramètres");

    private void OnLargeFormatQueue(object sender, RoutedEventArgs e) =>
        Navigator.Go(new LargeFormatQueueView(), "Agrandissements à tirer");

    // ----- les trois gestes -----

    /// <summary>
    /// Les tirages, directement. L'écran « quel type de produit ? » ne posait plus qu'une
    /// question à une seule réponse utile, puisque l'identité a sa propre tuile ici.
    /// </summary>
    private void OnTirages(object sender, RoutedEventArgs e) =>
        Navigator.Go(new PrintFamilyView(), "Tirages");

    private void OnPhoneUpload(object sender, RoutedEventArgs e) =>
        Navigator.Go(new PhoneUploadView(), "Photos depuis un téléphone");

    private void OnIdPhoto(object sender, RoutedEventArgs e) =>
        Navigator.Go(new IdDocumentPickerView(
                document =>
                    Navigator.Go(new SourcePickerView((root, profond) =>
                        Navigator.Go(new IdPhotoView(root, document, profond),
                            $"{document.Country} — {document.Document}")),
                        "Photos d'identité — choisir le support"),
                // voir ProductTypeView.OnIdPhoto : l'E-Photo est un tirage, pas une norme
                produit =>
                    Navigator.Go(new SourcePickerView((root, profond) =>
                        Navigator.Go(new PhotoGridView(root, produit.Code, avecSousDossiers: profond),
                            produit.Name)),
                        $"{produit.Name} — choisir le support")),
            "Photos d'identité — choisir le document");

    // ----- les commandes mises de côté -----

    /// <summary>
    /// Ce qui attend qu'on y revienne.
    ///
    /// <b>Sur l'accueil, et non derrière une tuile.</b> C'est ici qu'on revient après avoir
    /// servi le client qui a fait patienter l'autre : une commande mise de côté qu'il
    /// faudrait aller chercher dans un écran serait une commande oubliée.
    ///
    /// Le bandeau disparaît quand il n'y a rien — un titre suivi du vide ferait croire à un
    /// écran cassé, et prendrait la place de la liste des bornes.
    /// </summary>
    private void RafraichirLAttente()
    {
        var attente = App.Services.CommandesEnAttente.Lister();

        AttenteList.ItemsSource = attente;
        AttentePanel.Visibility = attente.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AttenteTitle.Text = attente.Count == 1
            ? "En attente — 1 commande"
            : $"En attente — {attente.Count} commandes";
    }

    /// <summary>
    /// Rouvre une commande mise de côté, telle qu'elle a été laissée.
    ///
    /// L'entrée n'est PAS effacée en la reprenant : l'opérateur peut la remettre de côté
    /// aussitôt, ou fermer l'écran sans rien décider. Elle part quand la commande est
    /// imprimée, ou quand il l'abandonne.
    /// </summary>
    private void OnAttenteReprendre(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TravailEnAttente travail) return;

        if (!Directory.Exists(travail.PhotosDirectory))
        {
            MessageBox.Show(
                "Les photos de cette commande ne sont plus là.\n\n" +
                $"Elles étaient dans « {travail.PhotosDirectory} » — le support a pu être " +
                "retiré, ou le dossier effacé.",
                "En attente", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // en taille libre, le format du catalogue n'a plus cours : rouvrir sans elle
        // remettrait tous les cadres au centre, au mauvais rapport
        var taille = travail.EnTaillePersonnalisee
            ? new CustomSize(travail.CustomWidthMm, travail.CustomHeightMm, travail.PaperCode)
            : null;

        Navigator.Go(
            new PhotoGridView(
                travail.PhotosDirectory,
                taille is null ? travail.ProduitParDefaut : null,
                travail.KioskOid,
                travail.AvecSousDossiers,
                taille,
                enAttente: travail),
            $"{travail.Titre} — reprise");
    }

    private void OnAttenteAbandonner(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TravailEnAttente travail) return;

        var reponse = MessageBox.Show(
            $"Abandonner « {travail.Titre} » ({travail.Resume}) ?\n\n" +
            "Le travail mis de côté est perdu. Les photos, elles, ne sont pas touchées.",
            "En attente", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (reponse != MessageBoxResult.Yes) return;

        App.Services.CommandesEnAttente.Effacer(travail.Id);
        FileLog.Write($"Commande en attente abandonnée : {travail.Titre} — {travail.Resume}");
        RafraichirLAttente();
    }

    // ----- le récepteur des bornes -----

    private void OnKioskRefresh(object sender, RoutedEventArgs e) => RafraichirBornes();

    private void OnKioskHistory(object sender, RoutedEventArgs e) =>
        Navigator.Go(new KioskOrdersView(), "Commandes des bornes");

    /// <summary>
    /// Relit les commandes en attente et met la liste à jour SANS la reconstruire.
    ///
    /// Reconstruire ferait perdre l'avancement affiché sur une ligne en cours de tirage, et
    /// ferait sauter la liste sous la main de l'opérateur toutes les quinze secondes.
    /// </summary>
    private void RafraichirBornes()
    {
        List<DiLandOrder> commandes;
        try
        {
            commandes = App.Services.DiLandImport.Pending().ToList();
        }
        catch (Exception ex)
        {
            // DiLand absent ou dépôt illisible : le reste de l'accueil doit rester utilisable
            FileLog.Write("Accueil : lecture des commandes de bornes impossible", ex);
            KioskEmpty.Text = "Commandes des bornes indisponibles — dépôt DiLand illisible.";
            KioskEmpty.Visibility = Visibility.Visible;
            return;
        }

        var vues = commandes.Select(c => c.Oid).ToHashSet();

        // les commandes tirées ou retirées s'en vont ; celles qui impriment restent, même
        // si le journal les a déjà closes — on veut voir la barre aller jusqu'au bout
        foreach (var partie in _lignes.Where(l => !vues.Contains(l.Oid) && !l.Imprime).ToList())
            _lignes.Remove(partie);

        foreach (var commande in commandes)
        {
            if (_lignes.Any(l => l.Oid == commande.Oid)) continue;
            _lignes.Add(Construire(commande));
        }

        // remise en ordre : les plus récentes en haut, comme elles arrivent
        var ordonnees = _lignes.OrderByDescending(l => l.Quand).ToList();
        for (var i = 0; i < ordonnees.Count; i++)
        {
            var actuel = _lignes.IndexOf(ordonnees[i]);
            if (actuel != i) _lignes.Move(actuel, i);
        }

        KioskEmpty.Visibility = _lignes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        KioskTitle.Text = _lignes.Count == 0
            ? "Commandes des bornes"
            : $"Commandes des bornes ({_lignes.Count})";

        MettreAJourAgrandissements();
    }

    private CommandeBorne Construire(DiLandOrder commande)
    {
        var importateur = App.Services.DiLandImport;
        var resume = importateur.Summarize(commande);

        return new CommandeBorne(commande, resume);
    }

    // ----- suivi de l'impression, pour colorer et faire avancer la barre -----

    private void BrancherSuivi()
    {
        var suivi = App.Services.Impressions;
        ((INotifyCollectionChanged)suivi.Travaux).CollectionChanged += OnTravauxChanged;
        foreach (var travail in suivi.Travaux) travail.PropertyChanged += OnTravailChanged;
    }

    private void DebrancherSuivi()
    {
        var suivi = App.Services.Impressions;
        ((INotifyCollectionChanged)suivi.Travaux).CollectionChanged -= OnTravauxChanged;
        foreach (var travail in suivi.Travaux) travail.PropertyChanged -= OnTravailChanged;
    }

    private void OnTravauxChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (TravailImpression travail in e.OldItems ?? Array.Empty<object>())
        {
            travail.PropertyChanged -= OnTravailChanged;

            // Le travail quitte la liste sans que la commande ait été close : impression
            // échouée, ou arrêtée par l'opérateur. La ligne doit redevenir normale, sans
            // quoi elle resterait orange et sans boutons pour toujours — impossible de
            // relancer la commande.
            foreach (var ligne in _lignes.Where(l => l.SuitCeTravail(travail) && !l.Close).ToList())
                ligne.AbandonnerImpression();
        }

        foreach (TravailImpression travail in e.NewItems ?? Array.Empty<object>())
            travail.PropertyChanged += OnTravailChanged;
    }

    private void OnTravailChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TravailImpression travail) return;
        Dispatcher.Invoke(() =>
        {
            foreach (var ligne in _lignes.Where(l => l.SuitCeTravail(travail)))
                ligne.Rafraichir();
        });
    }

    // ----- les trois actions d'une ligne -----

    /// <summary>
    /// Ouvre les photos pour les recadrer et les corriger — exactement le module de
    /// « Tirages », avec les mêmes fonctions.
    /// </summary>
    private void OnKioskModify(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CommandeBorne ligne)
            OuvertureBorne.Ouvrir(ligne.Order, taille: null);
    }

    /// <summary>
    /// Abandonne ce qui attend et repart du cadrage que le client a validé à la borne.
    /// Doublé de l'écran des bornes : les deux listes doivent porter les mêmes actions.
    /// </summary>
    private void OnKioskDiscardDraft(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandeBorne ligne) return;
        if (!OuvertureBorne.RepartirDeZero(ligne.Order)) return;

        RafraichirLAttente();
        RafraichirBornes();
    }

    /// <summary>
    /// Ouvre la commande dans une taille qui n'est pas au catalogue.
    ///
    /// Les bornes ne proposent que des formats standard : un client qui veut du 5,5 × 8 cm
    /// commande donc du 10×15 et le dit au comptoir. On demande la taille, puis ses photos
    /// s'ouvrent directement dedans.
    /// </summary>
    private void OnKioskCustom(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandeBorne ligne) return;

        Navigator.Go(new CustomSizeView(taille => OuvertureBorne.Ouvrir(ligne.Order, taille)),
            "Taille personnalisée");
    }

    /// <summary>
    /// Copie les photos de la commande dans les téléchargements, sous un nom qui parle.
    ///
    /// C'est la porte de sortie quand il faut faire autre chose des photos que les tirer :
    /// les graver, les envoyer, les ouvrir dans Photoshop.
    /// </summary>
    private void OnKioskDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandeBorne ligne) return;

        var telechargements = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (!Directory.Exists(telechargements))
            telechargements = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var nom = $"Borne-{ligne.Order.Number}-{ligne.Order.Date:yyyy-MM-dd-HHmm}";

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var prete = App.Services.DiLandImport.Stage(
                ligne.Order, telechargements, nom, ecraser: true);
            Mouse.OverrideCursor = null;

            if (prete.PhotoCount == 0)
            {
                MessageBox.Show("Aucune photo n'a pu être récupérée pour cette commande.",
                    "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // on ouvre le dossier : sans cela l'opérateur doit aller le chercher, et rien
            // à l'écran ne lui dit où il est
            Process.Start(new ProcessStartInfo(prete.PhotosDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Accueil : téléchargement d'une commande de borne impossible", ex);
            MessageBox.Show($"Téléchargement impossible : {ex.Message}",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Retire une commande de la liste.
    ///
    /// « Supprimer » ne supprime RIEN chez DiLand ni sur la borne : on lit une copie de
    /// sa base et on n'y écrit jamais, et les photos du client restent en place. La
    /// commande passe simplement dans notre historique, d'où elle peut être remise dans la
    /// liste. C'est la porte de sortie pour une commande que DiLand a déjà tirée, ou que
    /// le client a abandonnée — sans elle, elle resterait affichée indéfiniment.
    /// </summary>
    private void OnKioskDismiss(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandeBorne ligne) return;

        var reponse = MessageBox.Show(
            $"Retirer la commande #{ligne.Order.Number} de la liste ?\n\n" +
            $"{ligne.Resume.PhotoCount} photo(s), {ligne.Resume.Total:0.00} €.\n\n" +
            "Elle passera dans l'historique, d'où vous pourrez la remettre. Les photos du " +
            "client et la commande dans DiLand ne sont pas touchées.",
            "Commandes des bornes", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (reponse != MessageBoxResult.Yes) return;

        try
        {
            App.Services.DiLandImport.Dismiss(ligne.Order);
            FileLog.Write($"Borne #{ligne.Order.Number} retirée de la liste par l'opérateur");
        }
        catch (Exception ex)
        {
            FileLog.Write("Accueil : retrait d'une commande de borne impossible", ex);
            MessageBox.Show($"Retrait impossible : {ex.Message}",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RafraichirBornes();
    }

    /// <summary>
    /// Tire la commande telle que la borne l'a composée : produits, quantités et
    /// recadrages du client, sans passer par l'écran des photos.
    ///
    /// C'est le cas courant — le client a déjà tout choisi à la borne. On demande quand
    /// même confirmation : le geste engage du papier et ne se reprend pas.
    /// </summary>
    private void OnKioskPrint(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandeBorne ligne) return;

        var reponse = MessageBox.Show(
            $"Tirer la commande #{ligne.Order.Number} telle quelle ?\n\n" +
            $"{ligne.Resume.PhotoCount} photo(s), {ligne.Resume.PrintCount} tirage(s), " +
            $"{ligne.Resume.Total:0.00} €.\n\n" +
            "Les recadrages faits à la borne sont conservés. Rien ne sera retouché.",
            "Commandes des bornes", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (reponse != MessageBoxResult.Yes) return;

        var services = App.Services;
        DiLandImportOutcome resultat;
        try
        {
            resultat = services.DiLandImport.Import(ligne.Order);
        }
        catch (Exception ex)
        {
            FileLog.Write("Accueil : reprise d'une commande de borne impossible", ex);
            MessageBox.Show($"Reprise impossible : {ex.Message}",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (resultat.Created is not { } commande)
        {
            MessageBox.Show(
                "Cette commande n'a pas pu être reprise :\n" + string.Join("\n", resultat.Warnings),
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
            RafraichirBornes();
            return;
        }

        if (resultat.Warnings.Count > 0)
            FileLog.Write($"Borne #{ligne.Order.Number} reprise avec réserves : " +
                          string.Join(" ; ", resultat.Warnings));

        var oid = ligne.Oid;
        ligne.CommencerImpression(commande.DisplayNumber);

        services.Impressions.Lancer(commande,
            imprimer: (avancement, arret) =>
            {
                foreach (var enveloppe in commande.Envelopes)
                    services.Printer.PrintEnvelope(commande, enveloppe,
                        progression: avancement, ct: arret);
            },
            apresSucces: () =>
            {
                // seul un tirage réellement sorti ferme la commande de borne
                if (commande.Envelopes.All(env => env.Status == EnvelopeStatus.Printed))
                    services.DiLandImport.MarkPrinted(oid, commande.Id);

                ligne.TerminerImpression();
                RafraichirBornes();
            });
    }

    /// <summary>
    /// Une commande de borne, telle que la ligne l'affiche — avec son état, qui change
    /// pendant que la machine tire.
    /// </summary>
    private sealed class CommandeBorne : ObservableObject
    {
        private string? _numeroStudio;
        private bool _termine;

        public CommandeBorne(DiLandOrder order, DiLandImporter.KioskOrderSummary resume)
        {
            Order = order;
            Resume = resume;
        }

        public DiLandOrder Order { get; }
        public DiLandImporter.KioskOrderSummary Resume { get; }

        public long Oid => Order.Oid;
        public DateTime Quand => Order.Date;

        public string Numero => $"#{Order.Number}";

        public string Titre
        {
            get
            {
                var client = string.IsNullOrWhiteSpace(Order.EndUserName) ? "" : $" · {Order.EndUserName}";
                return $"{Resume.PhotoCount} photo(s) · {Resume.PrintCount} tirage(s)" +
                       $" · {Resume.Total:0.00} €{client}";
            }
        }

        public string Detail =>
            $"{Order.Date:dd/MM à HH:mm} — {string.Join(", ", Resume.Lines.Take(4))}";

        /// <summary>Le travail d'impression qui porte cette commande, s'il tourne encore.</summary>
        private TravailImpression? Travail =>
            _numeroStudio is null ? null : App.Services.Impressions.Travaux
                .FirstOrDefault(t => t.Numero == _numeroStudio);

        public bool SuitCeTravail(TravailImpression travail) =>
            _numeroStudio is not null && travail.Numero == _numeroStudio;

        /// <summary>Vrai dès que le tirage est lancé : la ligne reste jusqu'à la dernière photo.</summary>
        public bool Imprime => _numeroStudio is not null && !_termine;

        /// <summary>Bleu au repos, orange dès que la machine travaille.</summary>
        public Brush Fond => Imprime
            ? new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x1B, 0x4F, 0x72));

        /// <summary>Une commande qu'on tire déjà ne se modifie plus et ne se relance pas.</summary>
        public bool ActionsPossibles => !Imprime;

        /// <summary>
        /// Ce qui attend au nom de cette commande de borne, s'il y a quelque chose.
        ///
        /// Relu à chaque construction de ligne, et non gardé : la liste se rafraîchit
        /// toutes les quinze secondes, et la commande a pu être mise de côté depuis l'écran
        /// des photos entre-temps.
        /// </summary>
        private TravailEnAttente? EnAttente => App.Services.CommandesEnAttente.PourLaBorne(Oid);

        /// <summary>« Modifier » d'ordinaire, « Reprendre » quand la commande attend.</summary>
        public string ModifierLabel => EnAttente is null ? "Modifier" : "Reprendre";

        public string AttenteLabel => EnAttente is { } travail ? travail.Depuis.ToUpperInvariant() : "";

        public Visibility AttenteVisible =>
            EnAttente is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility AvancementVisible => Imprime ? Visibility.Visible : Visibility.Collapsed;

        public string AvancementTexte
        {
            get
            {
                if (Travail is not { } t) return _termine ? "Terminée" : "Préparation…";

                return t.Etape.StartsWith("Tirage", StringComparison.Ordinal)
                    ? $"{t.Sortis} / {t.Total} photo(s) sorties de l'imprimante"
                    : $"{t.Etape} {t.Detail}";
            }
        }

        public double Fraction => Travail?.Fraction ?? (_termine ? 1 : 0);

        public bool Indetermine => Travail is { Total: <= 0 };

        /// <summary>Vrai quand le tirage est allé à son terme : la ligne part à l'historique.</summary>
        public bool Close => _termine;

        public void CommencerImpression(string numeroStudio)
        {
            _numeroStudio = numeroStudio;
            _termine = false;
            Rafraichir();
        }

        public void TerminerImpression()
        {
            _termine = true;
            Rafraichir();
        }

        /// <summary>
        /// L'impression n'est pas allée au bout — échec, ou arrêt demandé. La commande
        /// redevient une commande à faire : elle reprend sa couleur et ses boutons.
        /// </summary>
        public void AbandonnerImpression()
        {
            _numeroStudio = null;
            _termine = false;
            Rafraichir();
        }

        public void Rafraichir()
        {
            OnPropertyChanged(nameof(Fond));
            OnPropertyChanged(nameof(Imprime));
            OnPropertyChanged(nameof(ActionsPossibles));
            OnPropertyChanged(nameof(AvancementVisible));
            OnPropertyChanged(nameof(AvancementTexte));
            OnPropertyChanged(nameof(Fraction));
            OnPropertyChanged(nameof(Indetermine));
        }
    }
}
